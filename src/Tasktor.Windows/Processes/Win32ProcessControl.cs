using System.Runtime.InteropServices;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Snapshot;
using Tasktor.Core.Features.Jobs;
using Tasktor.Core.Plugins;

namespace Tasktor.Windows.Processes;

/// <summary>
/// <see cref="IProcessControl"/> on Win32 (A3, B3, B3.7, D-H9). Every call re-checks (PID, creation time) right before it
/// acts; nothing is elevated. Freeze suspends every thread of the tree (repeating the thread enumeration until a pass finds
/// no new thread), writing each suspended thread to the <see cref="FrozenRegistry"/> first, and Thaw resumes exactly those
/// threads once each, clearing the entry last. No undocumented <c>NtSuspendProcess</c>.
/// </summary>
public sealed partial class Win32ProcessControl(ChildProcessRunner runner, FrozenRegistry frozen, Func<DateTimeOffset> now) : PortableProcessControl(runner)
{
    private const uint ProcessTerminate = 0x1;
    private const uint ProcessSetInformation = 0x200;
    private const uint ProcessSetQuota = 0x100;
    private const uint ProcessQueryLimited = 0x1000;
    private const uint ThreadSuspendResume = 0x2;
    private const uint Th32csSnapThread = 0x4;
    private const uint WmClose = 0x10;

    private readonly Dictionary<string, JobObject> _caps = new(StringComparer.Ordinal);
    private readonly Lock _capsGate = new();

    public override IReadOnlyList<ProcessOutcome> End(IReadOnlyList<ProcKey> keys) =>
        keys.Select(k => WithProcess(k, ProcessTerminate, h => TerminateProcess(h, 1) ? null : "Access denied")).ToList();

    public override IReadOnlyList<ProcessOutcome> Close(IReadOnlyList<ProcKey> keys)
    {
        var outcomes = new List<ProcessOutcome>();
        foreach (var k in keys)
        {
            var check = WithProcess(k, 0, _ => null);
            if (check.Status != ActionStatus.Ok)
            {
                outcomes.Add(check);
                continue;
            }

            var windows = TopLevelWindows(k.Pid);
            foreach (var hwnd in windows)
            {
                PostMessageW(hwnd, WmClose, 0, 0); // graceful: the app may still ask to save (A3.0)
            }

            outcomes.Add(new ProcessOutcome(k, windows.Count > 0 ? ActionStatus.Ok : ActionStatus.Failed, windows.Count > 0 ? null : "No window to close"));
        }

        return outcomes;
    }

    public override IReadOnlyList<(ProcessOutcome Outcome, ProcessPriority Previous)> SetPriority(IReadOnlyList<ProcKey> keys, ProcessPriority priority)
    {
        var list = new List<(ProcessOutcome, ProcessPriority)>();
        foreach (var k in keys)
        {
            var prev = ProcessPriority.Unknown;
            var o = WithProcess(k, ProcessSetInformation, h =>
            {
                prev = Map(GetPriorityClass(h));
                return SetPriorityClass(h, Class(priority)) ? null : "Access denied";
            });
            list.Add((o, prev));
        }

        return list;
    }

    public override IReadOnlyList<ProcessOutcome> SetEfficiency(IReadOnlyList<ProcKey> keys, bool on) =>
        keys.Select(k => WithProcess(k, ProcessSetInformation, h => Native.SetEcoQoS(h, on) ? null : "Access denied")).ToList();

