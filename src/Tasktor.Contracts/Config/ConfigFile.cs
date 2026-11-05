using System.Text.Json.Serialization;

namespace Tasktor.Contracts.Config;

/// <summary>
/// <c>config.json</c> (U1 D-H5): JSON with comments and <c>$schema</c>; <c>schema_version</c> migrates forward with a
/// <c>.bak</c>. Every section is optional; missing sections take their defaults, unknown fields are ignored.
/// </summary>
public sealed record ConfigFile
{
    [JsonPropertyName("$schema")]
    public string? SchemaRef { get; init; }

    public int SchemaVersion { get; init; } = 1;

    public CliSettings? Cli { get; init; }

    /// <summary>Module on / off by id (<c>tracking</c>, <c>capture</c>, <c>ports</c>, <c>agents</c>, <c>windows</c>, <c>focus</c>, …). Off when absent.</summary>
    public IReadOnlyDictionary<string, bool>? Modules { get; init; }

    /// <summary>A1.7 colour thresholds per metric (<c>cpu</c>, <c>ram</c>, <c>gpu</c>, <c>disk_active</c>).</summary>
    public IReadOnlyDictionary<string, Threshold>? Thresholds { get; init; }

    public DisplaySettings? Display { get; init; }

    public JobSettings? Jobs { get; init; }

    public FocusSettings? Focus { get; init; }

    public CalendarSettings? Calendar { get; init; }

    public AgentWatchSettings? Agents { get; init; }

    public SpaceSettings? Space { get; init; }

    public FlyoutSettings? Flyout { get; init; }

    public WindowsSettings? Windows { get; init; }

    public TrackingSettings? Tracking { get; init; }

    public CaptureSettings? Capture { get; init; }

    public DiskSettings? Disk { get; init; }

    /// <summary>H13: notify-only update check, off by default.</summary>
    public bool? UpdateCheck { get; init; }

    /// <summary>Portable build: opt-in logon start through the <c>HKCU\…\Run</c> key.</summary>
    public bool? StartWithWindows { get; init; }
}

public sealed record CliSettings
{
    /// <summary>Destructive CLI verbs need this and <c>--yes</c> (U1 D-H4). Off by default.</summary>
    public bool AllowDestructive { get; init; }
}

public sealed record Threshold
{
    public double Warn { get; init; }

    public double Critical { get; init; }
}

public sealed record DisplaySettings
{
    /// <summary>Network in bits/s instead of bytes/s (A1).</summary>
    public bool NetworkBits { get; init; }

    /// <summary>Rows hidden from the system card (A1: each row can be hidden).</summary>
    public IReadOnlyList<string>? HiddenRows { get; init; }

    /// <summary>Flyout pages hidden (U4 D-G6: not Home, Your data or Settings).</summary>
    public IReadOnlyList<string>? HiddenPages { get; init; }
}

public sealed record JobSettings
{
    /// <summary>Exe names added to the default watch list.</summary>
    public IReadOnlyList<string>? Watch { get; init; }

    /// <summary>Command-line patterns added to the default excludes.</summary>
    public IReadOnlyList<string>? Excludes { get; init; }

    /// <summary>B6.2 "Learn from my jobs" (on by default).</summary>
    public bool? Learning { get; init; }

    /// <summary>B2.5 finished toast (on by default).</summary>
    public bool? FinishedToast { get; init; }
}

public sealed record FocusPreset
{
    public required string Name { get; init; }

    public required IReadOnlyList<FocusBlockDef> Blocks { get; init; }

    public bool Repeat { get; init; } = true;

    public int? LongBreakEvery { get; init; }

    public int? LongBreakMinutes { get; init; }
}

public sealed record FocusBlockDef
{
    /// <summary><c>focus</c> or <c>break</c>.</summary>
    public required string Kind { get; init; }

    public required int Minutes { get; init; }
}

public sealed record FocusSettings
{
    public IReadOnlyList<FocusPreset>? Presets { get; init; }
}

/// <summary>U2 calendar inputs (E1, E2, I13): local files only; ICS URLs live in the <c>tasktor.calendar</c> plugin's settings.</summary>
public sealed record CalendarSettings
{
    /// <summary>Local <c>.ics</c> files the user picked.</summary>
    public IReadOnlyList<string>? IcsFiles { get; init; }

    /// <summary>E1's plain deadlines file; default <c>deadlines.txt</c> in the data folder.</summary>
    public string? DeadlinesFile { get; init; }

    /// <summary>All-day events whose title starts with this are deadlines (default <c>Due</c>).</summary>
    public string? DeadlineMarker { get; init; }

    /// <summary>Events whose title contains this open an event-mode window (I13, default <c>[focus]</c>).</summary>
    public string? EventMarker { get; init; }
}

/// <summary>I1: which agents report through hooks (Q1: written only on confirm).</summary>
public sealed record AgentWatchSettings
{
    /// <summary>Q2 permission toast (on by default).</summary>
    public bool? PermissionToast { get; init; }
}

/// <summary>I7: extra folders the user adds to the space report.</summary>
public sealed record SpaceSettings
{
    public IReadOnlyList<string>? Folders { get; init; }
}

public sealed record FlyoutSettings
{
    public string? Hotkey { get; init; }

    /// <summary>"Keep the window ready" (U4 D-G2).</summary>
    public bool KeepWarm { get; init; }

    /// <summary>Cards pinned to Home, in order (<c>card.pin</c> / <c>card.unpin</c>).</summary>
    public IReadOnlyList<string>? PinnedCards { get; init; }
}

public sealed record WindowsSettings
{
    /// <summary>Switcher hotkey (U3 D-W3, default Alt+`).</summary>
    public string? Hotkey { get; init; }

    public bool ReplaceAltTab { get; init; }

    public bool ThisDesktopOnly { get; init; }
}

public sealed record TrackingSettings
{
    /// <summary>App exe names and title patterns never recorded (C3 rule 3).</summary>
    public IReadOnlyList<string>? ExcludeApps { get; init; }

    public IReadOnlyList<string>? ExcludeTitles { get; init; }

    /// <summary>Raw event retention, days (default 30).</summary>
    public int? RetentionDays { get; init; }
}

public sealed record CaptureScheduleRange
{
    /// <summary>Days: <c>mon</c>…<c>sun</c>, or <c>weekdays</c> / <c>weekends</c> / <c>all</c>.</summary>
    public required string Days { get; init; }

    public required string From { get; init; }

    public required string To { get; init; }

    /// <summary><c>never</c> (block) or <c>only</c> (allow only inside).</summary>
    public required string Mode { get; init; }
}

public sealed record CaptureSettings
{
    public bool EdgeLine { get; init; } = true;

    public string? EdgeLineEdge { get; init; }

    public IReadOnlyList<CaptureScheduleRange>? Schedule { get; init; }

    public IReadOnlyList<string>? ExcludeApps { get; init; }

    public IReadOnlyList<string>? ExcludeHosts { get; init; }
}

public sealed record DiskSettings
{
    /// <summary>Volumes switched off for the low-space rule (A5).</summary>
    public IReadOnlyList<string>? Off { get; init; }

    public bool IncludeRemovable { get; init; }
}
