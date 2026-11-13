using System.Text.Json;
using System.Text.Json.Serialization;
using Tasktor.Contracts.Cards;

namespace Tasktor.Contracts.Plugins;

// tasktor.plugin/2 (Frame P, contract signed 2026-11-13). Read with ContractsJsonContext.Strict: an unknown field or
// capability anywhere is refused, never ignored, so a typo cannot quietly drop or widen a permission. ContractSpelling
// names the closest known name for the refusal message.

// ───────────────────────── plugin.json ─────────────────────────

/// <summary><c>tasktor.plugin/2</c>: contract 1's manifest plus capabilities. <c>contract</c> is read first; 1 keeps the
/// contract-1 record and rules.</summary>
public sealed record PluginManifest2
{
    [JsonPropertyName("$schema")]
    public string? SchemaRef { get; init; }

    public required int Contract { get; init; }

    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Version { get; init; }

    public string? Author { get; init; }

    public string? Description { get; init; }

    /// <summary>The command line. <c>{python}</c> names the bundled Python.</summary>
    public required IReadOnlyList<string> Command { get; init; }

    /// <summary>≥ <c>30s</c>, or <c>manual</c> (the default: a plugin with timers or events needs no polling).</summary>
    public string? Interval { get; init; }

    /// <summary>≤ <c>60s</c> per run.</summary>
    public required string Timeout { get; init; }

    public required IReadOnlyList<string> Reads { get; init; }

    public required IReadOnlyList<string> Network { get; init; }

    /// <summary>What the plugin may ask of Tasktor; each one is a line in the consent dialog.</summary>
    public IReadOnlyList<PluginCapability>? Capabilities { get; init; }

    /// <summary>Narrows <c>events:process</c> / <c>events:windows</c> to these exe names (named in the consent line). Empty = any.</summary>
    public IReadOnlyList<string>? WatchExes { get; init; }

    public IReadOnlyList<PluginSetting>? Settings { get; init; }

    public IReadOnlyList<PluginActionDecl>? Actions { get; init; }

    public string? MinTasktor { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<PluginCapability>))]
public enum PluginCapability
{
    [JsonStringEnumMemberName("page")] Page,
    [JsonStringEnumMemberName("notify")] Notify,
    [JsonStringEnumMemberName("timer")] Timer,
    [JsonStringEnumMemberName("state")] State,
    [JsonStringEnumMemberName("events:process")] EventsProcess,
    [JsonStringEnumMemberName("events:foreground")] EventsForeground,
    [JsonStringEnumMemberName("events:network")] EventsNetwork,
    [JsonStringEnumMemberName("events:windows")] EventsWindows,
    [JsonStringEnumMemberName("read:listeners")] ReadListeners,
    [JsonStringEnumMemberName("read:jobs")] ReadJobs,
    [JsonStringEnumMemberName("act:job")] ActJob,
    [JsonStringEnumMemberName("act:arrange")] ActArrange,
    [JsonStringEnumMemberName("browser:tabs")] BrowserTabs,
    [JsonStringEnumMemberName("browser:block")] BrowserBlock,
    [JsonStringEnumMemberName("browser:card")] BrowserCard,
    [JsonStringEnumMemberName("capture:browser")] CaptureBrowser,
    [JsonStringEnumMemberName("resident")] Resident,
}

/// <summary>The capability names as written in <c>plugin.json</c> (for consent lines and "did you mean").</summary>
public static class PluginCapabilities
{
    public static IReadOnlyList<string> Names { get; } =
    [
        "page", "notify", "timer", "state", "events:process", "events:foreground", "events:network", "events:windows",
        "read:listeners", "read:jobs", "act:job", "act:arrange", "browser:tabs", "browser:block", "browser:card",
        "capture:browser", "resident",
    ];
}

// ───────────────────────── stdin ─────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter<PluginCall2>))]
public enum PluginCall2
{
    [JsonStringEnumMemberName("render")] Render,
    [JsonStringEnumMemberName("action")] Action,
    [JsonStringEnumMemberName("timer")] Timer,
    [JsonStringEnumMemberName("event")] Event,
    [JsonStringEnumMemberName("submit")] Submit,
}

/// <summary>The one JSON object a contract-2 run gets on stdin.</summary>
public sealed record PluginInput2
{
    public required int Contract { get; init; }

    public required PluginCall2 Call { get; init; }

    /// <summary>The host's clock at the wake.</summary>
    public required DateTimeOffset Now { get; init; }

    /// <summary>The capabilities the user consented to: a subset of the manifest's, in its order.</summary>
    public required IReadOnlyList<PluginCapability> Granted { get; init; }

    /// <summary><c>action</c>: the declared action id; <c>submit</c>: the form id.</summary>
    public string? Action { get; init; }

    public IReadOnlyDictionary<string, string>? Args { get; init; }

