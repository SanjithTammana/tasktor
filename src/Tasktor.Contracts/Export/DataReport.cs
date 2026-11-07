namespace Tasktor.Contracts.Export;

/// <summary>
/// The agent's <c>data</c> pull (U1 D-H8, U4 D-G3 "Your data"): what Tasktor keeps on this PC, in the groups the
/// <c>forget</c> action deletes. Sizes and dates only: no file names, paths or contents.
/// </summary>
public sealed record DataReport
{
    public required IReadOnlyList<DataGroup> Groups { get; init; }
}

/// <summary>One group of stores. <see cref="Scope"/> is the <c>forget</c> target that deletes it (<c>learned</c>,
/// <c>tracking</c>, <c>capture</c>; <c>all</c> for the rest).</summary>
public sealed record DataGroup
{
    public required string Scope { get; init; }

    public required string Name { get; init; }

    /// <summary>How long it is kept, in words ("7 days", "until you forget it").</summary>
    public required string Kept { get; init; }

    public long Bytes { get; init; }

    public int Files { get; init; }

    public DateTimeOffset? Oldest { get; init; }

    public DateTimeOffset? Newest { get; init; }
}
