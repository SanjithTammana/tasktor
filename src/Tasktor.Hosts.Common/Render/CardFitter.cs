using System.Text.Json.Nodes;
using Tasktor.Contracts.Cards;

namespace Tasktor.Hosts.Common.Render;

/// <summary>Render targets and their budgets (U4 D-G1).</summary>
public enum FitTarget
{
    /// <summary>Widget small: 2 units, 1 touch target (the whole card).</summary>
    Small,

    /// <summary>Widget medium: 5 units, 3 touch targets.</summary>
    Medium,

    /// <summary>Widget large: 9 units, 4 touch targets.</summary>
    Large,

    /// <summary>Flyout and CLI <c>status</c>: everything.</summary>
    Flyout,
}

/// <summary>A block kept by the fitter; tables and lists may be cut to <see cref="Rows"/>.</summary>
public sealed record FittedBlock(Block Block, int? Rows);

/// <summary>A card after fitting: the blocks and actions that survive, in the provider's order.</summary>
public sealed record FittedCard(Card Card, IReadOnlyList<FittedBlock> Blocks, IReadOnlyList<CardAction> Actions, int UnitsUsed);

/// <summary>
/// Keeps the highest-priority blocks that fit the target (U4 D-G1 step 2). Units: Metric / Badge / Countdown / Sparkline /
/// Bar / Gauge / Progress / Alert = 1; each Table or List row = 1 (a table may be cut to the rows that fit, at least one);
/// Tree is flyout-only. Ties keep the provider's order. Actions count against touch targets, not units.
/// </summary>
public static class CardFitter
{
    public static (int Units, int Touch) Budget(FitTarget target) => target switch
    {
        FitTarget.Small => (2, 1),
        FitTarget.Medium => (5, 3),
        FitTarget.Large => (9, 4),
        _ => (int.MaxValue, int.MaxValue),
    };

    public static FittedCard Fit(Card card, FitTarget target, BindingResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(resolver);
        var (units, touch) = Budget(target);
        var order = card.Blocks.Select((b, i) => (b, i)).OrderBy(x => x.b.Priority).ThenBy(x => x.i).ToList();
        var kept = new List<(int Index, FittedBlock Block)>();
        var used = 0;
        foreach (var (b, i) in order)
        {
            if (b is ActionsBlock)
            {
                continue; // actions spend touch targets, handled below
            }

            if (b is TreeBlock && target != FitTarget.Flyout)
            {
                continue;
            }

            var cost = Cost(b, resolver, out var rows);
            if (target == FitTarget.Flyout)
            {
                kept.Add((i, new FittedBlock(b, rows)));
                used += cost;
                continue;
            }

            var left = units - used;
            if (left <= 0)
            {
                break;
            }

            if (cost <= left)
            {
                kept.Add((i, new FittedBlock(b, rows)));
                used += cost;
            }
            else if (b is TableBlock or ListBlock && rows is > 0)
            {
                kept.Add((i, new FittedBlock(b, left)));
                used += left;
            }
        }

        // S: the whole card is the one touch target, so no buttons; M / L keep the highest-priority actions.
        var actions = (card.Actions ?? []).Select((a, i) => (a, i)).OrderBy(x => x.a.Priority).ThenBy(x => x.i)
            .Take(target == FitTarget.Small ? 0 : touch).OrderBy(x => x.i).Select(x => x.a).ToList();
        return new FittedCard(card, kept.OrderBy(k => k.Index).Select(k => k.Block).ToList(), actions, used);
    }

    /// <summary>
    /// The tray tooltip / CLI one-liner (D-G1, first row): the card's top <c>Metric</c> and its severity word, e.g.
    /// "System: CPU 34 % · warning". Falls back to the first Badge or Countdown label.
    /// </summary>
    public static string Glance(Card card, BindingResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(resolver);
        var top = card.Blocks.Select((b, i) => (b, i)).OrderBy(x => x.b.Priority).ThenBy(x => x.i).Select(x => x.b).ToList();
        var text = top.OfType<MetricBlock>().FirstOrDefault() is { } m
            ? m.Label + " " + resolver.Text(m.Value) + (m.Unit is { } u ? " " + u : string.Empty)
            : top.OfType<BadgeBlock>().FirstOrDefault() is { } b ? resolver.Text(b.Text)
            : top.OfType<CountdownBlock>().FirstOrDefault()?.Label ?? string.Empty;
        var severity = resolver.SeverityOf(card.Severity);
        var word = severity switch { Severity.Critical => " · critical", Severity.Warn => " · warning", _ => string.Empty };
        return card.Title + (text.Length > 0 ? ": " + text : string.Empty) + word;
    }

    private static int Cost(Block b, BindingResolver resolver, out int? rows)
    {
        rows = null;
        switch (b)
        {
            case TableBlock t:
                rows = Count(resolver.Resolve(t.Rows), t.Max);
                return Math.Max(1, rows.Value);
            case ListBlock l:
                rows = Count(resolver.Resolve(l.Items), l.Max);
                return Math.Max(1, rows.Value);
            default:
                return 1;
        }
    }

    private static int Count(JsonNode? node, int? max)
    {
        var n = node is JsonArray a ? a.Count : 0;
        return max is { } m ? Math.Min(n, m) : n;
    }
}
