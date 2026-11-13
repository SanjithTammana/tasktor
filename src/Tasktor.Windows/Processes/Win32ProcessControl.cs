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
/// no new thread) through <see cref="FreezeEngine"/>, recording each thread in the <see cref="FrozenRegistry"/> before it
/// is suspended, and Thaw resumes exactly those threads once each, clearing the entry last. No undocumented <c>NtSuspendProcess</c>.
/// </summary>
public sealed partial class Win32ProcessControl(ChildProcessRunner runner, FrozenRegistry frozen, Func<DateTimeOffset> now, string? capsPath = null)
    : PortableProcessControl(runner)
{
    private const uint ProcessTerminate = 0x1;
    private const uint ProcessSetInformation = 0x200;
    private const uint ProcessSetQuota = 0x100;
    private const uint ProcessQueryLimited = 0x1000;
    private const uint ProcessDupHandle = 0x40;
    private const uint ThreadSuspendResume = 0x2;
    private const uint ThreadQueryLimited = 0x800;
    private const uint Th32csSnapThread = 0x4;
    private const uint WmClose = 0x10;

    private const string CapPrefix = @"Local\Tasktor.Cap.";

    private readonly Dictionary<string, JobObject> _caps = new(StringComparer.Ordinal);
    private readonly Lock _capsGate = new();

    // Cap jobs a previous run created (named, so they can be opened again); released at start and on quit (F-PC5).
    private readonly HashSet<string> _leftoverCaps = LoadCapNames(capsPath);

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

    // One engine for the agent's life: its per-job locks are what keep a freeze and a thaw of one job apart.
    private readonly FreezeEngine _engine = new(frozen, new Win32Threads(), now);

    public override IReadOnlyList<ProcessOutcome> Freeze(string jobId, IReadOnlyList<ProcKey> tree) => _engine.Freeze(jobId, tree);

    public override IReadOnlyList<ProcessOutcome> Thaw(string jobId, IReadOnlyList<ProcKey> tree) => _engine.Thaw(jobId, tree);

    public override IReadOnlyList<string> RecoverFrozen()
    {
        var ids = _engine.Recover();
        lock (_capsGate)
        {
            var live = _caps.Keys.Select(CapName).ToHashSet(StringComparer.Ordinal);
            foreach (var name in _leftoverCaps.Where(n => !live.Contains(n)))
            {
                JobObject.ReleaseNamed(name);
            }

            _leftoverCaps.Clear();
            SaveCapNames();
        }

        return ids;
    }

    /// <summary>Close every cap handle without lifting the caps, as a crash would (tests).</summary>
    internal void AbandonCaps()
    {
        lock (_capsGate)
        {
            foreach (var job in _caps.Values)
            {
                job.Abandon();
            }

            _caps.Clear();
        }
    }

    public override void ReleaseOnQuit()
    {
        _engine.Recover(); // every frozen entry, whoever froze it (F-PC7)
        lock (_capsGate)
        {
            foreach (var job in _caps.Values)
            {
                job.ReleaseCap();
                job.Dispose();
            }

            foreach (var name in _leftoverCaps)
            {
                JobObject.ReleaseNamed(name);
            }

            _caps.Clear();
            _leftoverCaps.Clear();
            SaveCapNames();
        }
    }

    /// <summary>The Win32 thread calls behind <see cref="FreezeEngine"/>.</summary>
    private sealed class Win32Threads : IThreadApi
    {
        public bool IsAlive(ProcKey key) => WithProcess(key, 0, _ => null).Status == ActionStatus.Ok;

        public IEnumerable<(uint Pid, uint Tid)> Threads(IReadOnlySet<uint> pids) => Win32ProcessControl.Threads(pids);

        public IOpenThread? Open(uint tid, ProcKey owner, out bool denied)
        {
            denied = false;
            var th = OpenThread(ThreadSuspendResume | ThreadQueryLimited, false, tid);
            if (th == 0)
            {
                denied = Marshal.GetLastPInvokeError() == 5;
                return null;
            }

            // The open handle pins the thread: its id cannot be reused while we hold it. The id came from an enumeration
            // that may be stale, so check it still names a thread of the process we mean.
            if (!Owns(th, owner) || !GetThreadTimes(th, out var created, out _, out _, out _))
            {
                CloseHandle(th);
                return null;
            }

            return new OpenedThread(th, created);
        }

        public ResumeResult Resume(uint tid, ProcKey owner, long threadCreated)
        {
            var th = OpenThread(ThreadSuspendResume | ThreadQueryLimited, false, tid);
            if (th == 0)
            {
                // Access denied: the thread may well still be suspended, so keep its record. Anything else: it is gone.
                return Marshal.GetLastPInvokeError() == 5 ? ResumeResult.Failed : ResumeResult.Gone;
            }

            try
            {
                // Same owner process (pid and creation time) and, when recorded, the same thread (its own creation time):
                // a reused id in the same process is another thread, which may be suspended on purpose.
                if (!Owns(th, owner) || (threadCreated != 0 && (!GetThreadTimes(th, out var created, out _, out _, out _) || created != threadCreated)))
                {
                    return ResumeResult.Gone;
                }

                return ResumeThread(th) != uint.MaxValue ? ResumeResult.Resumed : ResumeResult.Failed;
            }
            finally
            {
                CloseHandle(th);
            }
        }

        private sealed class OpenedThread(nint handle, long created) : IOpenThread
        {
            private nint _handle = handle;

            public long Created { get; } = created;

            public bool Suspend() => _handle != 0 && SuspendThread(_handle) != uint.MaxValue;

            public void Dispose()
            {
                if (_handle != 0)
                {
                    CloseHandle(_handle);
                    _handle = 0;
                }
            }
        }

        /// <summary>The opened thread belongs to <paramref name="owner"/>: same pid, and that process has the recorded creation
        /// time (0 = pid only). The thread handle keeps its process from being freed, so the pid cannot be reused meanwhile.</summary>
        private static bool Owns(nint thread, ProcKey owner) =>
            GetProcessIdOfThread(thread) == owner.Pid && (owner.CreatedTicks == 0 || WithProcess(owner, 0, _ => null).Status == ActionStatus.Ok);
    }

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
                    SaveCapNames();
                }

                return keys.Select(k => new ProcessOutcome(k, ActionStatus.Ok)).ToList();
            }

            if (!_caps.TryGetValue(capId, out var job))
            {
                // Named and written down before any process joins: a cap outlives the agent (Windows keeps a job while a
                // process is in it), so a restarted agent must be able to open it and lift the cap.
                job = new JobObject(0, killOnClose: false, name: CapName(capId));
                _caps[capId] = job;
                SaveCapNames();
            }

            job.CapCores(maxCores);
            return keys.Select(k => WithProcess(k, ProcessSetQuota | ProcessTerminate | ProcessDupHandle, h =>
            {
                if (!AssignProcessToJobObject(job, h))
                {
                    return "Access denied";
                }

                job.ShareWith(h); // keeps the job's name alive for a restarted agent; without it only a crash recovery is lost
                return null;
            })).ToList();
        }
    }

    /// <summary>A per-session name for a cap's job: the cap id hashed (ids contain targets, names must not).</summary>
    public static string CapName(string capId) =>
        CapPrefix + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(capId)), 0, 16);

    private static HashSet<string> LoadCapNames(string? path)
    {
        try
        {
            return path is not null && File.Exists(path)
                ? File.ReadAllLines(path).Where(l => l.StartsWith(CapPrefix, StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
        }
        catch (IOException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    /// <summary>Under <see cref="_capsGate"/>: the live caps' names plus the leftovers not yet released.</summary>
    private void SaveCapNames()
    {
        if (capsPath is null)
        {
            return;
        }

        var names = _caps.Keys.Select(CapName).Concat(_leftoverCaps).Distinct(StringComparer.Ordinal);
        Core.Storage.AtomicFile.Write(capsPath, System.Text.Encoding.UTF8.GetBytes(string.Join('\n', names)));
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

    private static unsafe List<(uint Pid, uint Tid)> Threads(IReadOnlySet<uint> pids)
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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetProcessIdOfThread(nint thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetThreadTimes(nint thread, out long creation, out long exit, out long kernel, out long user);
}
