using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Tasktor.Contracts.Cards;
using Tasktor.Hosts.Common.Render;
using Tasktor.Hosts.Common.Theme;
using Tasktor.Tests.Shared;

namespace Tasktor.Hosts.Common.Tests;

/// <summary>U4 amended D-G10/D-G11/D-G14: the shelf sentence, the edge grain, and the shared theme.</summary>
public sealed class ShelfTests
{
    private static readonly BindingResolver Resolver = new(JsonNode.Parse("""
        { "state": { "mem": "Memory is at 86%", "sev": "critical", "jobs": "train.py finishes at 10:36" } }
        """));

    private static Card C(string id, string? severity, string? verdict, params CardAction[] actions) => new()
    {
        Id = id,
        Title = id,
        Severity = severity is null ? null : Value.Literal(severity),
        Blocks = verdict is null ? [new MetricBlock { Label = "x", Value = Value.Literal(1) }] : [new BadgeBlock { Text = Value.Literal(verdict) }],
        Actions = actions,
    };

    private static CardAction A(string id, string? noun, int priority = 1) => new() { Id = id, Label = id, Action = id, Noun = noun, Priority = priority };

    [Fact]
    public void Calm_machine_is_all_quiet_plus_the_jobs_fact()
    {
        var s = ShelfComposer.Compose(
            [C("core.system", "normal", "Nothing is busy"), C("core.jobs", null, "train.py finishes at 10:36", A("job.end", "train.py"))],
            Resolver);

        Assert.True(s.Calm);
        Assert.Equal("All quiet. train.py finishes at 10:36.", s.Text);
        var noun = Assert.Single(s.Nouns);
        Assert.Equal("core.jobs:train.py", noun.Id);
        Assert.Equal("job.end", Assert.Single(noun.Actions).Id);
        Assert.Contains(s.Segments, seg => seg.Text == "train.py" && seg.Noun == noun.Id);
        Assert.Equal(0, s.More);
    }

    [Fact]
    public void Calm_without_jobs_is_two_words()
    {
        var s = ShelfComposer.Compose([C("core.system", null, "Nothing is busy")], Resolver);
        Assert.Equal(ShelfComposer.CalmOpening, s.Text);
        Assert.Empty(s.Nouns);
    }

    [Fact]
    public void Loud_cards_speak_worst_first_and_keep_provider_order_on_ties()
    {
        var s = ShelfComposer.Compose(
            [
                C("a", "warn", "Disk C: is 91% full"),
                C("b", "critical", "Memory is at 86%"),
                C("c", "warn", "Teams is not responding"),
                C("core.jobs", null, "train.py finishes at 10:36"),
            ],
            Resolver);

        Assert.Equal(Severity.Critical, s.Severity);
        Assert.Equal("Memory is at 86%. Disk C: is 91% full. Teams is not responding.", s.Text);
    }

    [Fact]
    public void At_most_three_clauses_the_rest_are_counted()
    {
        var cards = Enumerable.Range(1, 5).Select(i => C("w" + i, "warn", "Warning " + i)).ToList();
        var s = ShelfComposer.Compose(cards, Resolver);
        Assert.Equal("Warning 1. Warning 2. Warning 3.", s.Text);
        Assert.Equal(2, s.More);
    }

    [Fact]
    public void Verdict_is_the_highest_priority_badge_and_bindings_resolve()
    {
        var card = new Card
        {
            Id = "core.memory",
            Title = "Memory",
            Severity = Value.Binding("/state/sev"),
            Blocks =
            [
                new BadgeBlock { Text = Value.Literal("secondary"), Priority = 3 },
                new BadgeBlock { Text = Value.Binding("/state/mem"), Priority = 1 },
            ],
        };
        var s = ShelfComposer.Compose([card], Resolver);
        Assert.Equal(Severity.Critical, s.Severity);
        Assert.Equal("Memory is at 86%.", s.Text);
    }

    [Fact]
    public void Cards_without_a_verdict_or_with_a_missing_binding_stay_silent()
    {
        var unbound = new Card
        {
            Id = "x",
            Title = "x",
            Severity = Value.Literal("critical"),
            Blocks = [new BadgeBlock { Text = Value.Binding("/state/nope") }],
        };
        var s = ShelfComposer.Compose([C("m", "critical", null), unbound], Resolver);
        Assert.True(s.Calm);
        Assert.Equal(ShelfComposer.CalmOpening, s.Text);
    }

    [Fact]
    public void Existing_end_punctuation_is_kept()
    {
        var s = ShelfComposer.Compose([C("a", "warn", "Teams stopped responding!"), C("b", "warn", "Is the VPN up?")], Resolver);
        Assert.Equal("Teams stopped responding! Is the VPN up?", s.Text);
    }