    public override IReadOnlyList<ProcessOutcome> Freeze(string jobId, IReadOnlyList<ProcKey> tree)
    {
        var alive = tree.Where(k => WithProcess(k, 0, _ => null).Status == ActionStatus.Ok).ToList();
        frozen.Begin(jobId, alive, now()); // intent first: a crash from here on is recovered at the next start
        var pids = alive.Select(k => k.Pid).ToHashSet();
        var suspended = new HashSet<uint>();
        var denied = new HashSet<uint>();
        for (var pass = 0; pass < 8; pass++)
        {
            var found = 0;
            foreach (var (pid, tid) in Threads(pids))
            {
                if (suspended.Contains(tid))
                {
                    continue;
                }

                var th = OpenThread(ThreadSuspendResume, false, tid);
                if (th == 0)
                {
                    denied.Add(pid);
                    continue;
                }

                try
                {
                    if (SuspendThread(th) != uint.MaxValue)
                    {
                        frozen.AddThread(jobId, pid, tid);
                        suspended.Add(tid);
                        found++;
                    }
                }
                finally
                {
                    CloseHandle(th);
                }
            }

            if (found == 0)
            {
                break; // a pass that finds no new running thread
            }
        }

        var byPid = frozen.Find(jobId)?.Threads.GroupBy(t => t.Pid).ToDictionary(g => g.Key, g => g.Count()) ?? [];
        return tree.Select(k => !pids.Contains(k.Pid)
            ? new ProcessOutcome(k, ActionStatus.Failed, "Already exited")
            : byPid.ContainsKey(k.Pid) ? new ProcessOutcome(k, ActionStatus.Ok) : new ProcessOutcome(k, ActionStatus.Refused, denied.Contains(k.Pid) ? "Access denied" : "No threads")).ToList();
    }

    public override IReadOnlyList<ProcessOutcome> Thaw(string jobId, IReadOnlyList<ProcKey> tree)
    {
        var entry = frozen.Find(jobId);
        if (entry is null)
        {
            return tree.Select(k => new ProcessOutcome(k, ActionStatus.Failed, "Not frozen")).ToList();
        }

        var resumed = new HashSet<uint>();
        foreach (var t in entry.Threads)
        {
            var th = OpenThread(ThreadSuspendResume, false, t.Tid);
            if (th == 0)
            {
                continue; // the thread or the process is gone
            }

            try
            {
                if (ResumeThread(th) != uint.MaxValue)
                {
                    resumed.Add(t.Pid); // once each: only the threads Tasktor suspended
                }
            }
            finally
            {
                CloseHandle(th);
            }
        }

        frozen.Remove(jobId); // cleared last
        return tree.Select(k => new ProcessOutcome(k, resumed.Contains(k.Pid) ? ActionStatus.Ok : ActionStatus.Failed, resumed.Contains(k.Pid) ? null : "Already exited")).ToList();
    }

    public override IReadOnlyList<string> RecoverFrozen() => frozen.Recover((id, keys) => Thaw(id, keys));

    public override (ProcessOutcome Outcome, long Before, long After) TrimMemory(ProcKey key)
    {
        long before = 0, after = 0;
        var o = WithProcess(key, ProcessSetQuota, h =>
        {
            before = WorkingSet(h);
            var ok = EmptyWorkingSet(h);
            after = WorkingSet(h);
            return ok ? null : "Access denied";
        });
        return (o, before, after);
    }

    public override IReadOnlyList<ProcessOutcome> CapCores(string capId, IReadOnlyList<ProcKey> keys, int maxCores)
    {
        lock (_capsGate)
        {
            if (maxCores <= 0)
            {
                if (_caps.Remove(capId, out var old))
                {
                    old.ReleaseCap();
                    old.Dispose();
                }

                return keys.Select(k => new ProcessOutcome(k, ActionStatus.Ok)).ToList();
            }

            if (!_caps.TryGetValue(capId, out var job))
            {
                job = new JobObject(0, killOnClose: false);
                _caps[capId] = job;
            }

            job.CapCores(maxCores);
            return keys.Select(k => WithProcess(k, ProcessSetQuota | ProcessTerminate, h => AssignProcessToJobObject(job, h) ? null : "Access denied")).ToList();
        }
    }

    private static bool AssignProcessToJobObject(JobObject job, nint h)
    {
        using var p = System.Diagnostics.Process.GetProcessById(GetProcessId(h));
        return job.Assign(p);
    }

