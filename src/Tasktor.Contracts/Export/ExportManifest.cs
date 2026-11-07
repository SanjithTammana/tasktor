namespace Tasktor.Contracts.Export;

/// <summary><c>tasktor.export/1</c>: <c>manifest.json</c> of a data export (U2 D-S7).</summary>
public sealed record ExportManifest
{
    public string Schema { get; init; } = ContractIds.Export;

    public required DateTimeOffset ExportedAt { get; init; }

    public required string TasktorVersion { get; init; }

    /// <summary>The <c>--since</c> span, if any (e.g. <c>30d</c>).</summary>
    public string? Since { get; init; }

    /// <summary><c>csv</c>, <c>jsonl</c> or <c>both</c>.</summary>
    public required string Format { get; init; }

    public required IReadOnlyList<ExportTable> Tables { get; init; }

    /// <summary>Tables or data deliberately left out, with the reason ("events_raw: needs --include-raw").</summary>
    public IReadOnlyList<string>? LeftOut { get; init; }
}

public sealed record ExportTable
{
    public required string Name { get; init; }

    /// <summary>The table's row schema id, e.g. <c>tasktor.export/1#jobs_runs</c>.</summary>
    public required string Schema { get; init; }

    public required long Rows { get; init; }

    public required IReadOnlyList<string> Files { get; init; }

    public required IReadOnlyList<string> Columns { get; init; }
}