    /// <summary><c>submit</c>: the form's values by key (text as typed; toggles "true"/"false").</summary>
    public IReadOnlyDictionary<string, string>? Form { get; init; }

    /// <summary><c>event</c>: the batch since the last run, oldest first (≤ 100; <see cref="EventsDropped"/> counts the rest).</summary>
    public IReadOnlyList<PluginEvent>? Events { get; init; }

    public int EventsDropped { get; init; }

    /// <summary>Results of the previous run's requests.</summary>
    public IReadOnlyList<PluginRequestResult>? Results { get; init; }

    /// <summary>The plugin's last published <c>state</c>, so a run can be stateless.</summary>
    public IReadOnlyDictionary<string, JsonElement>? State { get; init; }

    /// <summary><c>read:jobs</c>: the jobs as the snapshot has them.</summary>
    public IReadOnlyList<PluginJob>? Jobs { get; init; }

    /// <summary><c>read:listeners</c>: the ports table.</summary>
    public IReadOnlyList<PluginListener>? Listeners { get; init; }

    public required IReadOnlyDictionary<string, string> Settings { get; init; }

    public required string SnapshotPath { get; init; }

    public required string DataDir { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ProcessStartedEvent), "process_started")]
[JsonDerivedType(typeof(ProcessExitedEvent), "process_exited")]
[JsonDerivedType(typeof(ForegroundEvent), "foreground")]
[JsonDerivedType(typeof(NetworkEvent), "network")]
[JsonDerivedType(typeof(WindowEvent2), "window")]
[JsonDerivedType(typeof(BrowserTabEvent), "browser_tab")]
[JsonDerivedType(typeof(BrowserTextEvent), "browser_text")]
public abstract record PluginEvent
{
    public required DateTimeOffset At { get; init; }
}

/// <summary><c>events:process</c>. <c>pid</c> + <c>created</c> (FILETIME) identify it for <c>act:job</c>; no command line.</summary>
public sealed record ProcessStartedEvent : PluginEvent
{
    public required string Exe { get; init; }

    public required uint Pid { get; init; }

    public long Created { get; init; }
}

public sealed record ProcessExitedEvent : PluginEvent
{
    public required string Exe { get; init; }

    public required uint Pid { get; init; }

    public int? ExitCode { get; init; }
}

/// <summary><c>events:foreground</c>: the exe only, never the window title.</summary>
public sealed record ForegroundEvent : PluginEvent
{
    public required string Exe { get; init; }
}

/// <summary><c>events:network</c>: what Windows reports; no addresses, no network names.</summary>
public sealed record NetworkEvent : PluginEvent
{
    public required bool Connected { get; init; }

    public bool CaptivePortal { get; init; }

    public bool Metered { get; init; }
}

/// <summary><c>events:windows</c>: a top-level window opened or closed, by exe; no titles.</summary>
public sealed record WindowEvent2 : PluginEvent
{
    public required string Exe { get; init; }

    public required bool Opened { get; init; }
}

/// <summary><c>browser:tabs</c> (capture on): the active tab's host and title, for sites capture may read.</summary>
public sealed record BrowserTabEvent : PluginEvent
{
    public required string Host { get; init; }

    public string? Title { get; init; }

    public bool Incognito { get; init; }
}

/// <summary><c>capture:browser</c> (capture on): page text lines as capture recorded them, same caps and filters; live,
/// never from the vault.</summary>
public sealed record BrowserTextEvent : PluginEvent
{
    public required string Host { get; init; }

    public string? Url { get; init; }

    public required IReadOnlyList<string> Lines { get; init; }
}

public sealed record PluginJob
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    public required string Status { get; init; }

    public double? Pct { get; init; }
}

public sealed record PluginListener
{
    public required int Port { get; init; }

    public required string Protocol { get; init; }

    public required string Exe { get; init; }

    public uint Pid { get; init; }
}

// ───────────────────────── stdout ─────────────────────────

/// <summary>The one JSON document a contract-2 run prints (≤ 64 KB). Every part is optional; an absent part keeps the last.</summary>
public sealed record PluginOutput2
{
    public required int Contract { get; init; }

    /// <summary>≤ 6 cards of the plugin block subset, ≤ 4 actions each, literal values only.</summary>
    public IReadOnlyList<Card>? Cards { get; init; }

    /// <summary><c>browser:card</c>: the id of one of <see cref="Cards"/> to show in the extension popup.</summary>
    public string? BrowserCard { get; init; }

    /// <summary><c>page</c>: the plugin's page in the Tasktor window.</summary>
    public PluginPage? Page { get; init; }

    /// <summary><c>notify</c>: ≤ 3 per run, ≤ 12 per hour; quiet hours, full screen and meetings apply.</summary>
    public IReadOnlyList<PluginNotice>? Notify { get; init; }

