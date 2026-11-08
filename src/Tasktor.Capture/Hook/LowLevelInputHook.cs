using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tasktor.Capture.Hook;

/// <summary>Which low-level hooks a <see cref="LowLevelInputHook"/> installs.</summary>
[Flags]
public enum HookDevices
{
    Keyboard = 1,
    Mouse = 2,
    Both = Keyboard | Mouse,
}

/// <summary>
/// One dedicated low-level keyboard and/or mouse hook thread (the mouse alone may attach before the keyboard's real-PC
/// layout check passes). Callbacks copy one value to the supplied SPSC ring adapter
/// and call the next hook; no decoding, allocation, waits, window inspection or logging occurs in a callback.
/// </summary>
public sealed unsafe partial class LowLevelInputHook : IDisposable
{
    private const int WhKeyboardLl = 13, WhMouseLl = 14, HcAction = 0, InvalidHookHandle = 1404;
    private const uint WmQuit = 0x0012, WmTimer = 0x0113;
    private const uint PmNoRemove = 0;
    [ThreadStatic] private static LowLevelInputHook? t_current;
    private static int s_active;
    private readonly IHookInputSink _sink;
    private readonly Action _postFailure;
    private readonly HookDevices _devices;
    private readonly ManualResetEventSlim _ready = new();
    private Thread? _thread;
    private nint _keyboard, _mouse;
    private nuint _timer;
    private volatile uint _threadId;
    private volatile bool _stopRequested;
    private bool _started, _failurePosted, _unhookFailed;

    /// <param name="sink">A single-producer, allocation-free ring writer; its Write must not throw.</param>
    /// <param name="postFailure">Must only enqueue or post a failure to the capture logic thread.</param>
    public LowLevelInputHook(IHookInputSink sink, Action postFailure, HookDevices devices = HookDevices.Both)
    {
        if (devices == 0 || (devices & ~HookDevices.Both) != 0) throw new ArgumentOutOfRangeException(nameof(devices));
        _devices = devices;
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _postFailure = postFailure ?? throw new ArgumentNullException(nameof(postFailure));
    }

