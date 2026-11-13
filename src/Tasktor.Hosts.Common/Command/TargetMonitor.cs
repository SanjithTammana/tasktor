using System.Runtime.InteropServices;

namespace Tasktor.Hosts.Common.Command;

/// <summary>Where a glance surface's monitor comes from.</summary>
public enum MonitorSource
{
    /// <summary>The monitor holding most of the active window.</summary>
    Foreground,

    /// <summary>The monitor under the mouse pointer.</summary>
    Cursor,
}

/// <summary>
/// G2 (D-G12): the shelf and the command line open "on the same monitor" as the user, which means the monitor of the window
/// they are working in. The taskbar counts as a window, so a tray click opens on that taskbar's monitor. The desktop
/// (<c>Progman</c>/<c>WorkerW</c>) spans every monitor, and a hidden or minimized window has no honest position, so those
/// fall back to the pointer. The caller falls back to the main monitor if the point lands on none.
/// </summary>
public static partial class TargetMonitor
{
    private const uint MonitorDefaultToNull = 0;

    /// <summary>Which source to trust for this foreground window. Pure, so it is unit-tested.</summary>
    public static MonitorSource Choose(bool hasForeground, string? className, bool visible, bool minimized) =>
        hasForeground && visible && !minimized && className is not ("Progman" or "WorkerW") ? MonitorSource.Foreground : MonitorSource.Cursor;

    /// <summary>A physical-pixel point inside the target monitor, or null off Windows or when nothing answers.</summary>
    public static (int X, int Y)? Point()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var window = GetForegroundWindow();
        var source = Choose(window != 0, ClassName(window), window != 0 && IsWindowVisible(window), window != 0 && IsIconic(window));
        if (source == MonitorSource.Foreground)
        {
            var monitor = MonitorFromWindow(window, MonitorDefaultToNull);
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (monitor != 0 && GetMonitorInfoW(monitor, ref info))
            {
                return (info.Monitor.Left + ((info.Monitor.Right - info.Monitor.Left) / 2), info.Monitor.Top + ((info.Monitor.Bottom - info.Monitor.Top) / 2));
            }
        }

        return GetCursorPos(out var cursor) ? (cursor.X, cursor.Y) : null;
    }

    private static unsafe string? ClassName(nint window)
    {
        if (window == 0) return null;
        var name = stackalloc char[64];
        var length = GetClassNameW(window, name, 64);
        return length > 0 ? new string(name, 0, length) : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Pt
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [LibraryImport("user32.dll")] private static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")] private static unsafe partial int GetClassNameW(nint window, char* name, int max);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsWindowVisible(nint window);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsIconic(nint window);
    [LibraryImport("user32.dll")] private static partial nint MonitorFromWindow(nint window, uint flags);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetCursorPos(out Pt point);
}
