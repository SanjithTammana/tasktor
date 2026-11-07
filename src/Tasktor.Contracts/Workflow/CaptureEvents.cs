using System.Text.Json.Serialization;
using Tasktor.Contracts.Platform;

namespace Tasktor.Contracts.Workflow;

/// <summary>Kinds of C1 events (the <c>events.db</c> <c>kind</c> column; values are stable, never reused).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CaptureKind>))]
public enum CaptureKind : byte
{
    [JsonStringEnumMemberName("fg")] Foreground = 1,
    [JsonStringEnumMemberName("title")] Title = 2,
    [JsonStringEnumMemberName("min")] Minimize = 3,
    [JsonStringEnumMemberName("restore")] Restore = 4,
    [JsonStringEnumMemberName("idle_start")] IdleStart = 5,
    [JsonStringEnumMemberName("idle_end")] IdleEnd = 6,
    [JsonStringEnumMemberName("lock")] Lock = 7,
    [JsonStringEnumMemberName("unlock")] Unlock = 8,
    [JsonStringEnumMemberName("suspend")] Suspend = 9,
    [JsonStringEnumMemberName("resume")] Resume = 10,
    [JsonStringEnumMemberName("proc_start")] ProcStart = 11,
    [JsonStringEnumMemberName("proc_stop")] ProcStop = 12,
    [JsonStringEnumMemberName("notif")] Notification = 13,
    [JsonStringEnumMemberName("excluded_start")] ExcludedStart = 14,
    [JsonStringEnumMemberName("excluded_end")] ExcludedEnd = 15,
    [JsonStringEnumMemberName("pause")] Pause = 16,
    [JsonStringEnumMemberName("unpause")] Unpause = 17,
    [JsonStringEnumMemberName("agent_start")] AgentStart = 18,
    [JsonStringEnumMemberName("agent_stop")] AgentStop = 19,
    [JsonStringEnumMemberName("job_on")] JobOn = 20,
    [JsonStringEnumMemberName("job_off")] JobOff = 21,
}

/// <summary>Input idle vs media idle (input stopped while the foreground app plays audio: watching, not away).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<IdleKind>))]
public enum IdleKind : byte
{
    [JsonStringEnumMemberName("input")] Input = 1,
    [JsonStringEnumMemberName("media")] Media = 2,
}

/// <summary>
/// One C1 event after the privacy gate: what <c>events.db</c> stores and the episode builder reads. Titles are already
/// redacted (and null for <c>drop-title</c> apps); never a path, URL path, command line or keystroke.
/// </summary>
public sealed record CaptureEvent
{
    public required CaptureKind Kind { get; init; }

    /// <summary>Wall clock (ms since the Unix epoch, UTC).</summary>
    public required long TsUtcMs { get; init; }

    /// <summary>UTC offset at capture, minutes (travel, DST).</summary>
    public int TzMin { get; init; }

    /// <summary>Monotonic ms; orders events across clock jumps.</summary>
    public required long MonoMs { get; init; }

    /// <summary><c>AppIdentity</c> key (lower-cased exe or package family); never a path.</summary>
    public string? App { get; init; }

    /// <summary>Per-install window id for (hwnd, process).</summary>
    public int? Win { get; init; }

    public string? Title { get; init; }

    public string? Domain { get; init; }

    public IdleKind? Idle { get; init; }
}

/// <summary>C's window events from the hook thread: five numbers, no text (the title is read by the source's own step).</summary>
public enum WindowEventKind : byte
{
    Foreground,
    TitleChanged,
    MinimizeStart,
    MinimizeEnd,

    // Lifecycle kinds (U3 D-W5, H-W1): only while a feature holds WinEventSource's lifecycle lease. Never recorded by
    // tracking (C1.4: open / close only while F runs); no title is read for them.
    Shown,
    Destroyed,
    MoveSizeStart,
    MoveSizeEnd,
}

public sealed record WindowEvent(WindowEventKind Kind, nint Hwnd, ProcKey Proc, long AtMono, DateTimeOffset AtUtc)
{
    /// <summary>The window title, read by the source (never by the hook callback) and personal data.</summary>
    public Sensitive<string>? Title { get; init; }

    /// <summary>The window's exe name, resolved by the source.</summary>
    public string? Exe { get; init; }
}

public enum PresenceKind : byte
{
    IdleStart,
    IdleEnd,
    Lock,
    Unlock,
    Suspend,
    Resume,
}

/// <summary>Presence: idle (backdated to the last input), lock, sleep.</summary>
public sealed record PresenceEvent(PresenceKind Kind, DateTimeOffset AtUtc, long AtMono, IdleKind? Idle = null);

public enum DomainOutcome : byte
{
    Ok,
    Focused,
    NotHost,
    Timeout,
    Unsupported,
}

/// <summary>C2: the browser address bar's hostname only, or why there is none.</summary>
public sealed record DomainRead(int Window, string? Host, DomainOutcome Outcome);
