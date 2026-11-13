using System.Runtime.InteropServices;

namespace Tasktor.Hosts.Common.Command;

/// <summary>
/// The effective scale (DPI / 96) of the monitor under a physical-pixel point. The viewer is per-monitor DPI aware, so a
/// window sized before it is shown cannot trust its own XAML rasterization scale (still 1.0, or the last monitor's):
/// size it from the scale of the monitor it is about to land on.
/// </summary>
public static partial class DisplayScale
{
    private const uint MonitorDefaultToPrimary = 1;
    private const int EffectiveDpi = 0;

    /// <summary>The scale of the monitor containing (<paramref name="x"/>, <paramref name="y"/>); 1.0 off Windows or on failure.</summary>
    public static double At(int x, int y)
    {
        if (!OperatingSystem.IsWindows()) return 1.0;
        var monitor = MonitorFromPoint(new Point { X = x, Y = y }, MonitorDefaultToPrimary);
        return monitor != 0 && GetDpiForMonitor(monitor, EffectiveDpi, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1.0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    [LibraryImport("user32.dll")] private static partial nint MonitorFromPoint(Point point, uint flags);
    [LibraryImport("shcore.dll")] private static partial int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
}
