using Tasktor.Contracts.Rules;

namespace Tasktor.Core.Rules;

/// <summary>Where a rule came from: the shipped read-only defaults or the user's <c>rules.json</c>.</summary>
public enum RuleOrigin
{
    Default,

    /// <summary>A ticked entry of a pack's <c>rules.pack.json</c> (U5 amendment: every entry off until ticked).</summary>
    Pack,

    User,
}

/// <summary>A rule that loaded, with its parsed durations.</summary>
public sealed record CompiledRule(Rule Rule, RuleOrigin Origin, int Line, TimeSpan HoldFor, TimeSpan Cooldown)
{
    public string Id => Rule.Id;

    public bool IsBuiltin => Origin == RuleOrigin.Default && Rule.Builtin == true;
}

/// <summary>A rule that did not load (disabled, listed on the error card with its line number). The rest keep running.</summary>
public sealed record RuleError(string? Id, RuleOrigin Origin, int Line, string Message)
{
    public string File => Origin switch { RuleOrigin.Default => "rules.default.json", RuleOrigin.Pack => "rules.pack.json", _ => "rules.json" };

    public override string ToString() => File + " line " + Line + (Id is null ? string.Empty : " (" + Id + ")") + ": " + Message;
}

/// <summary>The effective rules: shipped defaults overlaid by the user's file (same id overrides; <c>enabled: false</c> turns off).</summary>
public sealed class RuleSet
{
    public RuleSet(IReadOnlyList<CompiledRule> rules, IReadOnlyList<RuleError> errors, IReadOnlyDictionary<string, IReadOnlyList<string>> groups, IReadOnlyList<GroupingRule> grouping)
    {
        Rules = rules;
        Errors = errors;
        Groups = groups;
        Grouping = grouping;
    }

    public static RuleSet Empty { get; } = new([], [], new Dictionary<string, IReadOnlyList<string>>(), []);

    public IReadOnlyList<CompiledRule> Rules { get; }

    public IReadOnlyList<RuleError> Errors { get; }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Groups { get; }

    /// <summary>A2.2 grouping rules, user entries first.</summary>
    public IReadOnlyList<GroupingRule> Grouping { get; }

    public CompiledRule? Find(string id) => Rules.FirstOrDefault(r => r.Id == id);
}