    [Fact]
    public void Nouns_become_controls_once_with_their_actions_by_priority()
    {
        var s = ShelfComposer.Compose(
            [
                C("core.memory", "warn", "Chrome holds 5.7 GB; Chrome has 23 tabs",
                    A("eco", "Chrome", 2), A("lower", "Chrome", 1), A("tabs", "23 tabs"), A("ghost", "Firefox"), A("plain", null)),
            ],
            Resolver);

        Assert.Equal("Chrome holds 5.7 GB; Chrome has 23 tabs.", s.Text);
        Assert.Equal(["core.memory:Chrome", "core.memory:23 tabs"], s.Nouns.Select(n => n.Id));
        Assert.Equal(["lower", "eco"], s.Nouns[0].Actions.Select(a => a.Id));
        Assert.Equal(
            [("Chrome", "core.memory:Chrome"), (" holds 5.7 GB; Chrome has ", null), ("23 tabs", "core.memory:23 tabs"), (".", null)],
            s.Segments.Select(seg => (seg.Text, seg.Noun)));
    }

    [Fact]
    public void Row_and_plugin_actions_do_not_make_a_noun()
    {
        var row = new CardAction { Id = "end", Label = "End", Action = "app.end", Noun = "Chrome", Target = new Dictionary<string, string> { ["app"] = "{row}" } };
        var plugin = new CardAction { Id = "sync", Label = "Sync", Noun = "Drive" };
        var s = ShelfComposer.Compose([C("m", "warn", "Chrome holds 5.7 GB; Drive is syncing", row, plugin)], Resolver);
        Assert.Empty(s.Nouns);
        Assert.Equal("Chrome holds 5.7 GB; Drive is syncing.", s.Text);
    }

    [Fact]
    public void Overlapping_nouns_keep_the_earlier_one()
    {
        var s = ShelfComposer.Compose([C("j", "warn", "train.py stalled", A("a", "train.py"), A("b", "py stalled"))], Resolver);
        Assert.Equal("j:train.py", Assert.Single(s.Nouns).Id);
        Assert.Equal("train.py stalled.", s.Text);
    }

    [Fact]
    public void Grain_has_a_tick_every_four_px_and_never_vanishes()
    {
        var ticks = EdgeGrain.Compute([0, 0, 0], [false, false, false], 1280);
        Assert.Equal(320, ticks.Count);
        Assert.All(ticks, t => Assert.Equal(EdgeGrain.MinHeight, t.Height, 9));
        Assert.Equal(1276, ticks[^1].X);
    }

    [Fact]
    public void Grain_interpolates_load_and_follows_the_nearer_minute_for_brightness()
    {
        var ticks = EdgeGrain.Compute([0, 1], [false, true], 12); // three ticks at pos 0, 0.5, 1
        Assert.Equal([0, 4, 8], ticks.Select(t => t.X));
        Assert.Equal(0.2, ticks[0].Height, 9);
        Assert.Equal(0.6, ticks[1].Height, 9);
        Assert.Equal(1.0, ticks[2].Height, 9);
        Assert.Equal([false, true, true], ticks.Select(t => t.Hot));
    }

    [Fact]
    public void Grain_clamps_bad_samples_and_handles_empty_history()
    {
        Assert.All(EdgeGrain.Compute([double.NaN, 7, -3], [false, false, false], 12), t => Assert.InRange(t.Height, 0.2, 1.0));
        Assert.Equal([1.0, 1.0, 1.0], EdgeGrain.Compute([7, 7, 7], [false, false, false], 12).Select(t => t.Height));
        Assert.All(EdgeGrain.Compute([], [], 40), t => Assert.False(t.Hot));
        Assert.Empty(EdgeGrain.Compute([0.5], [false], 3));
        Assert.Throws<ArgumentException>(() => EdgeGrain.Compute([0.5], [], 40));
    }

    [Fact]
    public void Series_pull_becomes_load()
    {
        Assert.Equal([0.25, 0.0, 1.0], EdgeGrain.LoadFromSeries(JsonNode.Parse("""{ "id": "system.cpu_pct", "hour": [25, null, 100] }""")));
        Assert.Empty(EdgeGrain.LoadFromSeries(null));
        Assert.Empty(EdgeGrain.LoadFromSeries(JsonNode.Parse("{}")));
    }

    [Fact]
    public void Series_severity_becomes_bright_flags_aligned_to_now()
    {
        var pulled = JsonNode.Parse("""{ "hour": [1, 2, 3, 4], "severity": [2, 0, 1] }""");
        Assert.Equal([false, true, false, true], EdgeGrain.HotFromSeries(pulled, 4));
        Assert.Equal([false, true], EdgeGrain.HotFromSeries(pulled, 2));
        Assert.Equal([false, false], EdgeGrain.HotFromSeries(JsonNode.Parse("{}"), 2));
    }

