using System.Runtime.InteropServices;
using Tasktor.Capture.Deep;
using Tasktor.Contracts.Presets;
using Tasktor.Core.Windows;
using Tasktor.Windows.Shell;

namespace Tasktor.Capture.Visibility;

/// <summary>The live desktop for <see cref="VisibilityProducer"/>: geometry and app only, never a title.</summary>
public sealed partial class Win32WindowGeometry : IWindowGeometry
{
    public const int MaxCachedProcesses = 512;
    private readonly Dictionary<uint, string?> _exes = [];

    public IReadOnlyList<RectPx> Monitors() => Win32Windows.MonitorRects();

    public IReadOnlyList<WindowInfo> Windows()
    {
        if (_exes.Count > MaxCachedProcesses) _exes.Clear(); // pids are reused: a bounded cache, refilled as needed
        return Win32Windows.EnumerateGeometry(_exes);
    }

    public long Foreground() => OperatingSystem.IsWindows() ? GetForegroundWindow() : 0;

    [LibraryImport("user32.dll")] private static partial nint GetForegroundWindow();
}

/// <summary>
/// C19.2 source: one thread with its own message loop holds out-of-context WinEvent hooks (foreground, minimize, move-size
/// end, show / hide and location changes; skipping this process). The callback only filters numbers and marks a change;
/// a one-shot timer on the same loop runs <see cref="VisibilityProducer.Tick"/> when the debounce is due, which posts the
/// set (to <c>CaptureSession.PostVisible</c>, which drops it while the gate is closed). Never reads a title.
/// </summary>
public sealed unsafe partial class Win32VisibilitySource : IDisposable
{
    private const uint WineventOutOfContext = 0x0000, WineventSkipOwnProcess = 0x0002, Flags = WineventOutOfContext | WineventSkipOwnProcess;
    private const uint WmQuit = 0x0012, WmTimer = 0x0113, PmNoRemove = 0, GaRoot = 2;
    private static readonly (uint Min, uint Max)[] Ranges = [(0x0003, 0x0003), (0x000B, 0x000B), (0x0016, 0x0017), (0x8002, 0x8003), (0x800B, 0x800B)];

    [ThreadStatic] private static Win32VisibilitySource? t_current;
    private readonly VisibilityProducer _producer;
    private readonly ManualResetEventSlim _ready = new();
    private readonly List<nint> _hooks = [];
    private Thread? _thread;
    private volatile uint _threadId;
    private nuint _timer;
    private bool _started;

    public Win32VisibilitySource(VisibilityProducer producer) => _producer = producer ?? throw new ArgumentNullException(nameof(producer));

    /// <summary>Start the hook thread; false off Windows or when no hook could be installed. Posts the first set at once.</summary>
    public bool Start()
    {
        if (!OperatingSystem.IsWindows() || _thread is not null) return false;
        _thread = new Thread(Run) { IsBackground = true, Name = "tasktor-visibility" };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
        return _started;
    }

    /// <summary>Remove the hooks and join the thread (before the session stops).</summary>
    public void Dispose()
    {
        var thread = _thread;
        if (thread is null) return;
        if (_threadId != 0) PostThreadMessageW(_threadId, WmQuit, 0, 0);
        if (thread != Thread.CurrentThread) thread.Join(TimeSpan.FromSeconds(5));
        _thread = null;
    }

    private void Run()
    {
        t_current = this;
        _threadId = GetCurrentThreadId();
        Msg msg;
        PeekMessageW(&msg, 0, 0, 0, PmNoRemove); // this thread's message queue, before anything can post to it
        try
        {
            foreach (var (min, max) in Ranges)
            {
                var hook = SetWinEventHook(min, max, 0, &Callback, 0, 0, Flags);
                if (hook != 0) _hooks.Add(hook);
            }

            _started = _hooks.Count == Ranges.Length;
            if (!_started) return;
            _producer.Changed((long)GetTickCount64() - VisibilityProducer.DebounceMs); // the first set, at once
            Arm();
            _ready.Set();
            while (GetMessageW(&msg, 0, 0, 0) > 0)
            {
                if (msg.Message == WmTimer && msg.WParam == _timer)
                {
                    KillTimer(0, _timer);
                    _timer = 0;
                    try
                    {
                        _producer.Tick((long)GetTickCount64());
                    }
                    catch (Exception)
                    {
                        // a failed read (a window gone mid-enumeration): the next change tries again; never logged
                    }

                    Arm();
                }
                else
                {
                    TranslateMessage(&msg);
                    DispatchMessageW(&msg);
                }
            }
        }
        finally
        {
            if (_timer != 0) KillTimer(0, _timer);
            foreach (var h in _hooks) UnhookWinEvent(h);
            _hooks.Clear();
            t_current = null;
            _ready.Set();
        }
    }

    // A one-shot timer for the pending change (none while idle: no polling).
    private void Arm()
    {
        if (_timer != 0 || _producer.DueAtMs is not { } due) return;
        var wait = Math.Clamp(due - (long)GetTickCount64(), 1, VisibilityProducer.MaxDelayMs);
        _timer = SetTimer(0, 0, (uint)wait, 0);
    }

    [UnmanagedCallersOnly]
    private static void Callback(nint hook, uint winEvent, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (t_current is not { } self || hwnd == 0) return;
        if (!VisibilityProducer.Counts(winEvent, idObject, idChild, hwnd, static h => GetAncestor(h, GaRoot) == h)) return;
        self._producer.Changed((long)GetTickCount64());
        self.Arm();
    }

    [StructLayout(LayoutKind.Sequential)] private struct Msg { public nint Hwnd; public uint Message; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
    [LibraryImport("user32.dll")] private static partial nint SetWinEventHook(uint min, uint max, nint module, delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void> callback, uint pid, uint thread, uint flags);
    [LibraryImport("user32.dll")] private static partial int UnhookWinEvent(nint hook);
    [LibraryImport("user32.dll")] private static partial nint GetAncestor(nint hwnd, uint flags);
    [LibraryImport("user32.dll")] private static partial int GetMessageW(Msg* msg, nint hwnd, uint min, uint max);
    [LibraryImport("user32.dll")] private static partial int PeekMessageW(Msg* msg, nint hwnd, uint min, uint max, uint remove);
    [LibraryImport("user32.dll")] private static partial int TranslateMessage(Msg* msg);
    [LibraryImport("user32.dll")] private static partial nint DispatchMessageW(Msg* msg);
    [LibraryImport("user32.dll")] private static partial int PostThreadMessageW(uint thread, uint message, nuint wp, nint lp);
    [LibraryImport("user32.dll")] private static partial nuint SetTimer(nint hwnd, nuint id, uint interval, nint callback);
    [LibraryImport("user32.dll")] private static partial int KillTimer(nint hwnd, nuint id);
    [LibraryImport("kernel32.dll")] private static partial uint GetCurrentThreadId();
    [LibraryImport("kernel32.dll")] private static partial ulong GetTickCount64();
}
