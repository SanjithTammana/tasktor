using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Presets;

namespace Tasktor.Shell.Windowing;

/// <summary>A rectangle in physical pixels (virtual-screen coordinates).</summary>
public readonly record struct RectPx(int X, int Y, int W, int H)
{
    public int Right => X + W;

    public int Bottom => Y + H;

    public long Area => (long)Math.Max(0, W) * Math.Max(0, H);

    public RectPx Intersect(RectPx o)
    {
        var x = Math.Max(X, o.X);
        var y = Math.Max(Y, o.Y);
        var r = Math.Min(Right, o.Right);
        var b = Math.Min(Bottom, o.Bottom);
        return r <= x || b <= y ? default : new RectPx(x, y, r - x, b - y);
    }

    /// <summary>Whether this rect covers <paramref name="o"/> entirely, allowing <paramref name="slack"/> px per edge.</summary>
    public bool Covers(RectPx o, int slack = 1) => X <= o.X + slack && Y <= o.Y + slack && Right >= o.Right - slack && Bottom >= o.Bottom - slack;
}

/// <summary>Why DWM hides a window (<c>DWMWA_CLOAKED</c>).</summary>
public enum Cloak : byte
{
    None,
    App,

    /// <summary>Cloaked by the Shell: typically on another virtual desktop (D-W7).</summary>
    Shell,
    Inherited,
}

/// <summary>
/// One top-level window as the shell sees it (D-W4). The title is personal data and stays in the shell's memory only;
/// <see cref="FramePx"/> is the visible frame (<c>DWMWA_EXTENDED_FRAME_BOUNDS</c>), <see cref="WindowPx"/> the
/// <c>GetWindowRect</c> rect including the invisible resize border.
/// </summary>
public sealed record WindowInfo
{
    public required long Hwnd { get; init; }

    public required uint Pid { get; init; }

    /// <summary>A's app id (<c>AppIdentity</c>: lower-cased exe or package family).</summary>
    public required string AppId { get; init; }

    public string? Exe { get; init; }

    public string? Class { get; init; }

    public Sensitive<string> Title { get; init; } = Sensitive.Of(string.Empty);

    public bool Visible { get; init; } = true;

    public Cloak Cloaked { get; init; }

    public bool ToolWindow { get; init; }

    public bool AppWindow { get; init; }

    public bool NoActivate { get; init; }

    /// <summary>The owner window, or 0.</summary>
    public long Owner { get; init; }

    public RectPx FramePx { get; init; }

    public RectPx WindowPx { get; init; }

    public ShowState Show { get; init; }

    public bool Elevated { get; init; }

    public bool Hung { get; init; }

    /// <summary>The window belongs to a frozen job (B's <c>FrozenRegistry</c>).</summary>
    public bool Frozen { get; init; }

    public bool OnCurrentDesktop { get; init; } = true;
}

/// <summary>One display: device path (stable across reboots), bounds and work area in px, effective DPI.</summary>
public sealed record MonitorInfo(string DevicePath, RectPx BoundsPx, RectPx WorkPx, int Dpi, bool Primary)
{
    public double Scale => Dpi / 96.0;
}

/// <summary>
/// F6 / D-W8 monitor math: monitors by device path, rects stored in DIPs relative to the monitor's work area, scaled to
/// the monitor's current DPI on restore; a missing monitor falls back to the primary with the rect scaled to fit; a
/// result is never off-screen.
/// </summary>
public sealed class MonitorMap
{
    public MonitorMap(IReadOnlyList<MonitorInfo> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        if (monitors.Count == 0)
        {
            throw new ArgumentException("At least one monitor.", nameof(monitors));
        }

        Monitors = monitors;
        Primary = monitors.FirstOrDefault(m => m.Primary) ?? monitors[0];
    }

    public IReadOnlyList<MonitorInfo> Monitors { get; }

    public MonitorInfo Primary { get; }

    /// <summary>A stable id for this monitor arrangement (device paths, bounds, DPI), e.g. to remember per-setup presets.</summary>
    public string Fingerprint()
    {
        var text = string.Join('\n', Monitors.OrderBy(m => m.DevicePath, StringComparer.OrdinalIgnoreCase)
            .Select(m => string.Create(CultureInfo.InvariantCulture, $"{m.DevicePath.ToUpperInvariant()}|{m.BoundsPx.X},{m.BoundsPx.Y},{m.BoundsPx.W},{m.BoundsPx.H}|{m.Dpi}")));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }

    public MonitorInfo? Find(string devicePath) => Monitors.FirstOrDefault(m => string.Equals(m.DevicePath, devicePath, StringComparison.OrdinalIgnoreCase));