    /// <summary>Start only after the recording indicator is established.</summary>
    public bool Start()
    {
        if (!OperatingSystem.IsWindows() || _thread is not null || Interlocked.CompareExchange(ref s_active, 1, 0) != 0) return false;
        try
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "tasktor-input-hook" };
            _thread.Start();
        }
        catch
        {
            _thread = null;
            Volatile.Write(ref s_active, 0);
            throw;
        }
        if (!_ready.Wait(TimeSpan.FromSeconds(10)))
        {
            Dispose();
            return false;
        }
        return _started;
    }

    /// <summary>Uninstall and join the hook thread before removing the indicator.</summary>
    public void Dispose()
    {
        var thread = _thread;
        if (thread is null) return;
        _stopRequested = true;
        if (_threadId != 0) PostThreadMessageW(_threadId, WmQuit, 0, 0);
        if (thread == Thread.CurrentThread || !thread.Join(TimeSpan.FromSeconds(10)))
            throw new InvalidOperationException("Input hook thread did not stop.");
        if (_unhookFailed) throw new InvalidOperationException("Input hook could not be uninstalled.");
        _thread = null;
    }

    private void Run()
    {
        t_current = this;
        _threadId = GetCurrentThreadId();
        Msg ignored;
        PeekMessageW(&ignored, 0, 0, 0, PmNoRemove); // establish this thread's message queue
        try
        {
            if (_stopRequested || !TryInstall()) return;
            var input = new LastInputInfo { Size = (uint)sizeof(LastInputInfo) };
            if (GetLastInputInfo(&input) == 0) return;
            var watchdog = new HookWatchdog(GetTickCount(), input.Time, _sink.Published);
            _timer = SetTimer(0, 0, 1000, 0); // WM_TIMER on this message loop, never a timer thread
            if (_timer == 0 || _stopRequested) return;
            _started = true;
            _ready.Set();
            Msg msg;
            int result;
            while ((result = GetMessageW(&msg, 0, 0, 0)) > 0)
            {
                if (msg.Message == WmTimer && msg.WParam == _timer)
                {
                    input.Size = (uint)sizeof(LastInputInfo);
                    if (GetLastInputInfo(&input) != 0 && watchdog.ShouldReinstall(GetTickCount(), input.Time, _sink.Published))
                    {
                        if (!TryUninstall() || !TryInstall()) { PostFailure(); break; }
                    }
                }
                else
                {
                    TranslateMessage(&msg);
                    DispatchMessageW(&msg);
                }
            }
            if (!_stopRequested && !_failurePosted) PostFailure();
        }
        catch
        {
            if (_started && !_stopRequested) PostFailure();
        }
        finally
        {
            if (_timer != 0) KillTimer(0, _timer);
            if (!TryUninstall()) _unhookFailed = true;
            t_current = null;
            Volatile.Write(ref s_active, 0);
            _ready.Set();
        }
    }

    private bool TryInstall()
    {
        var module = GetModuleHandleW(null);
        if (module == 0) return false;
        if ((_devices & HookDevices.Keyboard) != 0)
        {
            _keyboard = SetWindowsHookExW(WhKeyboardLl, &KeyboardCallback, module, 0);
            if (_keyboard == 0) return false;
        }

        if ((_devices & HookDevices.Mouse) == 0) return true;
        _mouse = SetWindowsHookExW(WhMouseLl, &MouseCallback, module, 0);
        if (_mouse != 0) return true;
        if (!TryUninstall()) _unhookFailed = true;
        return false;
    }

    private bool TryUninstall()
    {
        var keyboard = _keyboard;
        var mouse = _mouse;
        _keyboard = _mouse = 0;
        var removed = RemoveHook(keyboard) & RemoveHook(mouse);
        if (!removed) _unhookFailed = true;
        return removed;
    }

    private static bool RemoveHook(nint handle)
    {
        if (handle == 0 || UnhookWindowsHookEx(handle) != 0) return true;
        return Marshal.GetLastPInvokeError() == InvalidHookHandle; // Windows already removed it
    }

    private void PostFailure()
    {
        if (_failurePosted) return;
        _failurePosted = true;
        _postFailure();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint KeyboardCallback(int code, nuint message, nint param)
    {
        if (code == HcAction && t_current is { } self && param != 0)
        {
            var raw = (KeyboardData*)param;
            if (HookInput.TryKeyboard((uint)message, raw->Vk, raw->Scan, raw->Flags, raw->Time, out var item)) self._sink.Write(in item);
        }
        return CallNextHookEx(0, code, message, param);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint MouseCallback(int code, nuint message, nint param)
    {
        if (code == HcAction && t_current is { } self && param != 0)
        {
            var raw = (MouseData*)param;
            if (HookInput.TryMouse((uint)message, raw->X, raw->Y, raw->Data, raw->Flags, raw->Time, out var item)) self._sink.Write(in item);
        }
        return CallNextHookEx(0, code, message, param);
    }

    [StructLayout(LayoutKind.Sequential)] private struct KeyboardData { public uint Vk, Scan, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseData { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct LastInputInfo { public uint Size, Time; }
    [StructLayout(LayoutKind.Sequential)] private struct Msg { public nint Hwnd; public uint Message; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
    [LibraryImport("user32.dll", SetLastError = true)] private static partial nint SetWindowsHookExW(int id, delegate* unmanaged[Stdcall]<int, nuint, nint, nint> callback, nint module, uint threadId);
    [LibraryImport("user32.dll", SetLastError = true)] private static partial int UnhookWindowsHookEx(nint hook);
    [LibraryImport("user32.dll")] private static partial nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [LibraryImport("user32.dll")] private static partial int GetMessageW(Msg* msg, nint hwnd, uint min, uint max);
    [LibraryImport("user32.dll")] private static partial int PeekMessageW(Msg* msg, nint hwnd, uint min, uint max, uint remove);
    [LibraryImport("user32.dll")] private static partial int TranslateMessage(Msg* msg);
    [LibraryImport("user32.dll")] private static partial nint DispatchMessageW(Msg* msg);
    [LibraryImport("user32.dll")] private static partial int PostThreadMessageW(uint thread, uint message, nuint wp, nint lp);
    [LibraryImport("user32.dll")] private static partial nuint SetTimer(nint hwnd, nuint id, uint interval, nint callback);
    [LibraryImport("user32.dll")] private static partial int KillTimer(nint hwnd, nuint id);
    [LibraryImport("user32.dll")] private static partial int GetLastInputInfo(LastInputInfo* info);
    [LibraryImport("kernel32.dll")] private static partial uint GetTickCount();
    [LibraryImport("kernel32.dll")] private static partial uint GetCurrentThreadId();
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint GetModuleHandleW(string? module);
}


