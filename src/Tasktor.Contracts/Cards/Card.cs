using System.Text.Json.Serialization;

namespace Tasktor.Contracts.Cards;

[JsonConverter(typeof(JsonStringEnumConverter<CardSourceKind>))]
public enum CardSourceKind
{
    [JsonStringEnumMemberName("core")] Core,
    [JsonStringEnumMemberName("module")] Module,
    [JsonStringEnumMemberName("plugin")] Plugin,
}

/// <summary>Where a card comes from. Plugin cards carry the plugin id and name for the source badge (U4 D-G1).</summary>
public sealed record CardSource
{
    public required CardSourceKind Kind { get; init; }

    public string? Plugin { get; init; }

    public string? Name { get; init; }
}

/// <summary>
/// A button on a card. <c>id</c> is unique within the card. Built-in cards name a catalog <c>action</c>
/// and its <c>target</c>; plugin cards name only their own declared action id.
/// </summary>
public sealed record CardAction
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    public int Priority { get; init; } = 1;

    public string? Action { get; init; }

    public IReadOnlyDictionary<string, string>? Target { get; init; }

    public IReadOnlyDictionary<string, string>? Args { get; init; }

    /// <summary>Set for destructive actions: renderers label them "End…" and open the two-step confirm.</summary>
    public bool? Destructive { get; init; }

    /// <summary>
    /// U4 amended D-G11: the word in this card's verdict the action belongs to (for example "Chrome" in "Chrome holds
    /// 5.7 GB"). The shelf turns that word into the control that unfolds the action. Optional; without it the action is
    /// still reachable from the card.
    /// </summary>
    public string? Noun { get; init; }
}

/// <summary>A card = ordered blocks + hints (A D-A5/D-A6, U1 D-H2).</summary>
public sealed record Card
{
    public required string Id { get; init; }

    public CardSource? Source { get; init; }

    public required string Title { get; init; }

    public IconKey? Icon { get; init; }

    public Value? Severity { get; init; }

    public required IReadOnlyList<Block> Blocks { get; init; }

    public IReadOnlyList<CardAction>? Actions { get; init; }

    public string? Link { get; init; }

    public int? StaleAfterS { get; init; }

    /// <summary>When the card's content was produced (plugins; used for staleness).</summary>
    public DateTimeOffset? UpdatedAt { get; init; }
}
