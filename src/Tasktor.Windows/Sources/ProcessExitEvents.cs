using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Tasktor.Contracts.Platform;

namespace Tasktor.Windows.Sources;

/// <summary>
/// B D-B4 exits as push events: one handle (<c>PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE</c>) and a one-shot
/// <c>RegisterWaitForSingleObject</c> per watched job; the exit code is read after the wait fires (so a real 259 is not
/// confused with STILL_ACTIVE). The wait is unregistered before its handle is closed. A key already gone exits at once.
/// </summary>
public sealed unsafe partial class ProcessExitEvents : IEventSource<ProcessExit>
{
    private const uint Synchronize = 0x00100000;
    private const uint QueryLimited = 0x1000;
    private const uint WtExecuteOnlyOnce = 0x8;

    private readonly ConcurrentDictionary<ProcKey, Waiting> _watches = new();
    private Action<ProcessExit>? _post;

    public string Name => "process-exits";

    public int Watching => _watches.Count;

    public void Start(Action<ProcessExit> post) => _post = post;

    public void Stop()
    {
        foreach (var k in _watches.Keys.ToList())
        {
            Unwatch(k);
        }

        _post = null;
    }

    public void Watch(ProcKey key)
    {
        if (!OperatingSystem.IsWindows() || _watches.ContainsKey(key))
        {
            return;
        }

        var h = OpenProcess(QueryLimited | Synchronize, false, key.Pid);
        if (h == 0 || !GetProcessTimes(h, out var created, out _, out _, out _) || (key.CreatedTicks != 0 && created != key.CreatedTicks))
        {
            if (h != 0)
            {
                CloseHandle(h);
            }

            _post?.Invoke(new ProcessExit(key, null, DateTimeOffset.UtcNow)); // already gone
            return;
        }

        var w = new Waiting(this, key, h);
        _watches[key] = w;
        var gc = GCHandle.Alloc(w);
        w.Gc = gc;
        if (!RegisterWaitForSingleObject(out var wait, h, &Fired, GCHandle.ToIntPtr(gc), uint.MaxValue, WtExecuteOnlyOnce))
        {
            Remove(key);
            return;
        }

        w.Wait = wait;
    }

    public void Unwatch(ProcKey key) => Remove(key);

    private void Remove(ProcKey key)
    {
        if (_watches.TryRemove(key, out var w))
        {
            if (w.Wait != 0)
            {
                UnregisterWait(w.Wait); // before closing the handle (closing while pending is undefined)
            }

            CloseHandle(w.Handle);
            if (w.Gc.IsAllocated)
            {
                w.Gc.Free();
            }
        }
    }

    [UnmanagedCallersOnly]
    private static void Fired(nint context, byte timedOut)
    {
        var w = (Waiting)GCHandle.FromIntPtr(context).Target!;
        int? code = GetExitCodeProcess(w.Handle, out var c) ? (int)c : null;
        var owner = w.Owner;
        var key = w.Key;
        owner._post?.Invoke(new ProcessExit(key, code, DateTimeOffset.UtcNow));
        ThreadPool.QueueUserWorkItem(_ => owner.Remove(key)); // not from inside the wait callback
    }

    private sealed class Waiting(ProcessExitEvents owner, ProcKey key, nint handle)
    {
        public ProcessExitEvents Owner { get; } = owner;

        public ProcKey Key { get; } = key;

        public nint Handle { get; } = handle;

        public nint Wait { get; set; }

        public GCHandle Gc { get; set; }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint h);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(nint h, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(nint h, out uint code);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterWaitForSingleObject(out nint wait, nint handle, delegate* unmanaged<nint, byte, void> callback, nint context, uint ms, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterWait(nint wait);
}

/// <summary>Command lines (B D-B8: read once per (PID, creation time)) and the foreground window's process (B2.5).</summary>
public static unsafe partial class ProcessInfo
{
    private const uint QueryLimited = 0x1000;
    private const int ProcessCommandLineInformation = 60;

    /// <summary><c>NtQueryInformationProcess(ProcessCommandLineInformation)</c> (Windows 8.1+, query-limited right). Null when unreadable (then the gate uses the exe name only).</summary>
    public static string? CommandLine(ProcKey key)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var h = OpenProcess(QueryLimited, false, key.Pid);
        if (h == 0)
        {
            return null;
        }

        try
        {
            var size = 0u;
            NtQueryInformationProcess(h, ProcessCommandLineInformation, null, 0, out size);
            if (size == 0 || size > 1 << 20)
            {
                return null;
            }

            var buf = new byte[size];
            fixed (byte* p = buf)
            {
                if (NtQueryInformationProcess(h, ProcessCommandLineInformation, p, size, out _) != 0)
                {
                    return null;
                }

                // UNICODE_STRING { USHORT Length; USHORT MaximumLength; PWSTR Buffer; }
                var length = *(ushort*)p;
                var text = *(nint*)(p + 8);
                return text == 0 ? null : new string((char*)text, 0, length / 2);
            }
        }
        finally
        {
            CloseHandle(h);
        }
    }

    /// <summary>The foreground window's process id (one <c>GetForegroundWindow</c>), or 0.</summary>
    public static uint ForegroundPid()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        var hwnd = GetForegroundWindow();
        if (hwnd == 0)
        {
            return 0;
        }

        GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(nint process, int infoClass, byte* info, uint length, out uint returned);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint h);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);
}
