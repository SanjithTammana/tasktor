using System.Runtime.InteropServices;

namespace Tasktor.Shell;

/// <summary>
/// D-W3 opt-in "Replace Alt+Tab": a <c>WH_KEYBOARD_LL</c> hook on its own thread. The callback reads only the key code and
/// the hook's own Alt flag, asks <see cref="AltTabDecider"/> and posts one message to the switcher window; it keeps and
/// forwards nothing about any key (a test scans this file for any write path). The native Alt+Tab runs whenever this
/// does not swallow Tab: an exclusive-fullscreen app in front, the shell not running, or the hook removed by the OS. The
/// removal guard reinstalls the hook when input keeps arriving but the hook has seen no key for 30 s.
/// </summary>
internal sealed unsafe partial class KeyChordHook : IDisposable
{
    public const uint WmAltTab = 0x8000 + 2; // WM_APP + 2, to the switcher window: wParam = AltTabCommand

    private const int WhKeyboardLl = 13;
    private const int HcAction = 0;
    private const uint WmKeyDown = 0x0100, WmSysKeyDown = 0x0104, WmTimer = 0x0113, WmQuit = 0x0012;
    private const uint LlkhfAltDown = 0x20;
    private const int VkShift = 0x10;
    private const int QunsRunningD3dFullScreen = 3;
    private const int GuardTicks = 30;

    private static KeyChordHook? s_hook;

    private readonly nint _switcher;
    private readonly Func<bool> _switcherOpen;
    private Thread? _thread;
    private uint _threadId;
    private nint _hook;
    private volatile bool _exclusive;
    private long _callbacks;

    public KeyChordHook(nint switcher, Func<bool> switcherOpen)
    {
        _switcher = switcher;
        _switcherOpen = switcherOpen;
    }

    public bool Installed => _hook != 0;

    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        s_hook = this;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "tasktor-alt-tab" };
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
    }

    public void Dispose()
    {
        if (_threadId != 0)
        {
            PostThreadMessageW(_threadId, WmQuit, 0, 0);
        }

        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        s_hook = null;
    }

    private void Run(ManualResetEventSlim ready)
    {
        _threadId = GetCurrentThreadId();
        Install();
        var timer = SetTimer(0, 0, 1000, 0);
        ready.Set();
        var ticks = 0;
        long seen = 0;
        var lastInput = LastInputTick();
        Msg msg;
        while (GetMessageW(&msg, 0, 0, 0) > 0)
        {
            if (msg.Message != WmTimer)
            {
                continue;
            }

            _exclusive = SHQueryUserNotificationState(out var state) == 0 && state == QunsRunningD3dFullScreen;
            if (++ticks < GuardTicks)
            {
                continue;
            }

            // Removal guard: input arrived over the last 30 s but the hook saw no key, so the OS may have removed it.
            ticks = 0;
            var now = LastInputTick();
            var calls = Interlocked.Read(ref _callbacks);
            if (now != lastInput && calls == seen)
            {
                Uninstall();
                Install();
            }

            (lastInput, seen) = (now, calls);
        }

        KillTimer(0, timer);
        Uninstall();
    }

    private void Install()
    {
        if (_hook == 0)
        {
            _hook = SetWindowsHookExW(WhKeyboardLl, &Callback, GetModuleHandleW(null), 0);
        }
    }

    private void Uninstall()
    {
        if (_hook != 0)
        {
            UnhookWindowsHookEx(_hook);
            _hook = 0;
        }
    }

    [UnmanagedCallersOnly]
    private static nint Callback(int code, nint wParam, nint lParam)
    {
        if (code == HcAction && s_hook is { } self)
        {
            try
            {
                Interlocked.Increment(ref self._callbacks);
                var info = (KbdLlHookStruct*)lParam;
                var down = (uint)wParam is WmKeyDown or WmSysKeyDown;
                var decision = AltTabDecider.Decide(
                    (int)info->VkCode,
                    down,
                    altHeld: (info->Flags & LlkhfAltDown) != 0,
                    shiftHeld: (GetAsyncKeyState(VkShift) & 0x8000) != 0,
                    switcherOpen: self._switcherOpen(),
                    exclusiveFullscreen: self._exclusive);
                if (decision.Command != AltTabCommand.None)
                {
                    PostMessageW(self._switcher, WmAltTab, (nint)decision.Command, 0);
                }

                if (decision.Swallow)
                {
                    return 1;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // never throw across the native boundary: the key passes through
            }
        }

        return CallNextHookEx(0, code, wParam, lParam);
    }

    private static uint LastInputTick()
    {
        var info = new LastInputInfo { CbSize = (uint)sizeof(LastInputInfo) };
        return GetLastInputInfo(&info) != 0 ? info.DwTime : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode, ScanCode, Flags, Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint CbSize, DwTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam, LParam;
        public uint Time;
        public int X, Y;
    }

    [LibraryImport("user32.dll")] private static partial nint SetWindowsHookExW(int id, delegate* unmanaged<int, nint, nint, nint> proc, nint module, uint thread);
    [LibraryImport("user32.dll")] private static partial int UnhookWindowsHookEx(nint hook);
    [LibraryImport("user32.dll")] private static partial nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [LibraryImport("user32.dll")] private static partial int PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);
    [LibraryImport("user32.dll")] private static partial int PostThreadMessageW(uint thread, uint msg, nint wParam, nint lParam);
    [LibraryImport("user32.dll")] private static partial int GetMessageW(Msg* msg, nint hwnd, uint min, uint max);
    [LibraryImport("user32.dll")] private static partial nuint SetTimer(nint hwnd, nuint id, uint ms, nint proc);
    [LibraryImport("user32.dll")] private static partial int KillTimer(nint hwnd, nuint id);
    [LibraryImport("user32.dll")] private static partial short GetAsyncKeyState(int vk);
    [LibraryImport("user32.dll")] private static partial int GetLastInputInfo(LastInputInfo* info);
    [LibraryImport("shell32.dll")] private static partial int SHQueryUserNotificationState(out int state);
    [LibraryImport("kernel32.dll")] private static partial uint GetCurrentThreadId();
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint GetModuleHandleW(string? name);
}
