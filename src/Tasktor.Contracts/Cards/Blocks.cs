using System.Text.Json.Serialization;

namespace Tasktor.Contracts.Cards;

/// <summary>
/// A block of a card (A D-A6). The vocabulary is closed: a new block kind is a project-level change.
/// Hints are semantic only: priority (lower = more important; what survives on a small card), severity,
/// accent role and icon key.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(MetricBlock), "Metric")]
[JsonDerivedType(typeof(SparklineBlock), "Sparkline")]
[JsonDerivedType(typeof(BarBlock), "Bar")]
[JsonDerivedType(typeof(GaugeBlock), "Gauge")]
[JsonDerivedType(typeof(TableBlock), "Table")]
[JsonDerivedType(typeof(ListBlock), "List")]
[JsonDerivedType(typeof(ProgressBlock), "Progress")]
[JsonDerivedType(typeof(TreeBlock), "Tree")]
[JsonDerivedType(typeof(AlertBlock), "Alert")]
[JsonDerivedType(typeof(BadgeBlock), "Badge")]
[JsonDerivedType(typeof(CountdownBlock), "Countdown")]
[JsonDerivedType(typeof(ActionsBlock), "Actions")]
public abstract record Block
{
    /// <summary>Fitting priority: 1 is kept first.</summary>
    public int Priority { get; init; } = 1;

    public Value? Severity { get; init; }

    public AccentRole? Accent { get; init; }

    public IconKey? Icon { get; init; }

    /// <summary>The block kind (the JSON discriminator <c>kind</c>).</summary>
    [JsonIgnore]
    public BlockKind Kind => this switch
    {
        MetricBlock => BlockKind.Metric,
        SparklineBlock => BlockKind.Sparkline,
        BarBlock => BlockKind.Bar,
        GaugeBlock => BlockKind.Gauge,
        TableBlock => BlockKind.Table,
        ListBlock => BlockKind.List,
        ProgressBlock => BlockKind.Progress,
        TreeBlock => BlockKind.Tree,
        AlertBlock => BlockKind.Alert,
        BadgeBlock => BlockKind.Badge,
        CountdownBlock => BlockKind.Countdown,
        _ => BlockKind.Actions,
    };
}

public enum BlockKind
{
    Metric,
    Sparkline,
    Bar,
    Gauge,
    Table,
    List,
    Progress,
    Tree,
    Alert,
    Badge,
    Countdown,
    Actions,
}

/// <summary>A single number or short text with unit and trend (series).</summary>
public sealed record MetricBlock : Block
{
    public required string Label { get; init; }

    public required Value Value { get; init; }

    public string? Unit { get; init; }

    public Trend? Trend { get; init; }

}

/// <summary>A small line chart. Built-in cards name a <c>series</c> pulled over the pipe; plugins send literal <c>points</c> (≤ 60).</summary>
public sealed record SparklineBlock : Block
{
    public string? Label { get; init; }

    public string? Series { get; init; }

    public IReadOnlyList<double>? Points { get; init; }

    public string? Unit { get; init; }

}

/// <summary>A horizontal bar: value out of max.</summary>
public sealed record BarBlock : Block
{
    public required string Label { get; init; }

    public required Value Value { get; init; }

    public Value? Max { get; init; }

    public string? Unit { get; init; }

    public Value? Text { get; init; }

}

/// <summary>A gauge between min and max.</summary>
public sealed record GaugeBlock : Block
{
    public required string Label { get; init; }

    public required Value Value { get; init; }

    public double Min { get; init; }

    public double Max { get; init; } = 100;

    public string? Unit { get; init; }

}

public sealed record TableColumn
{
    /// <summary>The row field this column shows.</summary>
    public required string Key { get; init; }

    public required string Label { get; init; }

    public string? Unit { get; init; }
}

/// <summary>Top-N rows. <c>rows</c> is a literal array of objects or a binding to one in <c>state</c>.</summary>
public sealed record TableBlock : Block
{
    public string? Label { get; init; }

    public required IReadOnlyList<TableColumn> Columns { get; init; }

    public required Value Rows { get; init; }

    /// <summary>Maximum rows to show (plugins ≤ 12).</summary>
    public int? Max { get; init; }

    /// <summary>Row field that names a row action target (for per-row actions).</summary>
    public string? RowKey { get; init; }

}

/// <summary>A list of short strings (literal array or binding).</summary>
public sealed record ListBlock : Block
{
    public string? Label { get; init; }

    public required Value Items { get; init; }

    public int? Max { get; init; }

}

/// <summary>A progress bar (percent, or done of total).</summary>
public sealed record ProgressBlock : Block
{
    public required string Label { get; init; }

    public Value? Pct { get; init; }

    public Value? Done { get; init; }

    public Value? Total { get; init; }

    public string? Unit { get; init; }

    public Value? Text { get; init; }

}

/// <summary>A drill-down tree (flyout only). The rows are pulled over the pipe from <c>table</c>.</summary>
public sealed record TreeBlock : Block
{
    public string? Label { get; init; }

    public required string Table { get; init; }

    public IReadOnlyList<TableColumn>? Columns { get; init; }

}

/// <summary>An alert line (built-in cards only; toasts go through rules).</summary>
public sealed record AlertBlock : Block
{
    public required Value Text { get; init; }

}

/// <summary>A short status word or line (e.g. the A2.7 verdict).</summary>
public sealed record BadgeBlock : Block
{
    public required Value Text { get; init; }

}

/// <summary>A countdown to an ISO-8601 time. Computed when a viewer draws it, never ticked.</summary>
public sealed record CountdownBlock : Block
{
    public required string Label { get; init; }

    public required Value Until { get; init; }

}

/// <summary>Buttons, by card action id.</summary>
public sealed record ActionsBlock : Block
{
    public required IReadOnlyList<string> Actions { get; init; }

}
