namespace Tasktor.Contracts.Rules;

/// <summary>U4 D-G3 Settings → Rules: every rule with its switch, where it comes from and when it last fired; load errors.</summary>
public sealed record RulesReport
{
    public required IReadOnlyList<RuleInfo> Rules { get; init; }

    /// <summary>Rules that did not load, as "rules.json line 12 (id): why".</summary>
    public required IReadOnlyList<string> Errors { get; init; }

    /// <summary>The rules of packs that are on (U5: each starts off until the user ticks it).</summary>
    public IReadOnlyList<PackRuleInfo> PackRules { get; set; } = [];

    /// <summary>Rules that would arrange windows and wait for the user's one-time consent (U3 OQ-W2).</summary>
    public IReadOnlyList<ArrangeRuleInfo> AwaitingArrange { get; set; } = [];
}

public sealed record ArrangeRuleInfo
{
    public required string Id { get; init; }

    public string? Description { get; init; }

    /// <summary>The presets it would arrange.</summary>
    public required IReadOnlyList<string> Presets { get; init; }
}

public sealed record PackRuleInfo
{
    public required string Pack { get; init; }

    public required string PackName { get; init; }

    public required string Id { get; init; }

    public string? Description { get; init; }

    public required bool Ticked { get; init; }
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
