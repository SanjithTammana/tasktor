using System.Text.Json.Nodes;
using Tasktor.Agent.Host;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Snapshot;
using Tasktor.Core.Actions;
using Tasktor.Hosts.Common.Render;
using Tasktor.Hosts.Common.Theme;
using Tasktor.Windows.Shell;

namespace Tasktor.Agent.Tests;

/// <summary>U4 D-G4 / amended D-G10, D-G15: what the tray shows, from the agent's state.</summary>
public sealed class TrayPresenterTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly TrayDisplay Display = new(new TrayRect(0, 0, 1920, 1080), new TrayRect(0, 0, 1920, 1032), 1.0, 16, LightTaskbar: false);

    private static AgentInfo Agent(bool trackingPaused = false) => new() { Version = "0.1.0", Paused = new PausedState { Tracking = trackingPaused } };

    private static SnapshotState Capturing(bool on = true, DateTimeOffset? until = null, bool indef = false) =>
        new() { Capture = new CaptureState { On = on, PausedUntil = until, PausedIndefinitely = indef } };

    [Fact]
    public void Recording_wins_then_paused_then_severity()
    {
        Assert.Equal(TrayIconState.Recording, TrayPresenter.StateOf(Capturing(), Agent(trackingPaused: true), Severity.Critical, Now));
        Assert.Equal(TrayIconState.Paused, TrayPresenter.StateOf(new SnapshotState(), Agent(trackingPaused: true), Severity.Critical, Now));
        Assert.Equal(TrayIconState.Critical, TrayPresenter.StateOf(new SnapshotState(), Agent(), Severity.Critical, Now));
        Assert.Equal(TrayIconState.Caution, TrayPresenter.StateOf(new SnapshotState(), Agent(), Severity.Warn, Now));
        Assert.Equal(TrayIconState.Normal, TrayPresenter.StateOf(new SnapshotState(), null, Severity.Normal, Now));
    }

    [Fact]
    public void Paused_capture_is_not_recording_until_its_pause_ends()
    {
        Assert.False(TrayPresenter.Recording(Capturing(until: Now.AddMinutes(5)).Capture, Now));
        Assert.True(TrayPresenter.Recording(Capturing(until: Now.AddMinutes(-1)).Capture, Now));
        Assert.False(TrayPresenter.Recording(Capturing(indef: true).Capture, Now));
        Assert.False(TrayPresenter.Recording(Capturing(on: false).Capture, Now));
        Assert.False(TrayPresenter.Recording(null, Now));
    }

    [Fact]
    public void Tooltip_leads_with_recording_and_stays_within_127()
    {
        Assert.Equal("CPU 34%", TrayPresenter.Tooltip("CPU 34%", recording: false));
        Assert.Equal(TrayPresenter.RecordingLine + " · CPU 34%", TrayPresenter.Tooltip("CPU 34%", recording: true));
        Assert.Equal(127, TrayPresenter.Tooltip(new string('x', 200), recording: true).Length);
    }

    private static List<string> Labels(IReadOnlyList<TrayMenuItem> items) => [.. items.Select(i => i.IsSeparator ? "-" : i.Label)];

    [Fact]
    public void Menu_is_led_by_the_sentence_and_every_row_is_a_tray_action()
    {
        var menu = TrayPresenter.Menu("All quiet.", Capturing(), Agent(), Now);
        Assert.Equal(
            ["All quiet.", "-", "Open Tasktor", "Pause tracking", "Pause capture", "Don't capture this app", "Snooze alerts for 1 hour", "-", "Quit Tasktor"],
            Labels(menu));
        Assert.False(menu[0].Enabled);

        var all = menu.SelectMany(i => i.Children ?? [i]).Where(i => i.Action is not null).ToList();
        Assert.All(all, i =>
        {
            Assert.Equal(Contracts.Actions.Origin.Tray, i.Action!.Origin);
            var def = ActionCatalog.All.Single(a => a.Id == i.Action.Action);
            Assert.True((def.Surfaces & Surface.Tray) != 0, def.Id + " is not offered on the tray");
        });
        Assert.Equal(["15m", "1h", "today", "indef"], menu.Single(i => i.Label == "Pause capture").Children!.Select(c => c.Action!.Args!["for"]));
    }

    [Fact]
    public void Menu_follows_the_pause_states_and_hides_capture_when_off()
    {
        Assert.Contains("Resume tracking", Labels(TrayPresenter.Menu("x", new SnapshotState(), Agent(trackingPaused: true), Now)));
        var paused = Labels(TrayPresenter.Menu("x", Capturing(indef: true), Agent(), Now));
        Assert.Contains("Resume capture", paused);
        Assert.DoesNotContain("Pause capture", paused);
        var off = Labels(TrayPresenter.Menu("x", Capturing(on: false), Agent(), Now));
        Assert.DoesNotContain(off, l => l.Contains("capture", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_long_sentence_is_shortened_for_the_menu()
    {
        var lead = TrayPresenter.Menu(new string('a', 300), new SnapshotState(), Agent(), Now)[0].Label;
        Assert.Equal(96, lead.Length);
        Assert.EndsWith("…", lead, StringComparison.Ordinal);
    }

    [Fact]
    public void Sentence_comes_from_the_shelf_composer()
    {
        var card = new Card { Id = "core.memory", Title = "Memory", Severity = Value.Literal("warn"), Blocks = [new BadgeBlock { Text = Value.Binding("/state/x") }] };
        Assert.Equal("Memory is at 86%.", TrayPresenter.Sentence([card], JsonNode.Parse("""{ "x": "Memory is at 86%" }""")!.AsObject()));
    }

    [Fact]
    public void Icons_are_painted_once_per_state_size_and_theme()
    {
        var p = new TrayPresenter();
        var a = p.Icon(TrayIconState.Caution, Display);
        Assert.Same(a, p.Icon(TrayIconState.Caution, Display));
        Assert.NotSame(a, p.Icon(TrayIconState.Normal, Display));
        Assert.NotSame(a, p.Icon(TrayIconState.Caution, Display with { LightTaskbar = true }));
        Assert.Equal(16 * 16, a.Length);
    }

    [Fact]
    public void Edge_sits_on_the_taskbar_and_lights_the_minutes_with_warnings()
    {
        float[] load = [.. Enumerable.Repeat(50f, 60)];
        byte[] sev = [.. Enumerable.Repeat((byte)0, 59), 2];
        var edge = TrayPresenter.Edge(Display, load, sev);
        Assert.Equal(new TrayRect(0, 1032, 1920, 6), edge.Bounds);
        Assert.Equal(1920 * 6, edge.Pixels.Length);
        var bright = TrayArt.Opaque(ThemeTokens.Dark.GlanceInk);
        var plain = TrayArt.Opaque(ThemeTokens.Dark.GlanceInk3);
        Assert.Contains(bright, edge.Pixels[(5 * 1920)..]);
        Assert.Contains(plain, edge.Pixels[(5 * 1920)..]);
        var lastBright = Array.LastIndexOf(edge.Pixels, bright) % 1920;
        Assert.True(lastBright > 1900, "the bright stretch should be at the right, now: " + lastBright);
    }
}
