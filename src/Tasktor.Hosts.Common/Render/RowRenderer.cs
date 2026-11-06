using System.Globalization;
using System.Text.Json.Nodes;
using Tasktor.Contracts.Cards;

namespace Tasktor.Hosts.Common.Render;

/// <summary>What a row is: a label and value, a meter, a line of text, an alert, a heading, a table header or row, or a chart.</summary>
public enum RowKind
{
    Value,
    Meter,
    Text,
    Alert,
    Heading,
    TableHeader,
    TableRow,
    Chart,

    /// <summary>A pulled tree (<see cref="CardRow.Series"/> names the table, e.g. <c>processes</c>): apps → processes.</summary>
    Tree,
}

/// <summary>
/// One row of a card in the deep window. <see cref="Fraction"/> fills a meter (0–1); <see cref="Cells"/> are a table's
/// columns; <see cref="RowKey"/> fills a row action's <c>{row}</c>; <see cref="Points"/> / <see cref="Series"/> feed a chart.
/// </summary>
public sealed record CardRow(
    RowKind Kind,
    string Label,
    string? Value = null,
    double? Fraction = null,
    Severity Severity = Severity.Normal,
    IReadOnlyList<string>? Cells = null,
    string? RowKey = null,
    IReadOnlyList<double>? Points = null,
    string? Series = null);

/// <summary>
/// A card as rows, with its title, severity word, plugin source, "as of" time when stale, its card-level actions, and the
/// actions that belong to one table row (their target holds <c>{row}</c>, filled from <see cref="CardRow.RowKey"/>).
/// </summary>
public sealed record CardRows(string Id, string Title, Severity Severity, string? Source, string? AsOf, IReadOnlyList<CardRow> Rows, IReadOnlyList<CardAction> Actions, IReadOnlyList<CardAction> RowActions);

/// <summary>
/// U4 D-G1 / amended D-G13: the deep window's <see cref="IBlockRenderer{TOutput}"/>. Cards become Settings-style rows
/// (label left, value right, hairlines between, D-G14): no card chrome. Severity stays a word on the row, never a colour
/// field; destructive actions read "End…". A block kind it does not know is skipped, never guessed at.
/// </summary>
public sealed class RowRenderer(DateTimeOffset now) : IBlockRenderer<CardRows>
{
    public CardRows Render(FittedCard card, BindingResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(resolver);
        var c = card.Card;
        var rows = new List<CardRow>();
        foreach (var fb in card.Blocks)
        {
            Add(rows, fb, resolver);
        }

        var source = c.Source is { Kind: CardSourceKind.Plugin } src ? src.Name ?? src.Plugin : null;
        var asOf = c.StaleAfterS is { } stale && c.UpdatedAt is { } at && now - at > TimeSpan.FromSeconds(stale)
            ? "as of " + at.ToOffset(now.Offset).ToString("H:mm", CultureInfo.InvariantCulture)
            : null;
        var actions = card.Actions.Select(a => a.Destructive == true && !a.Label.EndsWith('…') ? a with { Label = a.Label + "…" } : a).ToList();
        var perRow = actions.Where(NeedsRow).ToList();
        return new CardRows(c.Id, c.Title, resolver.SeverityOf(c.Severity), source, asOf, rows, [.. actions.Except(perRow)], perRow);
    }

