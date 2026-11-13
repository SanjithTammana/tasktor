using Microsoft.UI.Windowing;
using Tasktor.Hosts.Common.Command;
using Tasktor.Hosts.Common.Deep;
using Windows.Graphics;

namespace Tasktor.Flyout;

/// <summary>
/// The display the shelf or the command line opens on: the main one when the user chose it (<see cref="ScreenSetting"/>),
/// else the active window's (<see cref="TargetMonitor"/>), else the main one.
/// </summary>
internal static class GlanceDisplay
{
    /// <summary>Read once per show, before the glance window takes the foreground.</summary>
    public static DisplayArea Target(System.Text.Json.Nodes.JsonNode? root) =>
        ScreenSetting.Effective(root) == ScreenSetting.Main ? DisplayArea.Primary
        : TargetMonitor.Point() is { } p ? DisplayArea.GetFromPoint(new PointInt32(p.X, p.Y), DisplayAreaFallback.Primary)
        : DisplayArea.Primary;

    /// <summary>
    /// The area to lay out on now: <paramref name="area"/> while that screen is still connected, else a fresh choice (a
    /// laptop undocked while the shelf was open).
    /// </summary>
    public static DisplayArea Current(DisplayArea? area, System.Text.Json.Nodes.JsonNode? root)
    {
        if (area is not null)
        {
            var id = area.DisplayId.Value;
            var all = DisplayArea.FindAll(); // indexed: enumerating this WinRT list throws in some Windows App SDK builds
            for (var i = 0; i < all.Count; i++)
            {
                if (all[i].DisplayId.Value == id)
                {
                    return all[i];
                }
            }
        }

        return Target(root);
    }

    /// <summary>How many screens are connected now.</summary>
    public static int Count() => DisplayArea.FindAll().Count;
}
