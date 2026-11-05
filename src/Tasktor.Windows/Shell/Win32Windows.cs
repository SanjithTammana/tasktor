using System.Runtime.InteropServices;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Presets;

using Tasktor.Core.Windows;

namespace Tasktor.Windows.Shell;

/// <summary>
/// Win32 side of the shell's window logic: enumerate top-level windows with the DWM attributes D-W4 needs (cloak,
/// visible frame), list monitors by device path with their DPI (Per-Monitor-V2), read the notification state for F7, and
/// place windows asynchronously (D-W6: <c>SWP_ASYNCWINDOWPOS</c>, never activates). Thin: every decision lives in the
/// pure classes beside it.
/// </summary>
public static unsafe partial class Win32Windows
{
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const long WsVisible = 0x10000000;
    private const long WsMinimize = 0x20000000;
    private const long WsMaximize = 0x01000000;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExAppWindow = 0x00040000;
    private const long WsExNoActivate = 0x08000000;
    private const uint GwOwner = 4;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpAsyncWindowPos = 0x4000;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [ThreadStatic]
    private static List<nint>? t_found;

    /// <summary>Top-level windows in z-order, with styles, cloak, frame and process; titles only in memory.</summary>
    public static List<WindowInfo> Enumerate(Func<uint, bool>? frozen = null)
    {
        var list = new List<WindowInfo>();
        if (!OperatingSystem.IsWindows())
        {
            return list;
        }

        t_found = [];
        EnumWindows(&Collect, 0);
        foreach (var h in t_found)
        {
            GetWindowThreadProcessId(h, out var pid);
            var style = (long)GetWindowLongPtrW(h, GwlStyle);
            var ex = (long)GetWindowLongPtrW(h, GwlExStyle);
            var cloak = 0;
            DwmGetWindowAttribute(h, DwmwaCloaked, &cloak, sizeof(int));
            Rect frame = default, win = default;
            DwmGetWindowAttribute(h, DwmwaExtendedFrameBounds, &frame, sizeof(Rect));
            GetWindowRect(h, out win);
            var exe = ExeOf(pid);
            list.Add(new WindowInfo
            {
                Hwnd = h,
                Pid = pid,
                AppId = exe?.ToLowerInvariant() ?? "pid:" + pid,
                Exe = exe,
                Class = ClassOf(h),
                Title = Sensitive.Of(TitleOf(h)),
                Visible = (style & WsVisible) != 0,
                Cloaked = cloak switch { 0 => Cloak.None, 1 => Cloak.App, 2 => Cloak.Shell, _ => Cloak.Inherited },
                ToolWindow = (ex & WsExToolWindow) != 0,
                AppWindow = (ex & WsExAppWindow) != 0,
                NoActivate = (ex & WsExNoActivate) != 0,
                Owner = GetWindow(h, GwOwner),
                FramePx = frame.ToPx(),
                WindowPx = win.ToPx(),
                Show = (style & WsMinimize) != 0 ? ShowState.Minimized : (style & WsMaximize) != 0 ? ShowState.Maximized : ShowState.Normal,
                Hung = IsHungAppWindow(h) != 0,
                Frozen = frozen?.Invoke(pid) == true,
                Elevated = exe is null, // a medium-integrity process cannot open an elevated one's image name
            });
        }

        t_found = null;
        return list;
    }

    [UnmanagedCallersOnly]
    private static int Collect(nint hwnd, nint lParam)
    {
        t_found?.Add(hwnd);
        return 1;
    }

    /// <summary>Monitors with device path, bounds, work area and effective DPI.</summary>
    public static List<MonitorInfo> Monitors()
    {
        var list = new List<MonitorInfo>();
        if (!OperatingSystem.IsWindows())
        {
            return list;
        }

        t_found = [];
        EnumDisplayMonitors(0, 0, &CollectMonitor, 0);
        foreach (var m in t_found)
        {
            var info = new MonitorInfoEx { CbSize = (uint)sizeof(MonitorInfoEx) };
            if (GetMonitorInfoW(m, ref info) == 0)
            {
                continue;
            }

            var dpi = GetDpiForMonitor(m, 0, out var dx, out _) == 0 ? (int)dx : 96;
            var device = new string(info.Device, 0, 32).TrimEnd('\0');
            list.Add(new MonitorInfo(device, info.Monitor.ToPx(), info.Work.ToPx(), dpi, (info.Flags & 1) != 0));
        }

        t_found = null;
        return list;
    }

