namespace Tasktor.Contracts.Calendar;

/// <summary>
/// <c>tasktor.calendar/1</c>: the normalised events file the <c>tasktor.calendar</c> plugin writes to
/// <c>plugin-data\tasktor.calendar\events.json</c> (U2 D-S2). The agent reads it into memory only.
/// </summary>
public sealed record CalendarFile
{
    public string Schema { get; init; } = ContractIds.Calendar;

    public required DateTimeOffset GeneratedAt { get; init; }

    public IReadOnlyList<CalendarFeed>? Calendars { get; init; }

    public required IReadOnlyList<CalendarEvent> Events { get; init; }
}

public sealed record CalendarFeed
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary><c>ok</c> or <c>error</c>.</summary>
    public required string Status { get; init; }

    public string? Error { get; init; }
}

/// <summary>One expanded event instance (recurrences already expanded, next 14 days).</summary>
public sealed record CalendarEvent
{
    public required string Uid { get; init; }

    public string? Calendar { get; init; }

    public required DateTimeOffset Start { get; init; }

    public DateTimeOffset? End { get; init; }

    public bool AllDay { get; init; }

    public required string Title { get; init; }

    public string? Location { get; init; }

    public string? Url { get; init; }
}
