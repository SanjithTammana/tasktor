using Tasktor.Contracts.Cards;

namespace Tasktor.Hosts.Common.Render;

/// <summary>One run of the shelf sentence; <see cref="Noun"/> names the control it is part of, or null for plain text.</summary>
public sealed record ShelfSegment(string Text, string? Noun = null);

/// <summary>A noun in the sentence and the card actions it unfolds (U4 amended D-G11).</summary>
public sealed record ShelfNoun(string Id, string Text, string CardId, IReadOnlyList<CardAction> Actions);

/// <summary>The shelf's living sentence: segments in reading order, their nouns, and how many more cards need a look.</summary>
public sealed record ShelfSentence(Severity Severity, IReadOnlyList<ShelfSegment> Segments, IReadOnlyList<ShelfNoun> Nouns, int More)
{
    public bool Calm => Severity == Severity.Normal;

    /// <summary>The sentence as plain text (screen readers, the tray menu header, the CLI).</summary>
    public string Text => string.Concat(Segments.Select(s => s.Text));
}

/// <summary>
/// U4 amended D-G11: composes the shelf sentence from the cards, the way <see cref="CardFitter"/> fits a widget. A card
/// speaks through its verdict, the highest-priority <see cref="BadgeBlock"/>. Cards above normal severity speak worst
/// first (ties keep the provider's order), at most <see cref="MaxClauses"/>; the rest are counted in
/// <see cref="ShelfSentence.More"/>. A calm machine is one short sentence: "All quiet." plus, when there is one, the jobs
/// card's verdict. Each action's <see cref="CardAction.Noun"/> marks its first occurrence in that card's clause as a
/// control; a noun that does not occur is skipped (the action stays on the card).
/// </summary>
public static class ShelfComposer
{
    public const int MaxClauses = 3;
    public const string CalmOpening = "All quiet.";
    public const string CalmFactCard = "core.jobs";

    public static ShelfSentence Compose(IReadOnlyList<Card> cards, BindingResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(cards);
        ArgumentNullException.ThrowIfNull(resolver);
        var speaking = cards
            .Select((card, index) => (card, index, severity: resolver.SeverityOf(card.Severity), verdict: Verdict(card, resolver)))
            .Where(c => c.verdict is not null)
            .ToList();
        var loud = speaking.Where(c => c.severity != Severity.Normal)
            .OrderByDescending(c => c.severity).ThenBy(c => c.index).ToList();

        var segments = new List<ShelfSegment>();
        var nouns = new List<ShelfNoun>();
        if (loud.Count == 0)
        {
            segments.Add(new ShelfSegment(CalmOpening));
            if (speaking.FirstOrDefault(c => c.card.Id == CalmFactCard) is { card: not null } fact)
            {
                segments.Add(new ShelfSegment(" "));
                AddClause(fact.card, fact.verdict!, segments, nouns);
            }

            return new ShelfSentence(Severity.Normal, segments, nouns, 0);
        }

        foreach (var (card, _, _, verdict) in loud.Take(MaxClauses))
        {
            if (segments.Count > 0)
            {
                segments.Add(new ShelfSegment(" "));
            }

            AddClause(card, verdict!, segments, nouns);
        }

        return new ShelfSentence(loud[0].severity, segments, nouns, Math.Max(0, loud.Count - MaxClauses));
    }

    private static string? Verdict(Card card, BindingResolver resolver)
    {
        var badge = card.Blocks.Select((b, i) => (b, i)).Where(x => x.b is BadgeBlock)
            .OrderBy(x => x.b.Priority).ThenBy(x => x.i).Select(x => (BadgeBlock)x.b).FirstOrDefault();
        if (badge is null)
        {
            return null;
        }

        var text = resolver.Text(badge.Text).Trim();
        if (text.Length == 0 || text == BindingResolver.Missing)
        {
            return null;
        }

        return text[^1] is '.' or '!' or '?' ? text : text + ".";
    }

    private static void AddClause(Card card, string clause, List<ShelfSegment> segments, List<ShelfNoun> nouns)
    {
        // Each distinct noun once, at its first occurrence; overlapping matches keep the earlier one.
        var found = (card.Actions ?? [])
            .Where(a => !string.IsNullOrWhiteSpace(a.Noun))
            .GroupBy(a => a.Noun!, StringComparer.Ordinal)
            .Select(g => (text: g.Key, at: clause.IndexOf(g.Key, StringComparison.Ordinal), actions: g.OrderBy(a => a.Priority).ToList()))
            .Where(n => n.at >= 0)
            .OrderBy(n => n.at)
            .ToList();
        var pos = 0;
        foreach (var (text, at, actions) in found)
        {
            if (at < pos)
            {
                continue;
            }

            if (at > pos)
            {
                segments.Add(new ShelfSegment(clause[pos..at]));
            }

            var id = card.Id + ":" + text;
            segments.Add(new ShelfSegment(text, id));
            nouns.Add(new ShelfNoun(id, text, card.Id, actions));
            pos = at + text.Length;
        }

        if (pos < clause.Length)
        {
            segments.Add(new ShelfSegment(clause[pos..]));
        }
    }
}

/// <summary>One tick of the edge: its left edge in px, its height as a share of the strip (0.2–1), and whether it is bright.</summary>
public readonly record struct GrainTick(int X, double Height, bool Hot);

/// <summary>
/// U4 amended D-G10: the edge at rest. The last hour of load (oldest first, one sample per minute, 0–1) becomes ticks
/// every <see cref="TickPx"/> px across the strip; height follows load and never drops below <see cref="MinHeight"/>, so
/// the edge is always visible. A tick is bright where its minute had a caution or critical card.
/// </summary>
public static class EdgeGrain
{
    public const int TickPx = 4;
    public const double MinHeight = 0.2;

    public static IReadOnlyList<GrainTick> Compute(IReadOnlyList<double> load, IReadOnlyList<bool> hot, int widthPx)
    {
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(hot);
        if (hot.Count != load.Count)
        {
            throw new ArgumentException("load and hot need one entry per minute each", nameof(hot));
        }

        var n = Math.Max(0, widthPx / TickPx);
        var ticks = new GrainTick[n];
        for (var i = 0; i < n; i++)
        {
            if (load.Count == 0)
            {
                ticks[i] = new GrainTick(i * TickPx, MinHeight, false);
                continue;
            }

            // Linear interpolation between the two nearest minutes; the bright flag follows the nearer one.
            var pos = n == 1 ? load.Count - 1 : (double)i * (load.Count - 1) / (n - 1);
            var lo = (int)Math.Floor(pos);
            var hi = Math.Min(load.Count - 1, lo + 1);
            var t = pos - lo;
            var v = Clamp(load[lo]) * (1 - t) + Clamp(load[hi]) * t;
            var bright = t < 0.5 ? hot[lo] : hot[hi];
            ticks[i] = new GrainTick(i * TickPx, MinHeight + v * (1 - MinHeight), bright);
        }

        return ticks;
    }

    private static double Clamp(double v) => double.IsNaN(v) ? 0 : Math.Clamp(v, 0, 1);
}
