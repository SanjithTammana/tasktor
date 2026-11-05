namespace Tasktor.Contracts.Platform;

/// <summary>One calendar instance's key: the event UID and this instance's start (U2 <c>CalendarSource</c>).</summary>
public readonly record struct EventKey(string Uid, DateTimeOffset Start);

/// <summary>Where a calendar row came from.</summary>
public enum CalendarOrigin : byte
{
    IcsFile,
    Plugin,
    DeadlinesFile,
}

/// <summary>
/// One expanded calendar instance, in memory only (U2 D-S5): titles, locations and URLs are <see cref="Sensitive{T}"/> and
/// are never logged or stored. Descriptions and attendees are never read.
/// </summary>
public sealed record CalEvent
{
    public required EventKey Key { get; init; }

    public required Sensitive<string> Title { get; init; }

    public DateTimeOffset? End { get; init; }

    public bool AllDay { get; init; }

    public Sensitive<string>? Location { get; init; }

    /// <summary>The meeting link found in the location or URL field (U2: Teams, Zoom, Meet, Webex hosts only).</summary>
    public Sensitive<string>? MeetingUrl { get; init; }

    public CalendarOrigin Origin { get; init; }

    public string? Calendar { get; init; }

    /// <summary>E1: from the deadlines file, or an all-day event whose title starts with the deadline marker.</summary>
    public bool Deadline { get; init; }

    /// <summary>I13: the title carries the event-mode marker (default <c>[focus]</c>).</summary>
    public bool EventWindow { get; init; }

    public DateTimeOffset Start => Key.Start;
}
