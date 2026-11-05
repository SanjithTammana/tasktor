using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tasktor.Contracts;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Pipe;
using Tasktor.Hosts.Common.Pipe;
using Tasktor.Hosts.Common.Render;
using Tasktor.Tests.Shared;

namespace Tasktor.Hosts.Common.Tests;

/// <summary>U4 D-G1 / D-G5: resolve, fit per size, Adaptive Card template + data, the common rules.</summary>
public sealed partial class RenderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);

    private static readonly JsonNode Snapshot = JsonNode.Parse("""
        { "state": { "system": { "cpu_pct": 34.4, "ram_used_pct": 71.2, "verdict": "Busy: Chrome" },
          "apps": [ { "app_id": "chrome", "name": "Chrome", "cpu_pct": 22.5 }, { "app_id": "code", "name": "Code", "cpu_pct": 9.1 },
                    { "app_id": "teams", "name": "Teams", "cpu_pct": 3.0 }, { "app_id": "slack", "name": "Slack", "cpu_pct": 1.2 } ],
          "sev": "warn", "items": ["one", "two", "three"] } }
        """)!;

    private static Card SystemCard(int? staleAfter = null, DateTimeOffset? updated = null) => new()
    {
        Id = "core.system",
        Source = new CardSource { Kind = CardSourceKind.Core },
        Title = "System",
        Severity = Value.Binding("/state/sev"),
        StaleAfterS = staleAfter,
        UpdatedAt = updated,
        Blocks =
        [
            new MetricBlock { Label = "CPU", Value = Value.Binding("/state/system/cpu_pct"), Unit = "%", Priority = 1, Trend = Trend.Up },
            new MetricBlock { Label = "RAM", Value = Value.Binding("/state/system/ram_used_pct"), Unit = "%", Priority = 2 },
            new BadgeBlock { Text = Value.Binding("/state/system/verdict"), Priority = 3, Severity = Value.Literal("warn") },
            new SparklineBlock { Label = "CPU", Points = [10, 20, 35, 30, 34], Unit = "%", Priority = 5 },
            new TableBlock
            {
                Label = "Top apps",
                Columns = [new TableColumn { Key = "name", Label = "App" }, new TableColumn { Key = "cpu_pct", Label = "CPU", Unit = "%" }],
                Rows = Value.Binding("/state/apps"),
                RowKey = "app_id",
                Max = 3,
                Priority = 4,
            },
            new BarBlock { Label = "Disk C:", Value = Value.Literal(80), Max = Value.Literal(100), Unit = "%", Priority = 6 },
            new TreeBlock { Table = "processes", Priority = 7 },
        ],
        Actions =
        [
            new CardAction { Id = "open", Label = "Open", Action = "open", Priority = 1 },
            new CardAction { Id = "lower", Label = "Lower", Action = "priority.set", Target = new Dictionary<string, string> { ["app"] = "{row}" }, Priority = 2 },
            new CardAction { Id = "end", Label = "End", Action = "app.end", Target = new Dictionary<string, string> { ["app"] = "{row}" }, Priority = 3, Destructive = true },
            new CardAction { Id = "eco", Label = "Efficiency", Action = "efficiency.set", Priority = 4 },
            new CardAction { Id = "tm", Label = "Task Manager", Action = "proc.open_taskmgr", Priority = 5 },
        ],
    };

    [Theory]
    [InlineData(FitTarget.Small, 2, 0)]
    [InlineData(FitTarget.Medium, 5, 3)]
    [InlineData(FitTarget.Large, 9, 4)]
    public void Fitting_keeps_the_highest_priority_blocks_within_each_budget(FitTarget target, int units, int touch)
    {
        var fitted = CardFitter.Fit(SystemCard(), target, new BindingResolver(Snapshot));
        Assert.True(fitted.UnitsUsed <= units);
        Assert.Equal(touch, fitted.Actions.Count);
        Assert.DoesNotContain(fitted.Blocks, b => b.Block is TreeBlock); // tree is flyout only
        Assert.Equal("CPU", Assert.IsType<MetricBlock>(fitted.Blocks[0].Block).Label);
        if (target == FitTarget.Small)
        {
            Assert.Equal(["CPU", "RAM"], fitted.Blocks.Select(b => ((MetricBlock)b.Block).Label));
        }

        if (target == FitTarget.Medium)
        {
            // CPU, RAM, badge = 3 units; the table is cut to the 2 rows that fit; lower-priority blocks drop.
            var table = Assert.Single(fitted.Blocks, b => b.Block is TableBlock);
            Assert.Equal(2, table.Rows);
            Assert.DoesNotContain(fitted.Blocks, b => b.Block is SparklineBlock);
        }

        if (target == FitTarget.Large)
        {
            Assert.Equal(3, fitted.Blocks.Single(b => b.Block is TableBlock).Rows);
            Assert.Contains(fitted.Blocks, b => b.Block is BarBlock);
            Assert.Equal(["open", "priority.set", "app.end", "efficiency.set"], fitted.Actions.Select(a => a.Action));
        }
    }

    [Fact]
    public void The_flyout_keeps_everything_and_ties_keep_the_providers_order()
    {
        var fitted = CardFitter.Fit(SystemCard(), FitTarget.Flyout, new BindingResolver(Snapshot));
        Assert.Equal(7, fitted.Blocks.Count);
        Assert.Equal(5, fitted.Actions.Count);
        var tie = new Card { Id = "x", Title = "X", Blocks = [new BadgeBlock { Text = Value.Literal("a") }, new BadgeBlock { Text = Value.Literal("b") }, new BadgeBlock { Text = Value.Literal("c") }] };
        Assert.Equal(["\"a\"", "\"b\""], CardFitter.Fit(tie, FitTarget.Small, new BindingResolver(null)).Blocks.Select(b => ((BadgeBlock)b.Block).Text.ToString()));
    }

    [Fact]
    public void A_missing_binding_renders_a_dash_and_is_reported_once()
    {
        var missing = new List<string>();
        var r = new BindingResolver(Snapshot) { OnMissing = missing.Add };
        Assert.Equal(BindingResolver.Missing, r.Text(Value.Binding("/state/nope")));
        Assert.Equal(BindingResolver.Missing, r.Text(Value.Binding("/state/nope")));
        Assert.Equal(["/state/nope"], missing);
        Assert.Equal("34", r.Text(Value.Binding("/state/system/cpu_pct")));
        Assert.Equal("System: CPU 34 % · warning", CardFitter.Glance(SystemCard(), r));
    }

    private static string Golden(string name, AdaptiveCardOutput output)
    {
        var dir = Path.Combine(RepoPaths.Root, "tests", "fixtures", "render");
        var path = Path.Combine(dir, name + ".json");
        var json = new JsonObject { ["template"] = output.Template.DeepClone(), ["data"] = output.Data.DeepClone() }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        if (Environment.GetEnvironmentVariable("TASKTOR_REGEN_FIXTURES") == "1")
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, json);
        }

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(path)), JsonNode.Parse(json)), "render differs from tests/fixtures/render/" + name + ".json");
        return json;
    }

    [Theory]
    [InlineData(FitTarget.Small, "system-small")]
    [InlineData(FitTarget.Medium, "system-medium")]
    [InlineData(FitTarget.Large, "system-large")]
    public void The_system_card_renders_its_golden_template_and_data(FitTarget target, string golden)
    {
        var r = new BindingResolver(Snapshot);
        var renderer = new AdaptiveCardRenderer(() => Now, id => id == "app.end", TimeZoneInfo.Utc);
        var output = renderer.Render(CardFitter.Fit(SystemCard(), target, r), r);
        Assert.Equal("1.5", (string)output.Template["version"]!);
        Assert.Equal("tasktor://open?card=core.system", (string)output.Template["selectAction"]!["url"]!);
        Golden(golden, output);
        Assert.DoesNotContain("\"color\":\"Attention\"", output.TemplateJson, StringComparison.Ordinal); // warn, not critical
        Assert.Equal("Warning", (string)output.Data["severity"]!["word"]!); // severity is a word, not colour alone
    }

    [Fact]
    public void Destructive_actions_say_end_and_deep_link_to_the_confirm_step_and_reversible_ones_execute_in_place()
    {
        var r = new BindingResolver(Snapshot);
        var output = new AdaptiveCardRenderer(() => Now, _ => false, TimeZoneInfo.Utc).Render(CardFitter.Fit(SystemCard(), FitTarget.Large, r), r);
        var actions = output.Template["actions"]!.AsArray();
        var end = actions.Single(a => (string?)a!["title"] == "End…")!;
        Assert.Equal("Action.OpenUrl", (string)end["type"]!);
        Assert.Equal("tasktor://open?card=core.system&action=app.end&confirm=1", (string)end["url"]!);
        var lower = actions.Single(a => (string?)a!["title"] == "Lower")!;
        Assert.Equal("Action.Execute", (string)lower["type"]!);
        Assert.Equal("priority.set", (string)lower["verb"]!);
        Assert.Equal("{row}", (string)lower["data"]!["target"]!["app"]!);
    }

    [Fact]
    public void Stale_cards_grey_out_with_as_of_and_plugin_cards_carry_their_source()
    {
        var r = new BindingResolver(Snapshot);
        var renderer = new AdaptiveCardRenderer(() => Now, null, TimeZoneInfo.Utc);
        var stale = renderer.Render(CardFitter.Fit(SystemCard(60, Now.AddMinutes(-5)), FitTarget.Medium, r), r);
        Assert.True((bool)stale.Data["stale"]!);
        Assert.Equal("as of 08:55", (string)stale.Data["as_of"]!);
        Assert.Contains("\"isSubtle\":\"${stale}\"", stale.TemplateJson, StringComparison.Ordinal);
        var fresh = renderer.Render(CardFitter.Fit(SystemCard(60, Now.AddSeconds(-10)), FitTarget.Medium, r), r);
        Assert.False((bool)fresh.Data["stale"]!);

        var plugin = new Card
        {
            Id = "plugin.todo-count.main",
            Source = new CardSource { Kind = CardSourceKind.Plugin, Plugin = "todo-count", Name = "TODO count" },
            Title = "TODOs",
            Blocks = [new MetricBlock { Label = "Open", Value = Value.Literal(4) }, new ListBlock { Items = Value.Binding("/state/items"), Max = 5 }],
        };
        var p = renderer.Render(CardFitter.Fit(plugin, FitTarget.Large, r), r);
        Assert.Equal("from TODO count", (string)p.Data["source"]!);
        Golden("plugin-large", p);
    }

    [Fact]
    public void Every_block_kind_renders_with_its_values_in_the_data_only()
    {
        var card = new Card
        {
            Id = "test.all",
            Title = "All blocks",
            Severity = Value.Literal("critical"),
            Blocks =
            [
                new AlertBlock { Text = Value.Literal("Disk almost full"), Severity = Value.Literal("critical") },
                new CountdownBlock { Label = "Standup", Until = Value.Literal("2026-11-04T09:12:00Z") },
                new GaugeBlock { Label = "Temp", Value = Value.Literal(70), Min = 20, Max = 120, Unit = "°C" },
                new ProgressBlock { Label = "Embedding", Done = Value.Literal(42), Total = Value.Literal(100), Unit = "docs" },
                new ActionsBlock { Actions = ["open"] },
            ],
        };
        var r = new BindingResolver(Snapshot);
        var output = new AdaptiveCardRenderer(() => Now, null, TimeZoneInfo.Utc).Render(CardFitter.Fit(card, FitTarget.Flyout, r), r);
        Assert.Equal("in 12 min", (string)output.Data["b1"]!["text"]!);
        Assert.Equal(50, (int)output.Data["b2"]!["fill"]!);
        Assert.Equal("42 / 100 docs", (string)output.Data["b3"]!["value"]!);
        Assert.Equal("Critical", (string)output.Data["severity"]!["word"]!);
        Assert.DoesNotContain("Disk almost full", output.TemplateJson, StringComparison.Ordinal); // values live in the data
        Assert.DoesNotContain("42 / 100", output.TemplateJson, StringComparison.Ordinal);
        Golden("all-blocks-flyout", output);
    }

    [Fact]
    public void One_template_serves_all_three_widget_sizes()
    {
        var r = new BindingResolver(Snapshot);
        var all = new AdaptiveCardRenderer(() => Now, _ => false, TimeZoneInfo.Utc).RenderAllSizes(SystemCard(), r);
        var whens = all.Template["body"]!.AsArray().Select(c => (string)c!["$when"]!).ToList();
        Assert.Equal(["${$host.widgetSize==\"small\"}", "${$host.widgetSize==\"medium\"}", "${$host.widgetSize==\"large\"}"], whens);
        Assert.Contains("${small.b0.value}", all.TemplateJson, StringComparison.Ordinal);
        Assert.NotNull(all.Data["large"]!["b0"]);
    }

    [Fact]
    public void Sparklines_and_countdowns()
    {
        Assert.Equal("▁▅█", AdaptiveCardRenderer.Spark([0, 5, 10])); // the midpoint of 8 levels rounds to the 5th
        Assert.Equal("▁▁", AdaptiveCardRenderer.Spark([3, 3]));
        var renderer = new AdaptiveCardRenderer(() => Now);
        Assert.Equal("now", renderer.Countdown(JsonValue.Create("2026-11-04T09:00:20Z")));
        Assert.Equal("in 2 h 5 min", renderer.Countdown(JsonValue.Create("2026-11-04T11:05:00Z")));
        Assert.Equal("10 min ago", renderer.Countdown(JsonValue.Create("2026-11-04T08:50:00Z")));
        Assert.Equal(BindingResolver.Missing, renderer.Countdown(null));
    }

    // ---------------- snapshot mirror and action invoker ----------------

    [Fact]
    public void The_mirror_takes_the_full_snapshot_then_patches_and_asks_for_a_resync_when_a_patch_cannot_apply()
    {
        var mirror = new SnapshotMirror();
        Assert.False(mirror.Apply(new DeltaMessage { Seq = 1, Ops = [new PatchOp { Op = "replace", Path = "/state/x", Value = 1 }] }, out _));
        var full = JsonNode.Parse("""{ "state": { "system": { "cpu_pct": 10 } }, "cards": [ { "id": "core.system", "title": "System", "blocks": [] } ] }""");
        Assert.True(mirror.Apply(new DeltaMessage { Seq = 2, Ops = [new PatchOp { Op = "replace", Path = string.Empty, Value = full }] }, out var cardsChanged));
        Assert.True(cardsChanged);
        Assert.Equal("core.system", Assert.Single(mirror.Cards).Id);
        Assert.True(mirror.Apply(new DeltaMessage { Seq = 3, Ops = [new PatchOp { Op = "replace", Path = "/state/system/cpu_pct", Value = 55 }] }, out cardsChanged));
        Assert.False(cardsChanged);
        Assert.Equal("55", new BindingResolver(mirror.Root).Text(Value.Binding("/state/system/cpu_pct")));
        Assert.False(mirror.Apply(new DeltaMessage { Seq = 4, Ops = [new PatchOp { Op = "replace", Path = "/state/gone/deep", Value = 1 }] }, out _));
        Assert.Equal(3, mirror.Seq); // the failed patch changed nothing
        Assert.Equal(TimeSpan.FromSeconds(30), SnapshotClient.NextBackoff(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public async Task The_invoker_carries_the_confirm_token_only_after_a_yes()
    {
        var sent = new List<ActionRequest>();
        var invoker = new ActionInvoker(
            (req, _) =>
            {
                sent.Add(req);
                return Task.FromResult(req.Confirm is null
                    ? new ActionResult { Action = req.Action, Status = ActionStatus.NeedsConfirm, Token = "c-123", Summary = "End Chrome (12 processes)" }
                    : new ActionResult { Action = req.Action, Status = ActionStatus.Ok });
            },
            Origin.Widget);
        var end = SystemCard().Actions![2];
        string? asked = null;
        var declined = await invoker.InvokeAsync(end, "chrome", s =>
        {
            asked = s;
            return Task.FromResult(false);
        });
        Assert.True(declined.Declined);
        Assert.Equal("End Chrome (12 processes)", asked);
        Assert.Single(sent);

        var done = await invoker.InvokeAsync(end, "chrome", _ => Task.FromResult(true));
        Assert.True(done.Ok);
        Assert.Equal("c-123", sent[^1].Confirm);
        Assert.Equal("chrome", sent[^1].Target!["app"]);
        Assert.All(sent, s => Assert.Equal(Origin.Widget, s.Origin));
        Assert.Throws<ArgumentException>(() => invoker.Request(end)); // a row action needs its row
    }

    // ---------------- the card catalog ----------------

    [GeneratedRegex(@"^\| `(?<id>(core|module)\.[a-z_]+)` \|", RegexOptions.Multiline)]
    private static partial Regex CatalogRow();

    /// <summary>Cards whose owners land in later milestones (M12 capture, M14 presets / shell).</summary>
    private static readonly HashSet<string> Later = ["core.capture", "module.presets"];

    [Fact]
    public void Every_card_in_the_catalog_has_a_provider()
    {
        var spec = File.ReadAllText(Path.Combine(RepoPaths.Root, "docs", "design", "U4-surfaces.md"));
        var ids = CatalogRow().Matches(spec).Select(m => m.Groups["id"].Value).Distinct().ToList();
        Assert.True(ids.Count >= 19, "catalog table not found"); // 8 core + 11 module rows (plugin.<id>.<card> is a pattern)
        var sources = string.Concat(Directory.EnumerateFiles(Path.Combine(RepoPaths.Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText));
        var missing = ids.Where(id => !sources.Contains("Id = \"" + id + "\"", StringComparison.Ordinal)).ToList();
        Assert.Equal(Later.Order(StringComparer.Ordinal), missing.Order(StringComparer.Ordinal));
    }
}