    [UnmanagedCallersOnly]
    private static int CollectMonitor(nint monitor, nint hdc, Rect* rect, nint lParam)
    {
        t_found?.Add(monitor);
        return 1;
    }

    /// <summary><c>SHQueryUserNotificationState</c> for F7.</summary>
    public static NotificationState QueryNotificationState() =>
        OperatingSystem.IsWindows() && SHQueryUserNotificationState(out var s) == 0 ? (NotificationState)s : NotificationState.AcceptsNotifications;

    /// <summary>Apply one plan's move asynchronously, without activating or changing z-order.</summary>
    public static bool Place(long hwnd, RectPx r, ShowState show)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var h = (nint)hwnd;
        if (show == ShowState.Normal)
        {
            ShowWindowAsync(h, 4); // SW_SHOWNOACTIVATE: leave maximized / minimized first
        }

        var ok = SetWindowPos(h, 0, r.X, r.Y, r.W, r.H, SwpNoZOrder | SwpNoActivate | SwpAsyncWindowPos) != 0;
        if (show == ShowState.Maximized)
        {
            ShowWindowAsync(h, 3);
        }
        else if (show == ShowState.Minimized)
        {
            ShowWindowAsync(h, 7); // SW_SHOWMINNOACTIVE
        }

        return ok;
    }

    private static string TitleOf(nint h)
    {
        var buf = new ushort[512];
        fixed (ushort* p = buf)
        {
            var n = GetWindowTextW(h, p, buf.Length);
            return n <= 0 ? string.Empty : new string((char*)p, 0, n);
        }
    }

    private static string? ClassOf(nint h)
    {
        var buf = new ushort[256];
        fixed (ushort* p = buf)
        {
            var n = GetClassNameW(h, p, buf.Length);
            return n <= 0 ? null : new string((char*)p, 0, n);
        }
    }

    private static string? ExeOf(uint pid)
    {
        var proc = OpenProcess(ProcessQueryLimitedInformation, 0, pid);
        if (proc == 0)
        {
            return null;
        }

        try
        {
            var buf = new ushort[1024];
            var size = (uint)buf.Length;
            fixed (ushort* p = buf)
            {
                return QueryFullProcessImageNameW(proc, 0, p, ref size) != 0 ? Path.GetFileName(new string((char*)p, 0, (int)size)) : null;
            }
        }
        finally
        {
            CloseHandle(proc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly RectPx ToPx() => new(Left, Top, Right - Left, Bottom - Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfoEx
    {
        public uint CbSize;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        public fixed char Device[32];
    }

    [LibraryImport("user32.dll")]
    private static partial int EnumWindows(delegate* unmanaged<nint, nint, int> callback, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial int EnumDisplayMonitors(nint hdc, nint clip, delegate* unmanaged<nint, nint, Rect*, nint, int> callback, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    private static partial int GetMonitorInfoW(nint monitor, ref MonitorInfoEx info);

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtrW(nint hwnd, int index);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindow(nint hwnd, uint cmd);

    [LibraryImport("user32.dll")]
    private static partial int GetWindowRect(nint hwnd, out Rect rect);

    [LibraryImport("user32.dll")]
    private static partial int GetWindowTextW(nint hwnd, ushort* text, int max);

    [LibraryImport("user32.dll")]
    private static partial int GetClassNameW(nint hwnd, ushort* text, int max);

    [LibraryImport("user32.dll")]
    private static partial int IsHungAppWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    private static partial int ShowWindowAsync(nint hwnd, int cmd);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint hwnd, int attribute, void* value, int size);

    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out int state);

    [LibraryImport("kernel32.dll")]
    private static partial nint OpenProcess(uint access, int inherit, uint pid);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW")]
    private static partial int QueryFullProcessImageNameW(nint process, uint flags, ushort* name, ref uint size);

    [LibraryImport("kernel32.dll")]
    private static partial int CloseHandle(nint handle);
}
