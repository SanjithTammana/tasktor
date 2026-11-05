using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Workflow;

namespace Tasktor.Windows.Sources;

/// <summary>
/// C1 <c>WinEventSource</c>: foreground, minimize start / end (global) and title changes scoped to the foreground process
/// (<see cref="FocusProcess"/> moves that hook; a foreground change moves it too), all out-of-context (no DLL injected)
/// and skipping Tasktor's own process. One hook thread pumps messages; the callback only enqueues numbers. The thread's
/// own step then reads the title (<c>GetWindowTextW</c>) and the exe and posts a <see cref="WindowEvent"/>. Never a
/// keyboard or mouse hook.
/// </summary>
public sealed unsafe partial class WinEventSource : IEventSource<WindowEvent>
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventSystemMinimizeStart = 0x0016;
    private const uint EventSystemMinimizeEnd = 0x0017;
    private const uint EventObjectNameChange = 0x800C;
    private const uint WineventOutOfContext = 0x0000;
    private const uint WineventSkipOwnProcess = 0x0002;
    private const uint WmQuit = 0x0012;
    private const uint WmFocus = 0x8001; // WM_APP + 1: move the title hook to a process
    private const uint Flags = WineventOutOfContext | WineventSkipOwnProcess;

    private static WinEventSource? s_current;
    private readonly ConcurrentQueue<(uint Event, nint Hwnd)> _raw = new();
    private readonly Func<uint, ProcessRow?> _readProcess;
    private Thread? _thread;
    private uint _threadId;
    private Action<WindowEvent>? _post;
    private nint _titleHook;
    private uint _titlePid;

    public WinEventSource(Func<uint, ProcessRow?> readProcess) => _readProcess = readProcess;

    public string Name => "win-events";

    /// <summary>True when the hooks could not be installed: C1 reports "capture unavailable" and tracking stays off.</summary>
    public bool Failed { get; private set; }

    public void Start(Action<WindowEvent> post)
    {
        ArgumentNullException.ThrowIfNull(post);
        if (!OperatingSystem.IsWindows())
        {
            Failed = true;
            return;
        }

        _post = post;
        s_current = this;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "tasktor-win-events" };
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(2));
    }

    /// <summary>Move the scoped title-change hook to <paramref name="pid"/> (thread-safe).</summary>
    public void FocusProcess(uint pid)
    {
        if (_threadId != 0)
        {
            PostThreadMessageW(_threadId, WmFocus, (nint)pid, 0);
        }
    }

    public void Stop()
    {
        if (_threadId != 0)
        {
            PostThreadMessageW(_threadId, WmQuit, 0, 0);
        }

        _thread?.Join(TimeSpan.FromSeconds(1));
        _threadId = 0;
        s_current = null;
        _post = null;
    }

    private void Run(ManualResetEventSlim ready)
    {
        _threadId = GetCurrentThreadId();
        var fg = SetWinEventHook(EventSystemForeground, EventSystemForeground, 0, &Callback, 0, 0, Flags);
        var min = SetWinEventHook(EventSystemMinimizeStart, EventSystemMinimizeEnd, 0, &Callback, 0, 0, Flags);
        Failed = fg == 0;
        ready.Set();
        try
        {
            Msg msg;
            while (GetMessageW(&msg, 0, 0, 0) > 0)
            {
                if (msg.Message == WmFocus)
                {
                    MoveTitleHook((uint)msg.WParam);
                    continue;
                }

                TranslateMessage(&msg);
                DispatchMessageW(&msg);
                Drain();
            }
        }
        finally
        {
            foreach (var h in new[] { fg, min, _titleHook })
            {
                if (h != 0)
                {
                    UnhookWinEvent(h);
                }
            }

            _titleHook = 0;
            _titlePid = 0;
        }
    }

    /// <summary>Titles are read only for the foreground process: the name-change hook follows it.</summary>
    private void MoveTitleHook(uint pid)
    {
        if (pid == _titlePid)
        {
            return;
        }

        if (_titleHook != 0)
        {
            UnhookWinEvent(_titleHook);
        }

        _titlePid = pid;
        _titleHook = pid == 0 ? 0 : SetWinEventHook(EventObjectNameChange, EventObjectNameChange, 0, &Callback, pid, 0, Flags);
    }

    /// <summary>The source's own step (hook thread, never the callback): text and exe, then post.</summary>
    private void Drain()
    {
        while (_raw.TryDequeue(out var r))
        {
            GetWindowThreadProcessId(r.Hwnd, out var pid);
            var kind = r.Event switch
            {
                EventSystemForeground => WindowEventKind.Foreground,
                EventSystemMinimizeStart => WindowEventKind.MinimizeStart,
                EventSystemMinimizeEnd => WindowEventKind.MinimizeEnd,
                _ => WindowEventKind.TitleChanged,
            };
            if (kind == WindowEventKind.Foreground)
            {
                MoveTitleHook(pid);
            }

            var row = _readProcess(pid);
            _post?.Invoke(new WindowEvent(kind, r.Hwnd, row?.Key ?? new ProcKey(pid, 0), Environment.TickCount64, DateTimeOffset.UtcNow)
            {
                Title = Sensitive.Of(ReadTitle(r.Hwnd)),
                Exe = row?.Exe.ToLowerInvariant(),
            });
        }
    }

    private static string ReadTitle(nint hwnd)
    {
        const int Max = 512;
        var buf = stackalloc ushort[Max];
        var n = GetWindowTextW(hwnd, buf, Max);
        return n <= 0 ? string.Empty : new string((char*)buf, 0, n);
    }

    [UnmanagedCallersOnly]
    private static void Callback(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // Numbers only; window-level events only (OBJID_WINDOW, CHILDID_SELF).
        if (hwnd == 0 || idObject != 0 || idChild != 0)
        {
            return;
        }

        s_current?._raw.Enqueue((evt, hwnd));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [LibraryImport("user32.dll")]
    private static partial nint SetWinEventHook(uint eventMin, uint eventMax, nint module, delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void> callback, uint pid, uint thread, uint flags);

    [LibraryImport("user32.dll")]
    private static partial int UnhookWinEvent(nint hook);

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(Msg* msg, nint hwnd, uint min, uint max);

    [LibraryImport("user32.dll")]
    private static partial int TranslateMessage(Msg* msg);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(Msg* msg);

    [LibraryImport("user32.dll")]
    private static partial int PostThreadMessageW(uint thread, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    [LibraryImport("user32.dll")]
    private static partial int GetWindowTextW(nint hwnd, ushort* text, int max);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();
}
