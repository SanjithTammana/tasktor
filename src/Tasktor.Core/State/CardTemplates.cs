using System.Text.Json;
using Tasktor.Contracts;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Platform;

namespace Tasktor.Core.State;

/// <summary>
/// Card structures from every <see cref="ICardProvider"/> (U1). Values bind to state, so cards change only when a card's
/// structure changes; <see cref="Refresh"/> reports whether they did (compared by their JSON). Loop-only.
/// </summary>
public sealed class CardTemplates
{
    private readonly List<ICardProvider> _providers = [];
    private string _lastJson = "[]";

    public IReadOnlyList<Card> Cards { get; private set; } = [];

    public void Add(ICardProvider provider) => _providers.Add(provider);

    public void Remove(ICardProvider provider) => _providers.Remove(provider);

    /// <summary>Collect cards; returns true when the structure changed since the last refresh.</summary>
    public bool Refresh()
    {
        var cards = new List<Card>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in _providers)
        {
            foreach (var c in p.Cards())
            {
                if (ids.Add(c.Id))
                {
                    cards.Add(c);
                }
            }
        }

        var json = JsonSerializer.Serialize(cards, ContractsJsonContext.Default.IReadOnlyListCard);
        if (json == _lastJson)
        {
            return false;
        }

        _lastJson = json;
        Cards = cards;
        return true;
    }

    public Card? Find(string id) => Cards.FirstOrDefault(c => c.Id == id);
}
