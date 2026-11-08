using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tasktor.Contracts.Cards;
using Tasktor.Hosts.Common.Render;

namespace Tasktor.Cli;

/// <summary>
/// The CLI's <c>IBlockRenderer</c> (U4 D-G1): plain text at flyout size. Severity is a word, never colour; plugin cards
/// carry their source; stale cards say "as of"; destructive actions read "End…".
/// </summary>
public sealed class TextRenderer(DateTimeOffset now) : IBlockRenderer<string>
{
    private const string Bars = "▁▂▃▄▅▆▇█";

    public string Render(FittedCard card, BindingResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(resolver);
        var c = card.Card;
        var sb = new StringBuilder();
        var sev = resolver.SeverityOf(c.Severity);
        sb.Append("== ").Append(c.Title);
        if (sev != Severity.Normal)
        {
            sb.Append("  [").Append(sev == Severity.Critical ? "Critical" : "Warning").Append(']');
        }

        if (c.Source is { Kind: CardSourceKind.Plugin } src)
        {
            sb.Append("  (plugin: ").Append(src.Name ?? src.Plugin).Append(')');
        }

        if (c.StaleAfterS is { } stale && c.UpdatedAt is { } at && now - at > TimeSpan.FromSeconds(stale))
        {
            sb.Append("  (as of ").Append(at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)).Append(')');
        }

        sb.Append('\n');
        foreach (var fb in card.Blocks)
        {
            RenderBlock(sb, fb, resolver);
        }

        if (card.Actions.Count > 0)
        {
            sb.Append("  Actions: ").Append(string.Join(", ", card.Actions.Select(a => a.Destructive == true && !a.Label.EndsWith('…') ? a.Label + "…" : a.Label))).Append('\n');
        }

        return sb.ToString();
    }

    private void RenderBlock(StringBuilder sb, FittedBlock fb, BindingResolver r)
    {
        var sevWord = r.SeverityOf(fb.Block.Severity) switch { Severity.Critical => " (critical)", Severity.Warn => " (warning)", _ => string.Empty };
        switch (fb.Block)
        {
            case MetricBlock m:
                sb.Append("  ").Append(m.Label).Append(": ").Append(r.Text(m.Value)).Append(Unit(m.Unit));
                if (m.Trend is { } trend && trend != Trend.Flat)
                {
                    sb.Append(trend == Trend.Up ? " ↑" : " ↓");
                }

                sb.Append(sevWord).Append('\n');
                break;
            case BadgeBlock b:
                sb.Append("  ").Append(r.Text(b.Text)).Append(sevWord).Append('\n');
                break;
            case AlertBlock a:
                sb.Append("  ! ").Append(r.Text(a.Text)).Append(sevWord).Append('\n');
                break;
            case BarBlock bar:
                var v = r.Number(bar.Value);
                var max = r.Number(bar.Max) ?? 100;
                sb.Append("  ").Append(bar.Label).Append(' ').Append(Meter(v, max)).Append(' ').Append(r.Text(bar.Value)).Append(Unit(bar.Unit));
                if (bar.Text is not null)
                {
                    sb.Append("  ").Append(r.Text(bar.Text));
                }

                sb.Append(sevWord).Append('\n');
                break;
            case GaugeBlock g:
                sb.Append("  ").Append(g.Label).Append(' ').Append(Meter(r.Number(g.Value) - g.Min, g.Max - g.Min)).Append(' ').Append(r.Text(g.Value)).Append(Unit(g.Unit)).Append(sevWord).Append('\n');
                break;
            case ProgressBlock p:
                var pct = r.Number(p.Pct) ?? (r.Number(p.Done) is { } d && r.Number(p.Total) is { } tot && tot > 0 ? 100 * d / tot : null);
                sb.Append("  ").Append(p.Label).Append(' ').Append(Meter(pct, 100)).Append(' ').Append(pct is null ? "…" : BindingResolver.Format(JsonValue.Create(pct.Value)) + "%");
                if (p.Text is not null)
                {
                    sb.Append("  ").Append(r.Text(p.Text));
                }

                sb.Append('\n');
                break;
            case SparklineBlock s:
                sb.Append("  ").Append(s.Label ?? "History").Append(": ");
                sb.Append(s.Points is { Count: > 0 } pts ? Spark(pts) : "(live chart in the flyout)").Append('\n');
                break;
            case CountdownBlock cd:
                sb.Append("  ").Append(cd.Label).Append(": ").Append(Until(r.Resolve(cd.Until))).Append('\n');
                break;
            case ListBlock l:
                if (l.Label is not null)
                {
                    sb.Append("  ").Append(l.Label).Append(":\n");
                }

                var items = r.Array(l.Items) ?? [];
                foreach (var item in items.Take(fb.Rows ?? int.MaxValue))
                {
                    sb.Append("   - ").Append(BindingResolver.Format(item)).Append('\n');
                }

                break;
            case TableBlock t:
                RenderTable(sb, t, r.Array(t.Rows) ?? [], fb.Rows);
                break;
            case TreeBlock tree:
                sb.Append("  ").Append(tree.Label ?? tree.Table).Append(": (tree in the flyout)\n");
                break;
        }
    }

    private static void RenderTable(StringBuilder sb, TableBlock t, JsonArray rows, int? limit)
    {
        if (t.Label is not null)
        {
            sb.Append("  ").Append(t.Label).Append(":\n");
        }

        var shown = rows.Take(limit ?? int.MaxValue).OfType<JsonObject>().ToList();
        var cells = shown.Select(row => t.Columns.Select(c => BindingResolver.Format(row[c.Key]) + (row[c.Key] is null ? string.Empty : Unit(c.Unit))).ToArray()).ToList();
        var widths = t.Columns.Select((c, i) => Math.Max(c.Label.Length, cells.Count == 0 ? 0 : cells.Max(x => x[i].Length))).ToArray();
        sb.Append("   ").Append(string.Join("  ", t.Columns.Select((c, i) => c.Label.PadRight(widths[i])))).Append('\n');
        foreach (var row in cells)
        {
            sb.Append("   ").Append(string.Join("  ", row.Select((x, i) => x.PadRight(widths[i])))).Append('\n');
        }

        if (shown.Count == 0)
        {
            sb.Append("   (none)\n");
        }
    }

    private static string Unit(string? unit) => string.IsNullOrEmpty(unit) ? string.Empty : unit == "%" ? "%" : " " + unit;

    private static string Meter(double? value, double max)
    {
        const int width = 10;
        if (value is null || max <= 0)
        {
            return "[" + new string('·', width) + "]";
        }

        var filled = (int)Math.Round(Math.Clamp(value.Value / max, 0, 1) * width);
        return "[" + new string('#', filled) + new string('-', width - filled) + "]";
    }

    private static string Spark(IReadOnlyList<double> points)
    {
        var min = points.Min();
        var max = points.Max();
        var span = max - min;
        return new string(points.Select(p => Bars[span <= 0 ? 0 : (int)Math.Round((p - min) / span * (Bars.Length - 1))]).ToArray());
    }

    private string Until(JsonNode? node)
    {
        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.String
            && DateTimeOffset.TryParse(v.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
        {
            var left = at - now;
            return left <= TimeSpan.Zero ? "now" : "in " + Core(left);
        }

        return BindingResolver.Missing;
    }

    private static string Core(TimeSpan s) =>
        s.TotalDays >= 1 ? $"{(int)s.TotalDays} d {s.Hours} h"
        : s.TotalHours >= 1 ? $"{(int)s.TotalHours} h {s.Minutes} m"
        : $"{Math.Max(1, (int)s.TotalMinutes)} min";
}
