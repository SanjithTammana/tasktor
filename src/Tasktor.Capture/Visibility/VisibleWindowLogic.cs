using Tasktor.Capture.Deep;
using Tasktor.Contracts.Presets;
using Tasktor.Core.Windows;

namespace Tasktor.Capture.Visibility;

/// <summary>Computes on-screen areas of top-level windows in front-to-back z-order.</summary>
public static class VisibleWindowLogic
{
    public const int MaxWindows = 30;

    /// <summary>
    /// The input windows are in front-to-back order. Monitors use physical virtual-screen pixels; overlapping monitor
    /// bounds are treated as a union, so mirrored or partially overlapping displays are not counted twice. Unknown or
    /// excluded window states are ignored. An unidentifiable but otherwise eligible window still occludes those behind.
    /// </summary>
    public static VisibleSet Compute(DateTimeOffset at, IReadOnlyList<RectPx> monitors, IReadOnlyList<WindowInfo> windows, long foreground)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        ArgumentNullException.ThrowIfNull(windows);

        var screen = new List<Region>();
        foreach (var monitor in monitors)
        {
            if (!Region.TryCreate(monitor, out var rect)) continue;
            var additions = new List<Region> { rect };
            foreach (var existing in screen)
            {
                additions = Subtract(additions, existing);
                if (additions.Count == 0) break;
            }
            screen.AddRange(additions);
        }

        var screenPixels = Area(screen);
        var result = new List<VisibleWindow>();
        var occluders = new List<Region>();
        var seen = new HashSet<long>();
        var considered = 0;
        foreach (var window in windows)
        {
            if (window is null || window.Hwnd == 0 || !seen.Add(window.Hwnd) || !window.Visible || window.ToolWindow
                || !window.OnCurrentDesktop || window.Cloaked != Cloak.None
                || window.Show is not (ShowState.Normal or ShowState.Maximized)
                || !Region.TryCreate(window.FramePx, out var bounds)) continue;

            var fragments = new List<Region>();
            foreach (var monitor in screen)
            {
                if (Region.Intersection(bounds, monitor) is { } clipped) fragments.Add(clipped);
            }
            if (fragments.Count == 0) continue;
            if (considered == MaxWindows) break;
            considered++;

            foreach (var above in occluders)
            {
                fragments = Subtract(fragments, above);
                if (fragments.Count == 0) break;
            }

            var visible = Area(fragments);
            if (visible > 0 && !string.IsNullOrWhiteSpace(window.AppId))
                result.Add(new VisibleWindow(window.Hwnd, window.AppId, visible, window.Hwnd == foreground));
            occluders.Add(bounds);
        }

        return new VisibleSet(at, screenPixels, result);
    }

    private static long Area(IEnumerable<Region> rectangles)
    {
        long total = 0;
        foreach (var r in rectangles) total = checked(total + checked((r.Right - r.Left) * (r.Bottom - r.Top)));
        return total;
    }

    private static List<Region> Subtract(List<Region> source, Region cut)
    {
        var result = new List<Region>();
        foreach (var r in source)
        {
            if (Region.Intersection(r, cut) is not { } overlap)
            {
                result.Add(r);
                continue;
            }

            Add(r.Left, r.Top, r.Right, overlap.Top);
            Add(r.Left, overlap.Bottom, r.Right, r.Bottom);
            Add(r.Left, overlap.Top, overlap.Left, overlap.Bottom);
            Add(overlap.Right, overlap.Top, r.Right, overlap.Bottom);
        }
        return result;

        void Add(long left, long top, long right, long bottom)
        {
            if (left < right && top < bottom) result.Add(new Region(left, top, right, bottom));
        }
    }

    private readonly record struct Region(long Left, long Top, long Right, long Bottom)
    {
        public static bool TryCreate(RectPx rect, out Region result)
        {
            result = new Region(rect.X, rect.Y, (long)rect.X + rect.W, (long)rect.Y + rect.H);
            return rect.W > 0 && rect.H > 0;
        }

        public static Region? Intersection(Region a, Region b)
        {
            var r = new Region(Math.Max(a.Left, b.Left), Math.Max(a.Top, b.Top), Math.Min(a.Right, b.Right), Math.Min(a.Bottom, b.Bottom));
            return r.Left < r.Right && r.Top < r.Bottom ? r : null;
        }
    }
}
