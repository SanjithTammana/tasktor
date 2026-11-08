using System.Runtime.InteropServices;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Workflow;

namespace Tasktor.Windows.Sources;

/// <summary>
/// C1.2 <c>SessionSource</c>: lock / unlock (<c>WTSRegisterSessionNotification</c>) and sleep / resume
/// (<c>WM_POWERBROADCAST</c>) on a message-only window with its own thread. Events only, no polling.
/// </summary>
public sealed unsafe partial class SessionSource : IEventSource<PresenceEvent>
{
    private const uint WmWtsSessionChange = 0x02B1;
    private const uint WmPowerBroadcast = 0x0218;
    private const uint WmQuit = 0x0012;
    private const nint WtsSessionLock = 0x7;
    private const nint WtsSessionUnlock = 0x8;
    private const nint PbtApmSuspend = 0x4;
    private const nint PbtApmResumeAutomatic = 0x12;
    private static readonly nint HwndMessage = -3;

    private static Action<PresenceEvent>? s_post;
    private Thread? _thread;
    private uint _threadId;

    public string Name => "session";

    public void Start(Action<PresenceEvent> post)
    {
        ArgumentNullException.ThrowIfNull(post);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        s_post = post;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "tasktor-session" };
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(2));
    }

    public void Stop()
    {
        if (_threadId != 0)
        {
            PostThreadMessageW(_threadId, WmQuit, 0, 0);
        }

        _thread?.Join(TimeSpan.FromSeconds(1));
        _threadId = 0;
        s_post = null;
    }

    private void Run(ManualResetEventSlim ready)
    {
        _threadId = GetCurrentThreadId();
        var module = GetModuleHandleW(null);
        var className = Marshal.StringToHGlobalUni("TasktorSessionWindow");
        nint hwnd = 0;
        try
        {
            var wc = new WndClassEx
            {
                CbSize = (uint)sizeof(WndClassEx),
                WndProc = &WndProc,
                Instance = module,
                ClassName = className,
            };
            RegisterClassExW(&wc);
            hwnd = CreateWindowExW(0, className, 0, 0, 0, 0, 0, 0, HwndMessage, 0, module, 0);
            if (hwnd != 0)
            {
                WTSRegisterSessionNotification(hwnd, 0); // NOTIFY_FOR_THIS_SESSION
            }

            ready.Set();
            Msg msg;
            while (GetMessageW(&msg, 0, 0, 0) > 0)
            {
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
        }
        finally
        {
            ready.Set();
            if (hwnd != 0)
            {
                WTSUnRegisterSessionNotification(hwnd);
                DestroyWindow(hwnd);
            }

            UnregisterClassW(className, module);
            Marshal.FreeHGlobal(className);
        }
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        PresenceKind? kind = msg switch
        {
            WmWtsSessionChange when wParam == WtsSessionLock => PresenceKind.Lock,
            WmWtsSessionChange when wParam == WtsSessionUnlock => PresenceKind.Unlock,
            WmPowerBroadcast when wParam == PbtApmSuspend => PresenceKind.Suspend,
            WmPowerBroadcast when wParam == PbtApmResumeAutomatic => PresenceKind.Resume,
            _ => null,
        };
        if (kind is { } k)
        {
            s_post?.Invoke(new PresenceEvent(k, DateTimeOffset.UtcNow, Environment.TickCount64));
            return msg == WmPowerBroadcast ? 1 : 0;
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WndClassEx
    {
        public uint CbSize;
        public uint Style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> WndProc;
        public int ClsExtra;
        public int WndExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public nint MenuName;
        public nint ClassName;
        public nint IconSmall;
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
    private static partial ushort RegisterClassExW(WndClassEx* wc);

    [LibraryImport("user32.dll")]
    private static partial int UnregisterClassW(nint className, nint instance);

    [LibraryImport("user32.dll")]
    private static partial nint CreateWindowExW(uint exStyle, nint className, nint windowName, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    private static partial int DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(Msg* msg, nint hwnd, uint min, uint max);

    [LibraryImport("user32.dll")]
    private static partial int TranslateMessage(Msg* msg);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(Msg* msg);

    [LibraryImport("user32.dll")]
    private static partial int PostThreadMessageW(uint thread, uint msg, nint wParam, nint lParam);

    [LibraryImport("wtsapi32.dll")]
    private static partial int WTSRegisterSessionNotification(nint hwnd, uint flags);

    [LibraryImport("wtsapi32.dll")]
    private static partial int WTSUnRegisterSessionNotification(nint hwnd);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? name);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();
}