    /// <summary>The monitor a rect is mostly on (largest overlap), else the nearest by centre distance.</summary>
    public MonitorInfo Of(RectPx r)
    {
        var best = Monitors.MaxBy(m => m.BoundsPx.Intersect(r).Area)!;
        if (best.BoundsPx.Intersect(r).Area > 0)
        {
            return best;
        }

        double cx = r.X + (r.W / 2.0), cy = r.Y + (r.H / 2.0);
        return Monitors.MinBy(m => Math.Pow(m.BoundsPx.X + (m.BoundsPx.W / 2.0) - cx, 2) + Math.Pow(m.BoundsPx.Y + (m.BoundsPx.H / 2.0) - cy, 2))!;
    }

    /// <summary>A pixel rect → DIPs relative to <paramref name="m"/>'s work area.</summary>
    public static DipRect ToDip(RectPx r, MonitorInfo m)
    {
        ArgumentNullException.ThrowIfNull(m);
        return new DipRect
        {
            X = Math.Round((r.X - m.WorkPx.X) / m.Scale, 2),
            Y = Math.Round((r.Y - m.WorkPx.Y) / m.Scale, 2),
            W = Math.Round(r.W / m.Scale, 2),
            H = Math.Round(r.H / m.Scale, 2),
        };
    }

    /// <summary>DIPs relative to a work area → pixels on <paramref name="m"/> at its current DPI.</summary>
    public static RectPx FromDip(DipRect d, MonitorInfo m)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(m);
        return new RectPx(
            m.WorkPx.X + (int)Math.Round(d.X * m.Scale),
            m.WorkPx.Y + (int)Math.Round(d.Y * m.Scale),
            (int)Math.Round(d.W * m.Scale),
            (int)Math.Round(d.H * m.Scale));
    }

    /// <summary>
    /// Where a stored placement lands now: on its monitor if present; else on the primary, shrunk if it does not fit;
    /// always clamped inside the target work area (never off-screen).
    /// </summary>
    public (MonitorInfo Monitor, RectPx Rect) Resolve(PresetPlace place)
    {
        ArgumentNullException.ThrowIfNull(place);
        if (Find(place.Monitor) is { } m)
        {
            return (m, Clamp(FromDip(place.Rect, m), m.WorkPx));
        }

        var p = Primary;
        var r = FromDip(place.Rect, p);
        var fx = r.W > p.WorkPx.W ? (double)p.WorkPx.W / r.W : 1.0;
        var fy = r.H > p.WorkPx.H ? (double)p.WorkPx.H / r.H : 1.0;
        var f = Math.Min(fx, fy);
        r = new RectPx(r.X, r.Y, (int)Math.Round(r.W * f), (int)Math.Round(r.H * f));
        return (p, Clamp(r, p.WorkPx));
    }

    /// <summary>Move (and if needed shrink) <paramref name="r"/> so it lies inside <paramref name="area"/>.</summary>
    public static RectPx Clamp(RectPx r, RectPx area)
    {
        var w = Math.Clamp(r.W, 1, Math.Max(1, area.W));
        var h = Math.Clamp(r.H, 1, Math.Max(1, area.H));
        var x = Math.Clamp(r.X, area.X, area.Right - w);
        var y = Math.Clamp(r.Y, area.Y, area.Bottom - h);
        return new RectPx(x, y, w, h);
    }
}

/// <summary>Values of <c>SHQueryUserNotificationState</c> (QUERY_USER_NOTIFICATION_STATE).</summary>
public enum NotificationState
{
    NotPresent = 1,
    Busy = 2,
    D3dFullScreen = 3,
    PresentationMode = 4,
    AcceptsNotifications = 5,
    QuietTime = 6,
    App = 7,
}

/// <summary>
/// F7 / D-W9: <c>state.fullscreen</c> from one <c>SHQueryUserNotificationState</c> read and one rect compare on each
/// foreground change. Exclusive D3D wins; presentation mode next; a non-desktop, non-minimized foreground window whose
/// visible frame covers its whole monitor is borderless; otherwise none.
/// </summary>
public static class FullscreenClassifier
{
    public const string None = "none";
    public const string Borderless = "borderless";
    public const string Exclusive = "exclusive";
    public const string Presentation = "presentation";

    /// <summary>The desktop and taskbar surfaces cover a monitor but are not fullscreen apps.</summary>
    public static readonly HashSet<string> DesktopClasses = new(StringComparer.Ordinal) { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

    public static string Classify(NotificationState state, WindowInfo? foreground, MonitorMap monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        if (state == NotificationState.D3dFullScreen)
        {
            return Exclusive;
        }

        if (state == NotificationState.PresentationMode)
        {
            return Presentation;
        }

        if (foreground is null || (foreground.Class is { } c && DesktopClasses.Contains(c)) || foreground.Show == ShowState.Minimized)
        {
            return None;
        }

        return foreground.FramePx.Covers(monitors.Of(foreground.FramePx).BoundsPx) ? Borderless : None;
    }
}
