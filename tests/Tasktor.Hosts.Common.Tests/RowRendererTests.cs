using System.Text.Json.Nodes;
using Tasktor.Contracts.Cards;
using Tasktor.Hosts.Common.Render;

namespace Tasktor.Hosts.Common.Tests;

/// <summary>U4 D-G1 / amended D-G13: cards as Settings-style rows for the deep window.</summary>
public sealed class RowRendererTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);

    private static readonly BindingResolver Resolver = new(JsonNode.Parse("""
        { "state": { "cpu": 34.4, "sev": "warn", "apps": [ { "app_id": "chrome", "name": "Chrome", "cpu": 22.5 }, { "app_id": "code", "name": "Code" } ],
          "items": ["one", "two", "three"], "eta": "2026-11-06T10:25:00Z", "late": "2026-11-06T12:30:00Z" } }
        """));

    private static CardRows Render(params Block[] blocks) => Render(new Card { Id = "c", Title = "Card", Blocks = blocks });

    private static CardRows Render(Card card) => new RowRenderer(Now).Render(CardFitter.Fit(card, FitTarget.Flyout, Resolver), Resolver);

    [Fact]
    public void Metrics_badges_and_alerts_keep_severity_as_a_word_on_the_row()
    {
        var rows = Render(
            new MetricBlock { Label = "CPU", Value = Value.Binding("/state/cpu"), Unit = "%", Trend = Trend.Up, Severity = Value.Binding("/state/sev") },
            new BadgeBlock { Text = Value.Literal("Busy: Chrome"), Priority = 2 },
            new AlertBlock { Text = Value.Literal("Disk almost full"), Priority = 3 }).Rows;
        Assert.Equal(new CardRow(RowKind.Value, "CPU", "34% ↑", Severity: Severity.Warn), rows[0]); // the shared number format: whole numbers from 10 up
        Assert.Equal((RowKind.Text, "Busy: Chrome"), (rows[1].Kind, rows[1].Label));
        Assert.Equal((RowKind.Alert, Severity.Warn), (rows[2].Kind, rows[2].Severity));
    }

    [Fact]
    public void Bars_gauges_and_progress_become_meters_with_a_fraction()
    {
        var rows = Render(
            new BarBlock { Label = "Disk C:", Value = Value.Literal(80), Max = Value.Literal(200), Unit = "GB", Text = Value.Literal("120 GB free") },
            new GaugeBlock { Label = "Temp", Value = Value.Literal(60), Min = 20, Max = 100, Unit = "°C", Priority = 2 },
            new ProgressBlock { Label = "train.py", Done = Value.Literal(52), Total = Value.Literal(100), Text = Value.Literal("done ≈10:36"), Priority = 3 }).Rows;
        Assert.Equal(("80 GB · 120 GB free", 0.4), (rows[0].Value, rows[0].Fraction));
        Assert.Equal(0.5, rows[1].Fraction);
        Assert.Equal(("52% · done ≈10:36", 0.52), (rows[2].Value, rows[2].Fraction));
    }

    [Fact]
    public void Tables_carry_headers_cells_with_units_and_row_keys()
    {
        var rows = Render(new TableBlock
        {
            Label = "Top apps",
            Columns = [new TableColumn { Key = "name", Label = "App" }, new TableColumn { Key = "cpu", Label = "CPU", Unit = "%" }],
            Rows = Value.Binding("/state/apps"),
            RowKey = "app_id",
        }).Rows;
        Assert.Equal([RowKind.Heading, RowKind.TableHeader, RowKind.TableRow, RowKind.TableRow], rows.Select(r => r.Kind));
        Assert.Equal(["App", "CPU"], rows[1].Cells!);
        Assert.Equal(["Chrome", "22%"], rows[2].Cells!);
        Assert.Equal(["Code", BindingResolver.Missing], rows[3].Cells!); // a missing cell is a dash, without a unit
        Assert.Equal("chrome", rows[2].RowKey);
    }

    [Fact]
    public void Lists_charts_countdowns_and_trees()
    {
        var rows = Render(
            new ListBlock { Label = "Recent", Items = Value.Binding("/state/items"), Max = 2 },
            new SparklineBlock { Label = "CPU", Series = "system.cpu_pct", Unit = "%", Priority = 2 },
            new CountdownBlock { Label = "Done", Until = Value.Binding("/state/eta"), Priority = 3 },
            new CountdownBlock { Label = "Meeting", Until = Value.Binding("/state/late"), Priority = 4 },
            new TreeBlock { Table = "processes", Priority = 5 }).Rows;
        Assert.Equal(["Recent", "one", "two"], rows.Take(3).Select(r => r.Label));
        Assert.Equal((RowKind.Chart, "system.cpu_pct", "%"), (rows[3].Kind, rows[3].Series, rows[3].Value));
        Assert.Equal("in 25 min", rows[4].Value);
        Assert.Equal("at 12:30", rows[5].Value);
        Assert.Equal((RowKind.Tree, "Apps", "processes"), (rows[6].Kind, rows[6].Label, rows[6].Series));
    }

    [Fact]
    public void Header_carries_severity_plugin_source_staleness_and_destructive_labels()
    {
        var card = new Card
        {
            Id = "plugin.x",
            Title = "Builds",
            Severity = Value.Literal("critical"),
            Source = new CardSource { Kind = CardSourceKind.Plugin, Plugin = "ci-watch", Name = "CI watch" },
            StaleAfterS = 60,
            UpdatedAt = Now.AddMinutes(-5),
            Blocks = [new BadgeBlock { Text = Value.Literal("2 failing") }],
            Actions = [new CardAction { Id = "end", Label = "End", Action = "app.end", Destructive = true }, new CardAction { Id = "o", Label = "Open", Action = "open" }],
        };
        var r = Render(card);
        Assert.Equal((Severity.Critical, "CI watch", "as of 9:55"), (r.Severity, r.Source, r.AsOf));
        Assert.Equal(["End…", "Open"], r.Actions.Select(a => a.Label));
        Assert.Empty(r.RowActions);
    }

    [Fact]
    public void Actions_on_a_row_are_split_from_the_cards_own()
    {
        var row = new Dictionary<string, string> { ["app"] = "{row}" };
        var card = new Card
        {
            Id = "core.apps",
            Title = "Apps",
            Blocks = [new BadgeBlock { Text = Value.Literal("x") }],
            Actions =
            [
                new CardAction { Id = "open", Label = "Open", Action = "open" },
                new CardAction { Id = "lower", Label = "Lower", Action = "priority.set", Target = row, Priority = 2 },
                new CardAction { Id = "end", Label = "End", Action = "app.end", Target = row, Destructive = true, Priority = 3 },
            ],
        };
        var r = Render(card);
        Assert.Equal(["Open"], r.Actions.Select(a => a.Label));
        Assert.Equal(["Lower", "End…"], r.RowActions.Select(a => a.Label));
        Assert.True(RowRenderer.NeedsRow(card.Actions![1]));
        Assert.False(RowRenderer.NeedsRow(new CardAction { Id = "p", Label = "Plugin", Target = row })); // plugin actions are sent by their host
    }
}
