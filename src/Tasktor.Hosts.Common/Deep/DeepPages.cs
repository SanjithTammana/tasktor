using Tasktor.Contracts.Cards;

namespace Tasktor.Hosts.Common.Deep;

/// <summary>One page of the deep window: its id (the <c>--open</c> argument), its name, and the cards it shows.</summary>
public sealed record DeepPage(string Id, string Name, IReadOnlyList<string> CardPrefixes, bool AlwaysShown);

/// <summary>
/// U4 D-G3 / amended D-G13: the deep window's nine pages and which cards each shows. A page for a Module that is off is
/// hidden; the viewer sees that from the snapshot (a Module's cards exist only while it is on), so no Module flags travel
/// in the contract. Home shows the pinned cards (default System, Jobs, Workflow) under the alerts.
/// </summary>
public static class DeepPages
{
    public const string Home = "home";

    public static IReadOnlyList<string> DefaultPinned { get; } = ["core.system", "core.jobs", "core.workflow"];

    public static IReadOnlyList<DeepPage> All { get; } =
    [
        new(Home, "Home", [], AlwaysShown: true),
        new("system", "System", ["core.system", "core.apps", "core.disk", "core.battery", "core.guards", "module.ports", "module.batteries", "module.connectivity", "module.folders"], AlwaysShown: true),
        new("jobs", "Jobs", ["core.jobs", "module.agents"], AlwaysShown: true),
        new("activity", "Activity", ["core.workflow", "module.projects", "module.deadlines", "module.next_event"], AlwaysShown: true),
        new("learned", "What Tasktor learned", [], AlwaysShown: true),
        new("focus", "Focus", ["module.focus"], AlwaysShown: false),
        new("windows", "Windows", ["module.presets", "module.windows_watch"], AlwaysShown: false),
        new("data", "Your data", ["core.capture"], AlwaysShown: true),
        new("settings", "Settings", [], AlwaysShown: true),
    ];

    /// <summary>Pages the user may hide in Settings → Appearance: all but Home, Your data and Settings (U4: forget and export stay reachable).</summary>
    public static IReadOnlyList<DeepPage> Hideable { get; } = [.. All.Where(p => p.Id is not (Home or "data" or "settings"))];

    /// <summary>
    /// The pages to list: every always-shown page, and a Module page only while one of its cards exists, minus the ones the
    /// user hid (never Home, Your data or Settings).
    /// </summary>
    public static IReadOnlyList<DeepPage> Visible(IReadOnlyList<Card> cards, IReadOnlyCollection<string>? hidden = null)
    {
        ArgumentNullException.ThrowIfNull(cards);
        return [.. All.Where(p => (p.AlwaysShown || cards.Any(c => Owns(p, c.Id))) && !(hidden?.Contains(p.Id) == true && Hideable.Contains(p)))];
    }

    /// <summary>The hidden pages from the snapshot (<c>state.hidden_pages</c>); empty when none.</summary>
    public static IReadOnlyList<string> HiddenPages(System.Text.Json.Nodes.JsonNode? root) =>
        root?["state"]?["hidden_pages"] is System.Text.Json.Nodes.JsonArray a
            ? [.. a.OfType<System.Text.Json.Nodes.JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).OfType<string>()]
            : [];

    /// <summary>The cards a page shows, in snapshot order. Home: the pinned ones, in pin order.</summary>
    public static IReadOnlyList<Card> CardsFor(string pageId, IReadOnlyList<Card> cards, IReadOnlyList<string>? pinned = null)
    {
        ArgumentNullException.ThrowIfNull(cards);
        if (pageId == Home)
        {
            return [.. (pinned ?? DefaultPinned).Select(id => cards.FirstOrDefault(c => c.Id == id)).OfType<Card>()];
        }

        return All.FirstOrDefault(p => p.Id == pageId) is { } page ? [.. cards.Where(c => Owns(page, c.Id))] : [];
    }

    /// <summary>Home's pins from the snapshot (<c>state.pinned</c>): null when never chosen (the defaults apply).</summary>
    public static IReadOnlyList<string>? Pinned(System.Text.Json.Nodes.JsonNode? root) =>
        root?["state"]?["pinned"] is System.Text.Json.Nodes.JsonArray a
            ? [.. a.OfType<System.Text.Json.Nodes.JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).OfType<string>()]
            : null;

    /// <summary>A card's own "Pin to Home" / "Unpin from Home" (<c>card.pin</c> / <c>card.unpin</c>, reversible).</summary>
    public static CardAction PinAction(string cardId, IReadOnlyList<string>? pinned)
    {
        var on = (pinned ?? DefaultPinned).Contains(cardId);
        return new CardAction
        {
            Id = on ? "unpin" : "pin",
            Label = on ? "Unpin from Home" : "Pin to Home",
            Action = on ? "card.unpin" : "card.pin",
            Target = new Dictionary<string, string>(StringComparer.Ordinal) { ["card"] = cardId },
        };
    }

    /// <summary>The page a launch asks for: the word after <c>--open</c> when it names a page, else Home.</summary>
    public static string FromArgs(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var at = args.ToList().IndexOf("--open");
        return at >= 0 && at + 1 < args.Count && All.Any(p => p.Id == args[at + 1]) ? args[at + 1] : Home;
    }

    /// <summary>A known page id, else Home (a stale link or a typo lands somewhere useful).</summary>
    public static string Normalize(string? pageId) => All.Any(p => p.Id == pageId) ? pageId! : Home;

    // Plugin cards (any other id) belong to no page here; Home shows them when pinned.
    private static bool Owns(DeepPage page, string cardId) => page.CardPrefixes.Any(p => cardId == p || cardId.StartsWith(p + ".", StringComparison.Ordinal));
}
