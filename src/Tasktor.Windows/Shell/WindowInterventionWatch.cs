using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Tasktor.Windows.Shell;

/// <summary>Tracks user move/size starts and window destruction without recording titles or paths.</summary>
public sealed unsafe partial class WindowInterventionWatch : IDisposable
{
    private const uint MoveSizeStart = 0x000A;
    private const uint MoveSizeEnd = 0x000B;
    private const uint ObjectDestroy = 0x8001;
    private const uint Quit = 0x0012;
    private static WindowInterventionWatch? s_current;
    private readonly ConcurrentDictionary<long, long> _changed = new();
    private readonly ConcurrentQueue<(long Hwnd, long At, uint Kind)> _raw = new();
    private readonly ConcurrentDictionary<long, byte> _active = new();
    private readonly Lock _gate = new();
    private Thread? _thread;
    private uint _threadId;
    private volatile bool _ready;
    private volatile bool _disposed;

    public bool ChangedSince(long hwnd, long since)
    {
        Drain();
        return !_ready || _active.ContainsKey(hwnd) || (_changed.TryGetValue(hwnd, out var at) && at >= since);
    }

    private void Drain()
    {
        lock (_gate)
        {
            while (_raw.TryDequeue(out var item))
            {
                if (item.Kind == MoveSizeStart) _active[item.Hwnd] = 0;
                else _active.TryRemove(item.Hwnd, out _);
                _changed[item.Hwnd] = item.At;
            }
            if (_changed.Count <= 512) return;
            var cutoff = Environment.TickCount64 - 60000;
            foreach (var entry in _changed)
            {
                if (entry.Value < cutoff) _changed.TryRemove(entry.Key, out _);
            }
        }
    }

    public void Start()
    {
        if (!OperatingSystem.IsWindows() || _disposed || _thread is not null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "tasktor-window-intervention" };
        _thread.Start();
    }

    public void Dispose()
    {
        _disposed = true;
        _ready = false;
        if (_threadId != 0) PostThreadMessageW(_threadId, Quit, 0, 0);
        _thread?.Join(TimeSpan.FromSeconds(1));
    }

    private void Run()
    {
        Msg initial;
        PeekMessageW(&initial, 0, 0, 0, 0);
        _threadId = GetCurrentThreadId();
        if (_disposed) return;
        s_current = this;
        var moves = SetWinEventHook(MoveSizeStart, MoveSizeEnd, 0, &Callback, 0, 0, 2);
        var destroyed = SetWinEventHook(ObjectDestroy, ObjectDestroy, 0, &Callback, 0, 0, 2);
        _ready = moves != 0 && destroyed != 0;
        try
        {
            Msg msg;
            while (!_disposed && GetMessageW(&msg, 0, 0, 0) > 0) { TranslateMessage(&msg); DispatchMessageW(&msg); Drain(); }
        }
        finally
        {
            _ready = false;
            if (moves != 0) UnhookWinEvent(moves);
            if (destroyed != 0) UnhookWinEvent(destroyed);
            s_current = null;
        }
    }

    [UnmanagedCallersOnly]
    private static void Callback(nint hook, uint kind, nint hwnd, int objectId, int childId, uint thread, uint time)
    {
        if (hwnd != 0 && objectId == 0 && childId == 0 && s_current is { } owner)
            owner._raw.Enqueue(((long)hwnd, Environment.TickCount64, kind));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [LibraryImport("user32.dll")]
    private static partial nint SetWinEventHook(uint min, uint max, nint module, delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void> callback, uint process, uint thread, uint flags);
    [LibraryImport("user32.dll")]
    private static partial int UnhookWinEvent(nint hook);
    [LibraryImport("user32.dll")]
    private static partial int PeekMessageW(Msg* msg, nint hwnd, uint min, uint max, uint remove);
    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(Msg* msg, nint hwnd, uint min, uint max);
    [LibraryImport("user32.dll")]
    private static partial int TranslateMessage(Msg* msg);
    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(Msg* msg);
    [LibraryImport("user32.dll")]
    private static partial int PostThreadMessageW(uint thread, uint message, nint wParam, nint lParam);
    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();
}