    /// <summary><c>timer</c>: the next wake (replaces any pending one), 1 s to 7 days ahead. Absent keeps the pending one.</summary>
    public DateTimeOffset? WakeAt { get; init; }

    /// <summary>True cancels the pending wake.</summary>
    public bool ClearWake { get; init; }

    /// <summary><c>state</c>: ≤ 4 KB of string, number and bool values.</summary>
    public IReadOnlyDictionary<string, JsonElement>? State { get; init; }

    /// <summary>Host actions (≤ 5 per run), each needing its capability; results arrive in the next input.</summary>
    public IReadOnlyList<PluginRequest>? Requests { get; init; }
}

public sealed record PluginPage
{
    public required string Title { get; init; }

    /// <summary>≤ 20.</summary>
    public required IReadOnlyList<PluginPageSection> Sections { get; init; }
}

/// <summary>One settings-pattern group: an optional heading, display blocks, buttons and/or one form.</summary>
public sealed record PluginPageSection
{
    public string? Heading { get; init; }

    public IReadOnlyList<Block>? Blocks { get; init; }

    public IReadOnlyList<CardAction>? Actions { get; init; }

    public PluginForm? Form { get; init; }
}

public sealed record PluginForm
{
    public required string Id { get; init; }

    public required IReadOnlyList<PluginField> Fields { get; init; }

    public required string SubmitLabel { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<PluginFieldType>))]
public enum PluginFieldType
{
    [JsonStringEnumMemberName("string")] String,
    [JsonStringEnumMemberName("text")] Text,
    [JsonStringEnumMemberName("number")] Number,
    [JsonStringEnumMemberName("choice")] Choice,
    [JsonStringEnumMemberName("toggle")] Toggle,
}

public sealed record PluginField
{
    public required string Key { get; init; }

    public required string Label { get; init; }

    public required PluginFieldType Type { get; init; }

    public string? Value { get; init; }

    public IReadOnlyList<string>? Choices { get; init; }

    /// <summary><c>text</c>: ≤ 64 KB.</summary>
    public int? MaxLength { get; init; }
}

public sealed record PluginNotice
{
    /// <summary>A notice with the same id replaces the shown one.</summary>
    public required string Id { get; init; }

    public required string Title { get; init; }

    public string? Body { get; init; }

    /// <summary>≤ 3 of the plugin's declared action ids.</summary>
    public IReadOnlyList<string>? Buttons { get; init; }
}

/// <summary>A host action a plugin asks for. Each goes through the same ActionRouter, guards and undo as a user action,
/// with origin <c>plugin:&lt;id&gt;</c>. There is no generic request.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(JobPauseRequest), "job.pause")]
[JsonDerivedType(typeof(JobResumeRequest), "job.resume")]
[JsonDerivedType(typeof(ArrangeRequest), "arrange")]
[JsonDerivedType(typeof(SiteBlockRequest), "browser.block")]
[JsonDerivedType(typeof(SiteUnblockRequest), "browser.unblock")]
public abstract record PluginRequest
{
    /// <summary>The plugin's own id for this request; echoed in the result.</summary>
    public required string Id { get; init; }
}

/// <summary><c>act:job</c>: pause a Tasktor job.</summary>
public sealed record JobPauseRequest : PluginRequest
{
    public required string Job { get; init; }

    public string? Mode { get; init; }
}

/// <summary><c>act:job</c>: resume a job the plugin or the user paused.</summary>
public sealed record JobResumeRequest : PluginRequest
{
    public required string Job { get; init; }
}

/// <summary><c>act:arrange</c>: apply a saved preset; the first use of each preset asks the user.</summary>
public sealed record ArrangeRequest : PluginRequest
{
    public required string Preset { get; init; }
}

/// <summary><c>browser:block</c>: block these hosts and their subdomains (≤ 50) until a time ≤ 12 h ahead.</summary>
public sealed record SiteBlockRequest : PluginRequest
{
    public required IReadOnlyList<string> Hosts { get; init; }

    public required DateTimeOffset Until { get; init; }
}

/// <summary><c>browser:block</c>: lifts this plugin's own blocks, never another plugin's.</summary>
public sealed record SiteUnblockRequest : PluginRequest;

[JsonConverter(typeof(JsonStringEnumConverter<PluginRequestStatus>))]
public enum PluginRequestStatus
{
    [JsonStringEnumMemberName("ok")] Ok,
    [JsonStringEnumMemberName("waiting")] Waiting,
    [JsonStringEnumMemberName("refused")] Refused,
    [JsonStringEnumMemberName("failed")] Failed,
}

public sealed record PluginRequestResult
{
    public required string Id { get; init; }

    public required PluginRequestStatus Status { get; init; }

    /// <summary>Why, in plain words, when refused or failed ("act:job was not granted", "No browser is connected").</summary>
    public string? Message { get; init; }
}
