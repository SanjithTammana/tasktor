using Tasktor.Contracts.Platform;

namespace Tasktor.Core.Rules;

/// <summary>A named group's status this evaluation (D-H6 <c>group</c>): processes running, busy ones (JobGate's classifier).</summary>
public sealed record GroupStatus(int Count, int Busy);

/// <summary>
/// One thing a rule can fire for: a job, a process, a volume instance, an event. <see cref="Values"/> hold numbers
/// (cores, age_s, quiet_s …), <see cref="Thresholds"/> learned thresholds by stat, <see cref="Fields"/> placeholder
/// text, <see cref="Target"/> the action target when the rule names none.
/// </summary>
public sealed record RuleSubject
{
    public required string Id { get; init; }

    public string? Name { get; init; }

    public IReadOnlyDictionary<string, double> Values { get; init; } = new Dictionary<string, double>();

    public IReadOnlyDictionary<string, LearnedValue> Thresholds { get; init; } = new Dictionary<string, LearnedValue>();

    public IReadOnlyDictionary<string, string> Fields { get; init; } = new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string>? Target { get; init; }
}

/// <summary>A pushed event (file.created, job.done …) with its fields.</summary>
public sealed record RuleEvent
{
    public required string Name { get; init; }

    public required string Id { get; init; }

    public IReadOnlyDictionary<string, string> Fields { get; init; } = new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string>? Target { get; init; }
}

/// <summary>
/// Everything one evaluation reads (D-H6: "snapshot + events in, action requests out"). Plain data, built by the agent
/// on the loop; tests build it by hand.
/// </summary>
public sealed class RuleInputs
{
    public Dictionary<MetricKey, double> Metrics { get; } = [];

    /// <summary>State values: <c>"true"</c> / <c>"false"</c>, or a value such as <c>fullscreen = exclusive</c>.</summary>
    public Dictionary<string, string> States { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, GroupStatus> Groups { get; } = new(StringComparer.Ordinal);

    public List<RuleSubject> Processes { get; } = [];

    public List<RuleSubject> Jobs { get; } = [];

    public List<RuleEvent> Events { get; } = [];

    /// <summary>Placeholders every subject sees (e.g. A6's <c>{offender}</c>, <c>{growth}</c>).</summary>
    public Dictionary<string, string> Fields { get; } = new(StringComparer.Ordinal);

    /// <summary>The action target for rules without a subject of their own (e.g. A6's offender app).</summary>
    public Dictionary<string, string>? Target { get; set; }

    public RuleInputs Metric(string id, double value, string instance = "")
    {
        Metrics[new MetricKey(id, instance)] = value;
        return this;
    }

    public RuleInputs State(string name, bool on) => State(name, on ? "true" : "false");

    public RuleInputs State(string name, string value)
    {
        States[name] = value;
        return this;
    }
}
