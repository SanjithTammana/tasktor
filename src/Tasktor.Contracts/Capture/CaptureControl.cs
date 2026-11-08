using System.Text.Json.Serialization;

namespace Tasktor.Contracts.Capture;

/// <summary>What the agent asks the capture host to do (U4 D-G6 capture blocks; T1 forget by time range).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CaptureVerb>))]
public enum CaptureVerb
{
    [JsonStringEnumMemberName("status")] Status,
    [JsonStringEnumMemberName("pause")] Pause,
    [JsonStringEnumMemberName("resume")] Resume,
    [JsonStringEnumMemberName("exclude")] Exclude,
    [JsonStringEnumMemberName("unexclude")] Unexclude,
    [JsonStringEnumMemberName("tab_pause")] TabPause,
    [JsonStringEnumMemberName("forget")] Forget,

    /// <summary>C21 "Search my activity": <see cref="CaptureCommand.Query"/>, from <see cref="CaptureCommand.Since"/>. People only.</summary>
    [JsonStringEnumMemberName("search")] Search,

    /// <summary>
    /// C22.4: a process started under a known agent (B's scan): <see cref="CaptureCommand.Agent"/>, the agent's exe in
    /// <see cref="CaptureCommand.App"/>, the child's exe in <see cref="CaptureCommand.Exe"/>
    /// and its <see cref="CaptureCommand.CommandLine"/>. From the agent process only, never a rule or a person.
    /// </summary>
    [JsonStringEnumMemberName("agent_command")] AgentCommand,
}

/// <summary>
/// <c>tasktor.capture-control/1</c>: one command from the agent to <c>tasktor-capture</c>. <see cref="ByUser"/> is true only
/// for a person's request (the router has already refused rule-origin lifts); rules may add blocks but never lift them.
/// </summary>
public sealed record CaptureCommand
{
    public string Schema { get; init; } = ContractIds.CaptureControl;

    public required CaptureVerb Verb { get; init; }

    public bool ByUser { get; init; }

    /// <summary>Pause until this time; null with <see cref="CaptureVerb.Pause"/> = until resumed.</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>Exclude: an app exe glob. Forget (without <see cref="Since"/>): everything captured from it.</summary>
    public string? App { get; init; }

    /// <summary>Exclude: a hostname (and its subdomains). Forget (without <see cref="Since"/>): everything captured from it.</summary>
    public string? Site { get; init; }

    /// <summary>Tab pause: the browser tab id.</summary>
    public int? Tab { get; init; }

    /// <summary>Unexclude: the block's id.</summary>
    public string? BlockId { get; init; }

    /// <summary>Forget: records from this time on. Search: activities from this time on.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Search: the words to look for. Personal data: never logged.</summary>
    public string? Query { get; init; }

    /// <summary>Agent command: the agents-group entry that ran it (e.g. <c>claude</c>).</summary>
    public string? Agent { get; init; }

    /// <summary>Agent command: the child process's exe.</summary>
    public string? Exe { get; init; }

    /// <summary>Agent command: the child's command line, read once. Personal data: never logged.</summary>
    public string? CommandLine { get; init; }
}

/// <summary>One activity a search found (C21): when, which track, and a snippet with the matched words in brackets.</summary>
public sealed record ActivityHit
{
    public required DateTimeOffset Start { get; init; }

    public required DateTimeOffset End { get; init; }

    public int Track { get; init; }

    /// <summary>Personal data (what was on screen or typed): shown to the user, never logged.</summary>
    public required string Snippet { get; init; }

    /// <summary>Web pages the activity showed (scrubbed http(s) addresses), first seen first: "reopen" opens one. Personal data.</summary>
    public IReadOnlyList<string> Links { get; set; } = [];
}

/// <summary>One active capture block as the agent's card shows it.</summary>
public sealed record CaptureBlockInfo
{
    public required string Id { get; init; }

    /// <summary><c>pause</c>, <c>schedule</c>, <c>app</c>, <c>site</c> or <c>tab</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Plain words for the card: "Paused until 14:30", "Not capturing slack.exe".</summary>
    public required string Text { get; init; }

    public DateTimeOffset? Until { get; init; }

    /// <summary>Added by a rule (shown so; only the user lifts it).</summary>
    public bool ByRule { get; init; }
}

/// <summary>The capture host's state, returned for every command and pushed on change.</summary>
public sealed record CaptureStatus
{
    public string Schema { get; init; } = ContractIds.CaptureControl;

    public required bool On { get; init; }

    public DateTimeOffset? PausedUntil { get; init; }

    public bool PausedIndefinitely { get; init; }

    public IReadOnlyList<CaptureBlockInfo> Blocks { get; init; } = [];

    public long RecordsToday { get; init; }

    /// <summary>
    /// The browsers whose companion extension is connected now (<c>chrome</c>, <c>edge</c>). The agent offers to set the
    /// extension up when a browser is in front for 60 s without one (C §5.5 failure table).
    /// </summary>
    public IReadOnlyList<string>? Extensions { get; set; }

    /// <summary>Records removed by a forget command.</summary>
    public long? Forgotten { get; init; }

    /// <summary>Search results (only for <see cref="CaptureVerb.Search"/>), best first.</summary>
    public IReadOnlyList<ActivityHit>? Hits { get; set; }

    /// <summary>Why a command was refused (<c>rule_owned</c>, <c>not_found</c>, <c>invalid</c>), or null.</summary>
    public string? Refusal { get; init; }
}
