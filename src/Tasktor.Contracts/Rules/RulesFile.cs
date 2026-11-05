using System.Text.Json.Serialization;

namespace Tasktor.Contracts.Rules;

/// <summary><c>tasktor.rules/1</c>: <c>rules.json</c>, JSON with comments (U1 D-H5, D-H6).</summary>
public sealed record RulesFile
{
    [JsonPropertyName("$schema")]
    public string? SchemaRef { get; init; }

    public int SchemaVersion { get; init; } = 1;

    /// <summary>Named process groups, e.g. <c>agents</c> (U2 D-S3) or <c>builds</c>.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? Groups { get; init; }

    public IReadOnlyList<Rule>? Rules { get; init; }

    /// <summary>A2.2 grouping rules, ordered; user entries go before the shipped defaults.</summary>
    public IReadOnlyList<GroupingRule>? Grouping { get; init; }
}

/// <summary>A2.2: <c>match exe/path/package → group as / attach to parent / never group</c>.</summary>
public sealed record GroupingRule
{
    public required GroupingMatch Match { get; init; }

    /// <summary>Show matching processes as their own group with this name.</summary>
    public string? GroupAs { get; init; }

    /// <summary><c>parent</c> (fold into the launching app) or <c>never</c> (always its own group).</summary>
    public string? Attach { get; init; }
}

/// <summary>Case-insensitive globs (<c>*</c>, <c>?</c>) on the exe name, full path or package family.</summary>
public sealed record GroupingMatch
{
    public string? Exe { get; init; }

    public string? Path { get; init; }

    public string? Package { get; init; }
}

/// <summary>One rule: when → for → then, with cooldown and undo.</summary>
public sealed record Rule
{
    public required string Id { get; init; }

    public bool? Enabled { get; init; }

    /// <summary>Built-in rules may use <c>use_fallback</c> / <c>relearn</c>; user files may not set this.</summary>
    public bool? Builtin { get; init; }

    public string? Description { get; init; }

    public required Condition When { get; init; }

    /// <summary>Hold duration (<c>30s</c>, <c>5m</c>, <c>2h</c>): A's <c>HoldFor</c>.</summary>
    [JsonPropertyName("for")]
    public string? HoldFor { get; init; }

    public string? Cooldown { get; init; }

    public required IReadOnlyList<RuleAction> Then { get; init; }

    /// <summary>Reverse the rule's actions when its condition clears.</summary>
    public bool? Undo { get; init; }

    /// <summary>Commands for <c>run:&lt;name&gt;</c> toast buttons. Placeholders only; never literal user paths in shipped rules.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? Commands { get; init; }
}

/// <summary>
/// A condition. Exactly one kind per object, chosen by which key is present (<c>metric</c>, <c>learned</c>,
/// <c>group</c>, <c>process</c>, <c>state</c>, <c>event</c>, <c>all</c>, <c>any</c>, <c>not</c>). The loader enforces it.
/// </summary>
public sealed record Condition
{
    // metric + above / below
    public string? Metric { get; init; }

    public string? Instance { get; init; }

    public double? Above { get; init; }

    public double? Below { get; init; }

    // learned (B's LearnedThreshold)
    public LearnedCondition? Learned { get; init; }

    // group + count_above / busy_count_above / running
    public string? Group { get; init; }

    public int? CountAbove { get; init; }

    public int? BusyCountAbove { get; init; }

    public bool? Running { get; init; }

    // process (name or glob) + cpu_above / age_above / silent_for
    public string? Process { get; init; }

    public double? CpuAbove { get; init; }

    public string? AgeAbove { get; init; }

    public string? SilentFor { get; init; }

    // state (boolean states, or a value with "is")
    public string? State { get; init; }

    public string? Is { get; init; }

    // event + filters
    public string? Event { get; init; }

    public string? Folder { get; init; }

    public string? Glob { get; init; }

    public string? App { get; init; }

    public string? Title { get; init; }

    public string? Agent { get; init; }

    // combinators
    public IReadOnlyList<Condition>? All { get; init; }

    public IReadOnlyList<Condition>? Any { get; init; }

    public Condition? Not { get; init; }
}

/// <summary>A learned threshold condition (B's <c>LearnedThreshold</c>): learned value with fallback and clamp.</summary>
public sealed record LearnedCondition
{
    public required string Stat { get; init; }

    public required string Key { get; init; }

    public required string Fallback { get; init; }

    public IReadOnlyList<string>? Clamp { get; init; }

    public double? Multiplier { get; init; }
}

/// <summary>
/// A rule action. Exactly one kind per object, chosen by which key is present. Rules may only call reversible
/// actions; <c>run</c> appears only as a toast button (<c>run:&lt;command&gt;</c>), never as an action.
/// </summary>
public sealed record RuleAction
{
    // toast with ≤ 3 buttons
    public string? Toast { get; init; }

    public IReadOnlyList<string>? Buttons { get; init; }

    public string? Severity { get; init; }

    // badge / card pin
    public string? Badge { get; init; }

    public string? Card { get; init; }

    // pause / resume: refits | tracking | capture | plugin:<id> | job:<id|group> (+ mode polite | freeze)
    public string? Pause { get; init; }

    public string? Resume { get; init; }

    public string? Mode { get; init; }

    // set_priority / efficiency / cap_cores on a target (group:<name>, job:<id>, process:<name>)
    public string? SetPriority { get; init; }

    public bool? Efficiency { get; init; }

    public CapCores? CapCores { get; init; }

    public string? Target { get; init; }

    // plugin_action: the plugin's own declared action
    public string? PluginAction { get; init; }

    // windows (U3): arrange a preset (reversible, one-time consent per rule)
    public string? Arrange { get; init; }

    // capture blocks (U4 D-G6): rules may add blocks, never lift them
    public string? CapturePause { get; init; }

    public string? CaptureExclude { get; init; }

    // built-in rules only (B guardrails)
    public string? UseFallback { get; init; }

    public string? Relearn { get; init; }

    // snooze an alert
    public string? Snooze { get; init; }
}

public sealed record CapCores
{
    public required int Max { get; init; }
}