    [Fact]
    public void Contrast_matches_wcag_reference_values()
    {
        Assert.Equal(21.0, ThemeTokens.Contrast(Rgb.Parse("#000000"), Rgb.Parse("#FFFFFF")), 6);
        Assert.Equal(1.0, ThemeTokens.Contrast(Rgb.Parse("#777777"), Rgb.Parse("#777777")), 6);
        Assert.Equal(4.48, ThemeTokens.Contrast(Rgb.Parse("#777777"), Rgb.Parse("#FFFFFF")), 2);
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Every_foreground_token_reaches_text_contrast_on_every_surface(string theme)
    {
        var p = ThemeTokens.Palettes.Single(x => x.Name == theme);
        foreach (var (fg, fc) in p.Foregrounds)
        {
            foreach (var (bg, bc) in p.Backgrounds)
            {
                var ratio = ThemeTokens.Contrast(fc, bc);
                Assert.True(ratio >= ThemeTokens.MinTextContrast, $"{theme} {fg} on {bg}: {ratio:F2}");
            }
        }

        Assert.True(ThemeTokens.Contrast(p.OnAccent, p.Accent) >= ThemeTokens.MinTextContrast, theme + " OnAccent on Accent");
    }

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    // XAML takes named weights; these are their OpenType values.
    private static readonly Dictionary<string, int> Weights = new(StringComparer.Ordinal) { ["SemiLight"] = 350, ["Normal"] = 400, ["SemiBold"] = 600 };

    private static XDocument Xaml() => XDocument.Load(Path.Combine(RepoPaths.Root, "src", "Tasktor.Flyout", "Themes", "Tasktor.xaml"));

    private static XElement ThemeDictionary(XDocument doc, string key) =>
        doc.Root!.Element(P + "ResourceDictionary.ThemeDictionaries")!.Elements(P + "ResourceDictionary")
            .Single(d => (string?)d.Attribute(X + "Key") == key);

    [Theory]
    [InlineData("Default", "Dark")]
    [InlineData("Light", "Light")]
    public void Xaml_colours_equal_the_tokens(string key, string theme)
    {
        var palette = ThemeTokens.Palettes.Single(x => x.Name == theme);
        var dict = ThemeDictionary(Xaml(), key);
        var colours = dict.Elements(P + "Color").ToDictionary(e => (string)e.Attribute(X + "Key")!, e => Rgb.Parse(e.Value.Trim()));
        Assert.Equal(palette.Entries.Select(e => "Tasktor" + e.Name + "Color").Order(), colours.Keys.Order());
        foreach (var (name, colour) in palette.Entries)
        {
            Assert.Equal(colour, colours["Tasktor" + name + "Color"]);
        }
    }

    [Theory]
    [InlineData("Default")]
    [InlineData("Light")]
    [InlineData("HighContrast")]
    public void Every_theme_defines_every_brush(string key)
    {
        var brushes = ThemeDictionary(Xaml(), key).Elements(P + "SolidColorBrush").Select(e => (string)e.Attribute(X + "Key")!).Order();
        Assert.Equal(ThemeTokens.Dark.Entries.Select(e => "Tasktor" + e.Name + "Brush").Order(), brushes);
    }

    [Fact]
    public void High_contrast_uses_system_colours_only()
    {
        var hc = ThemeDictionary(Xaml(), "HighContrast");
        Assert.Empty(hc.Elements(P + "Color"));
        Assert.All(hc.Elements(P + "SolidColorBrush"), b => Assert.StartsWith("{ThemeResource SystemColor", (string)b.Attribute("Color")!, StringComparison.Ordinal));
    }

    [Fact]
    public void Xaml_type_ramp_and_spacing_equal_the_tokens()
    {
        var doc = Xaml();
        var styles = doc.Root!.Elements(P + "Style").ToDictionary(
            s => (string)s.Attribute(X + "Key")!,
            s => s.Elements(P + "Setter").ToDictionary(x => (string)x.Attribute("Property")!, x => (string)x.Attribute("Value")!));
        Assert.Equal(ThemeTokens.Ramp.Select(r => "Tasktor" + r.Name + "TextStyle").Order(), styles.Keys.Order());
        foreach (var step in ThemeTokens.Ramp)
        {
            var s = styles["Tasktor" + step.Name + "TextStyle"];
            Assert.Equal(step.Family, s["FontFamily"]);
            Assert.Equal(step.Size, double.Parse(s["FontSize"], CultureInfo.InvariantCulture));
            Assert.Equal(step.LineHeight, double.Parse(s["LineHeight"], CultureInfo.InvariantCulture));
            Assert.Equal(step.Weight, Weights[s["FontWeight"]]);
        }

        var spacing = doc.Root.Elements(X + "Double").ToDictionary(e => (string)e.Attribute(X + "Key")!, e => int.Parse(e.Value, CultureInfo.InvariantCulture));
        Assert.Equal(ThemeTokens.Spacing.Select(v => "TasktorSpace" + v.ToString(CultureInfo.InvariantCulture)).Order(), spacing.Keys.Order());
        Assert.All(spacing, kv => Assert.Equal("TasktorSpace" + kv.Value.ToString(CultureInfo.InvariantCulture), kv.Key));
    }

    [Fact]
    public void App_merges_the_theme()
    {
        var app = File.ReadAllText(Path.Combine(RepoPaths.Root, "src", "Tasktor.Flyout", "App.xaml"));
        Assert.Contains("ms-appx:///Themes/Tasktor.xaml", app, StringComparison.Ordinal);
    }
}