    /// <summary>Open with the extra rights, re-check (PID, creation time), act.</summary>
    private static ProcessOutcome WithProcess(ProcKey key, uint rights, Func<nint, string?> act)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new ProcessOutcome(key, ActionStatus.Refused, "Not on this platform");
        }

        var h = OpenProcess(ProcessQueryLimited | rights, false, key.Pid);
        if (h == 0)
        {
            return new ProcessOutcome(key, ActionStatus.Refused, Marshal.GetLastPInvokeError() == 5 ? "Access denied" : "Already exited");
        }

        try
        {
            if (!GetProcessTimes(h, out var created, out var exit, out _, out _) || (key.CreatedTicks != 0 && created != key.CreatedTicks))
            {
                return new ProcessOutcome(key, ActionStatus.Refused, "Process changed, refresh");
            }

            if (exit != 0)
            {
                return new ProcessOutcome(key, ActionStatus.Failed, "Already exited");
            }

            var reason = act(h);
            return new ProcessOutcome(key, reason is null ? ActionStatus.Ok : ActionStatus.Refused, reason);
        }
        finally
        {
            CloseHandle(h);
        }
    }

    private static unsafe List<(uint Pid, uint Tid)> Threads(HashSet<uint> pids)
    {
        var list = new List<(uint, uint)>();
        var snap = CreateToolhelp32Snapshot(Th32csSnapThread, 0);
        if (snap == -1)
        {
            return list;
        }

        try
        {
            var te = new ThreadEntry32 { Size = (uint)sizeof(ThreadEntry32) };
            if (Thread32First(snap, ref te))
            {
                do
                {
                    if (pids.Contains(te.OwnerProcessId))
                    {
                        list.Add((te.OwnerProcessId, te.ThreadId));
                    }
                }
                while (Thread32Next(snap, ref te));
            }
        }
        finally
        {
            CloseHandle(snap);
        }

        return list;
    }

    private static List<nint> TopLevelWindows(uint pid)
    {
        var found = new List<nint>();
        var gc = GCHandle.Alloc((pid, found));
        try
        {
            unsafe
            {
                EnumWindows(&EnumProc, GCHandle.ToIntPtr(gc));
            }
        }
        finally
        {
            gc.Free();
        }

        return found;
    }

    [UnmanagedCallersOnly]
    private static int EnumProc(nint hwnd, nint state)
    {
        var (pid, found) = ((uint, List<nint>))GCHandle.FromIntPtr(state).Target!;
        GetWindowThreadProcessId(hwnd, out var owner);
        if (owner == pid && IsWindowVisible(hwnd))
        {
            found.Add(hwnd);
        }

        return 1;
    }

    private static unsafe long WorkingSet(nint h)
    {
        var c = new long[10];
        fixed (long* p = c)
        {
            return GetProcessMemoryInfo(h, p, 80) ? c[2] : 0; // WorkingSetSize
        }
    }

    private static ProcessPriority Map(uint c) => c switch
    {
        0x40 => ProcessPriority.Idle,
        0x4000 => ProcessPriority.BelowNormal,
        0x20 => ProcessPriority.Normal,
        0x8000 => ProcessPriority.AboveNormal,
        0x80 => ProcessPriority.High,
        0x100 => ProcessPriority.Realtime,
        _ => ProcessPriority.Unknown,
    };

    private static uint Class(ProcessPriority p) => p switch
    {
        ProcessPriority.Idle => 0x40,
        ProcessPriority.BelowNormal => 0x4000,
        ProcessPriority.AboveNormal => 0x8000,
        ProcessPriority.High => 0x80,
        _ => 0x20,
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct ThreadEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ThreadId;
        public uint OwnerProcessId;
        public int BasePri;
        public int DeltaPri;
        public uint Flags;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint h);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(nint h, uint code);

    [LibraryImport("kernel32.dll")]
    private static partial int GetProcessId(nint h);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(nint h, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetPriorityClass(nint h);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetPriorityClass(nint h, uint priority);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateToolhelp32Snapshot(uint flags, uint pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Thread32First(nint snapshot, ref ThreadEntry32 entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Thread32Next(nint snapshot, ref ThreadEntry32 entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenThread(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint tid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint SuspendThread(nint thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint ResumeThread(nint thread);

    [LibraryImport("kernel32.dll", EntryPoint = "K32EmptyWorkingSet", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyWorkingSet(nint h);

    [LibraryImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetProcessMemoryInfo(nint h, long* counters, uint size);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool EnumWindows(delegate* unmanaged<nint, nint, int> proc, nint state);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint hwnd, uint msg, nint wparam, nint lparam);
}