    private void Add(List<CardRow> rows, FittedBlock fb, BindingResolver r)
    {
        var sev = r.SeverityOf(fb.Block.Severity);
        switch (fb.Block)
        {
            case MetricBlock m:
                var trend = m.Trend switch { Trend.Up => " ↑", Trend.Down => " ↓", _ => string.Empty };
                rows.Add(new CardRow(RowKind.Value, m.Label, r.Text(m.Value) + Unit(m.Unit) + trend, Severity: sev));
                break;
            case BadgeBlock b:
                rows.Add(new CardRow(RowKind.Text, r.Text(b.Text), Severity: sev));
                break;
            case AlertBlock a:
                rows.Add(new CardRow(RowKind.Alert, r.Text(a.Text), Severity: sev == Severity.Normal ? Severity.Warn : sev));
                break;
            case BarBlock bar:
                var max = r.Number(bar.Max) ?? 100;
                var text = r.Text(bar.Value) + Unit(bar.Unit) + (bar.Text is null ? string.Empty : " · " + r.Text(bar.Text));
                rows.Add(new CardRow(RowKind.Meter, bar.Label, text, Fraction(r.Number(bar.Value), max), sev));
                break;
            case GaugeBlock g:
                rows.Add(new CardRow(RowKind.Meter, g.Label, r.Text(g.Value) + Unit(g.Unit), Fraction(r.Number(g.Value) - g.Min, g.Max - g.Min), sev));
                break;
            case ProgressBlock p:
                var pct = r.Number(p.Pct) ?? (r.Number(p.Done) is { } d && r.Number(p.Total) is { } tot && tot > 0 ? 100 * d / tot : null);
                var value = (pct is null ? "…" : BindingResolver.Format(JsonValue.Create(Math.Round(pct.Value))) + "%") + (p.Text is null ? string.Empty : " · " + r.Text(p.Text));
                rows.Add(new CardRow(RowKind.Meter, p.Label, value, Fraction(pct, 100), sev));
                break;
            case SparklineBlock s:
                rows.Add(new CardRow(RowKind.Chart, s.Label ?? "History", Unit(s.Unit).Trim(), Severity: sev, Points: s.Points, Series: s.Series));
                break;
            case CountdownBlock cd:
                rows.Add(new CardRow(RowKind.Value, cd.Label, Until(r.Resolve(cd.Until)), Severity: sev));
                break;
            case ListBlock l:
                if (l.Label is not null)
                {
                    rows.Add(new CardRow(RowKind.Heading, l.Label));
                }

                foreach (var item in (r.Array(l.Items) ?? []).Take(fb.Rows ?? l.Max ?? int.MaxValue))
                {
                    rows.Add(new CardRow(RowKind.Text, BindingResolver.Format(item)));
                }

                break;
            case TableBlock t:
                if (t.Label is not null)
                {
                    rows.Add(new CardRow(RowKind.Heading, t.Label));
                }

                rows.Add(new CardRow(RowKind.TableHeader, string.Empty, Cells: [.. t.Columns.Select(col => col.Label)]));
                foreach (var row in (r.Array(t.Rows) ?? []).OfType<JsonObject>().Take(fb.Rows ?? t.Max ?? int.MaxValue))
                {
                    var cells = t.Columns.Select(col => BindingResolver.Format(row[col.Key]) + (row[col.Key] is null ? string.Empty : Unit(col.Unit))).ToList();
                    var key = t.RowKey is { } k && row[k] is { } kv ? BindingResolver.Format(kv) : null;
                    rows.Add(new CardRow(RowKind.TableRow, cells.FirstOrDefault() ?? string.Empty, Cells: cells, RowKey: key));
                }

                break;
            case TreeBlock tree:
                rows.Add(new CardRow(RowKind.Tree, tree.Label ?? "Apps", Series: tree.Table));
                break;
            default:
                break; // ActionsBlock is drawn from the card's actions; unknown kinds are skipped
        }
    }

    private string Until(JsonNode? node)
    {
        if (node is not JsonValue v || !v.TryGetValue<string>(out var s) || !DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
        {
            return BindingResolver.Missing;
        }

        var left = at - now;
        return left <= TimeSpan.Zero ? "now"
            : left < TimeSpan.FromHours(1) ? "in " + Math.Ceiling(left.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min"
            : "at " + at.ToOffset(now.Offset).ToString("H:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>A row action: a catalog action whose target names <c>{row}</c>.</summary>
    public static bool NeedsRow(CardAction action) =>
        action is { Action.Length: > 0, Target: { } t } && t.Values.Any(v => v.Contains("{row}", StringComparison.Ordinal));

    private static double? Fraction(double? value, double max) => value is { } v && max > 0 ? Math.Clamp(v / max, 0, 1) : null;

    private static string Unit(string? unit) => string.IsNullOrEmpty(unit) ? string.Empty : unit == "%" ? "%" : " " + unit;
}
