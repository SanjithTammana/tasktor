using Tasktor.Contracts.Plugins;

namespace Tasktor.Core.Plugins;

/// <summary>The consent dialog's plain line for each capability (plugin contract 2, S5).</summary>
public static class PluginCapabilityText
{
    /// <summary>The JSON name, as in <c>plugin.json</c>.</summary>
    public static string Name(PluginCapability c) => PluginCapabilities.Names[(int)c];

    /// <summary>One line, in the words the user sees; <paramref name="watchExes"/> narrows the process and window lines.</summary>
    public static string Line(PluginCapability c, IReadOnlyList<string>? watchExes)
    {
        var which = watchExes is { Count: > 0 } w ? JoinOr(w) : "any program";
        return c switch
        {
            PluginCapability.Page => "Show a page in the Tasktor window",
            PluginCapability.Notify => "Send notifications (up to 12 an hour; quiet hours apply)",
            PluginCapability.Timer => "Wake itself up at a time it picks",
            PluginCapability.State => "Keep a few saved values that rules can read",
            PluginCapability.EventsProcess => "See when " + which + " starts and stops",
            PluginCapability.EventsForeground => "See which program is in front (not window titles)",
            PluginCapability.EventsNetwork => "See when you go online or offline",
            PluginCapability.EventsWindows => "See when windows of " + which + " open and close (not their titles)",
            PluginCapability.ReadListeners => "See which programs listen on network ports",
            PluginCapability.ReadJobs => "See your Tasktor jobs and their progress",
            PluginCapability.ActJob => "Pause and resume your jobs",
            PluginCapability.ActArrange => "Arrange windows with your saved presets (asks you the first time for each)",
            PluginCapability.BrowserTabs => "See the site and title of your active browser tab, while capture is on",
            PluginCapability.BrowserBlock => "Block sites in your browser until a time it sets (up to 12 hours)",
            PluginCapability.BrowserCard => "Show a card in the Tasktor browser extension",
            PluginCapability.CaptureBrowser => "See the page text capture records from sites, live while capture is on (no history)",
            PluginCapability.Resident => "Keep running between wakes",
            _ => Name(c),
        };
    }

    private static string JoinOr(IReadOnlyList<string> names) =>
        names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " or " + names[^1];
}
