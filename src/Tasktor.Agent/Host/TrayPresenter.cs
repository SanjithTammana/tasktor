using System.Text.Json.Nodes;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Snapshot;
using Tasktor.Core.Features;
using Tasktor.Hosts.Common.Render;
using Tasktor.Hosts.Common.Shelf;
using Tasktor.Hosts.Common.Theme;
using Tasktor.Windows.Shell;

namespace Tasktor.Agent.Host;

/// <summary>
/// U4 D-G4 / amended D-G10, D-G15: turns the agent's state into what <see cref="TrayHost"/> draws. Loop-only. Icons
/// are painted once per state and size and reused (the host recreates an icon only when the array changes); the edge
/// is repainted only when <see cref="Edge"/> is called (each 60 s tick and on display changes).
/// </summary>
internal sealed class TrayPresenter
{
    public const string RecordingLine = "Recording typing and screen text";
    private const int MaxSentence = 96;

    private readonly Dictionary<(TrayIconState, int, bool), uint[]> _icons = [];

    /// <summary>
    /// Recording wins (the ring must always show while capture records, D-G6), then paused tracking, then the worst
    /// card severity.
    /// </summary>
    public static TrayIconState StateOf(SnapshotState state, AgentInfo? agent, Severity worst, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (Recording(state.Capture, now))
        {
            return TrayIconState.Recording;
        }

        if (agent?.Paused?.Tracking == true)
        {
            return TrayIconState.Paused;
        }

        return worst switch
        {
            Severity.Critical => TrayIconState.Critical,
            Severity.Warn => TrayIconState.Caution,
            _ => TrayIconState.Normal,
        };
    }

    public static bool Recording(CaptureState? capture, DateTimeOffset now) =>
        capture is { On: true, PausedIndefinitely: false } c && (c.PausedUntil is not { } until || until <= now);

    /// <summary>The tooltip is also the icon's accessible name; ≤ 127 characters, the recording line first while it records.</summary>
    public static string Tooltip(string glance, bool recording)
    {
        var text = recording ? RecordingLine + " · " + glance : glance;
        return text.Length <= 127 ? text : text[..127];
    }

    /// <summary>Windows' own menu, led by the sentence (amended D-G15), with the tray rows of D-G4 that have actions today.</summary>
    public static IReadOnlyList<TrayMenuItem> Menu(string sentence, SnapshotState state, AgentInfo? agent, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        static ActionRequest Act(string action, string? arg = null, string? value = null) => new()
        {
            Action = action,
            Origin = Origin.Tray,
            Args = arg is null ? null : new Dictionary<string, string> { [arg] = value! },
        };

        var lead = sentence.Length <= MaxSentence ? sentence : sentence[..(MaxSentence - 1)] + "…";
        var items = new List<TrayMenuItem>
        {
            new(lead, Enabled: false),
            TrayMenuItem.Separator,
            new("Open Tasktor", Act("open")),
            agent?.Paused?.Tracking == true ? new("Resume tracking", Act("tracking.resume")) : new("Pause tracking", Act("tracking.pause")),
        };

        if (state.Capture is { On: true } capture)
        {
            if (Recording(capture, now))
            {
                items.Add(new TrayMenuItem("Pause capture", Children:
                [
                    new("For 15 minutes", Act("capture.pause", "for", "15m")),
                    new("For 1 hour", Act("capture.pause", "for", "1h")),
                    new("Until tomorrow", Act("capture.pause", "for", "today")),
                    new("Until I resume", Act("capture.pause", "for", "indef")),
                ]));
                items.Add(new TrayMenuItem("Don't capture this app", Act("capture.exclude", "app", CaptureFeature.Foreground)));
            }
            else
            {
                items.Add(new TrayMenuItem("Resume capture", Act("capture.resume")));
            }
        }

        items.Add(new TrayMenuItem("Snooze alerts for 1 hour", Act("alerts.snooze_all")));
        items.Add(TrayMenuItem.Separator);
        items.Add(new TrayMenuItem("Quit Tasktor", Act("quit")));
        return items;
    }

    /// <summary>The shelf sentence for the menu's first row, from the snapshot's cards and state.</summary>
    public static string Sentence(IReadOnlyList<Card> cards, JsonObject state) =>
        ShelfComposer.Compose(cards, new BindingResolver(new JsonObject { ["state"] = state.DeepClone() })).Text;

    public static Palette PaletteFor(TrayDisplay display) => display.LightTaskbar ? ThemeTokens.Light : ThemeTokens.Dark;

    /// <summary>The icon pixels for a state, painted once per state, size and theme.</summary>
    public uint[] Icon(TrayIconState state, TrayDisplay display)
    {
        ArgumentNullException.ThrowIfNull(display);
        var key = (state, display.IconSize, display.LightTaskbar);
        if (!_icons.TryGetValue(key, out var px))
        {
            px = TrayArt.Icon(state, open: false, display.IconSize, PaletteFor(display));
            _icons[key] = px;
        }

        return px;
    }

    /// <summary>The edge strip for the last hour: load 0–100 % per minute and the minute's worst severity (0 / 1 / 2).</summary>
    public static TrayEdgeImage Edge(TrayDisplay display, IReadOnlyList<float> loadPct, IReadOnlyList<byte> severity)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(loadPct);
        ArgumentNullException.ThrowIfNull(severity);
        var placement = ShelfLayout.Place(
            new PxRect(display.Monitor.X, display.Monitor.Y, display.Monitor.Width, display.Monitor.Height),
            new PxRect(display.Work.X, display.Work.Y, display.Work.Width, display.Work.Height),
            display.Scale,
            0);
        var rect = placement.Edge;
        var load = loadPct.Select(v => v / 100.0).ToList();
        var hot = new bool[load.Count];
        for (int i = hot.Length - 1, j = severity.Count - 1; i >= 0 && j >= 0; i--, j--)
        {
            hot[i] = severity[j] >= 1; // both series end at "now"
        }

        var ticks = EdgeGrain.Compute(load, hot, (int)(rect.Width / display.Scale));
        var pixels = TrayArt.Edge(ticks, display.Scale, rect.Width, rect.Height, PaletteFor(display));
        return new TrayEdgeImage(new TrayRect(rect.X, rect.Y, rect.Width, rect.Height), pixels);
    }
}
