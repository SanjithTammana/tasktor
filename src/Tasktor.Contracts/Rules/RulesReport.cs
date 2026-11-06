namespace Tasktor.Contracts.Rules;

/// <summary>U4 D-G3 Settings → Rules: every rule with its switch, where it comes from and when it last fired; load errors.</summary>
public sealed record RulesReport
{
    public required IReadOnlyList<RuleInfo> Rules { get; init; }

    /// <summary>Rules that did not load, as "rules.json line 12 (id): why".</summary>
    public required IReadOnlyList<string> Errors { get; init; }
}

public sealed record RuleInfo
{
    public required string Id { get; init; }

    public string? Description { get; init; }

    /// <summary><c>default</c> (shipped), <c>pack</c> (a ticked rule pack) or <c>user</c> (rules.json).</summary>
    public required string Source { get; init; }

    public required bool On { get; init; }

    public DateTimeOffset? LastFired { get; init; }
}
