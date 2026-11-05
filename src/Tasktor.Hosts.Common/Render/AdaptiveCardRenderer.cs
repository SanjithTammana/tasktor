using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Tasktor.Contracts.Cards;

namespace Tasktor.Hosts.Common.Render;

/// <summary>An Adaptive Card 1.5 template and its data (U4 D-G5: structure changes rebuild the template; samples change only the data).</summary>
public sealed record AdaptiveCardOutput(JsonObject Template, JsonObject Data)
{
    public string TemplateJson => Template.ToJsonString();

    public string DataJson => Data.ToJsonString();
}

/// <summary>
/// The widget's <see cref="IBlockRenderer{TOutput}"/> (U4 D-G1 step 3, D-G5): a fitted card → an Adaptive Card 1.5 template
/// whose values are <c>${…}</c> references into a separate data object. Every block kind maps to plain 1.5 elements (no
/// chart element exists: bars and gauges are weighted column pairs, sparklines are block characters). Common rules: severity
/// is an icon and a word, never colour alone; a stale card greys out with "as of hh:mm"; a plugin card carries its source
/// badge; a destructive action says "End…" and deep-links to the flyout with the confirm step armed (reversible actions run
/// in place through <c>Action.Execute</c>); the card body opens the flyout at that card. Pure: no I/O, AOT-safe.
/// </summary>
public sealed class AdaptiveCardRenderer : IBlockRenderer<AdaptiveCardOutput>
{
    public const string Version = "1.5";
    public static readonly string Sparks = "▁▂▃▄▅▆▇█";

    private readonly Func<DateTimeOffset> _now;
    private readonly TimeZoneInfo _zone;
    private readonly Func<string, bool> _destructive;

    /// <param name="now">The clock (stale cards and countdowns are computed at render time).</param>
    /// <param name="destructive">Whether an action id is destructive in the catalog (the host passes the catalog's view).</param>
    /// <param name="zone">Display zone for "as of hh:mm".</param>
    public AdaptiveCardRenderer(Func<DateTimeOffset> now, Func<string, bool>? destructive = null, TimeZoneInfo? zone = null)
    {
        _now = now;
        _destructive = destructive ?? (_ => false);
        _zone = zone ?? TimeZoneInfo.Local;
    }

    public static (string Icon, string Word) SeverityLabel(Severity s) => s switch
    {
        Severity.Critical => ("⛔", "Critical"),
        Severity.Warn => ("⚠", "Warning"),
        _ => ("✓", "Normal"),
    };

    public AdaptiveCardOutput Render(FittedCard card, BindingResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(resolver);
        var c = card.Card;
        var data = new JsonObject();
        var body = new JsonArray();

        // Header: title, severity (icon + word), source badge, stale line.
        var severity = resolver.SeverityOf(c.Severity);
        var (icon, word) = SeverityLabel(severity);
        data["title"] = c.Title;
        data["severity"] = new JsonObject { ["icon"] = icon, ["word"] = word, ["show"] = severity != Severity.Normal, ["color"] = Color(severity) };
        var stale = c.StaleAfterS is { } s && c.UpdatedAt is { } at && _now() - at > TimeSpan.FromSeconds(s);
        data["stale"] = stale;
        data["as_of"] = c.UpdatedAt is { } u ? "as of " + TimeZoneInfo.ConvertTime(u, _zone).ToString("HH:mm", CultureInfo.InvariantCulture) : string.Empty;
        var header = new JsonArray
        {
            (JsonNode)new JsonObject
            {
                ["type"] = "Column",
                ["width"] = "stretch",
                ["items"] = new JsonArray((JsonNode)Text("${title}", weight: "Bolder", subtle: "${stale}")),
            },
            (JsonNode)new JsonObject
            {
                ["type"] = "Column",
                ["width"] = "auto",
                ["$when"] = "${severity.show}",
                ["items"] = new JsonArray((JsonNode)Text("${severity.icon} ${severity.word}", color: "${severity.color}")),
            },
        };
        if (c.Source is { Kind: CardSourceKind.Plugin } src)
        {
            data["source"] = "from " + (src.Name ?? src.Plugin ?? "a plugin");
            header.Add((JsonNode)new JsonObject { ["type"] = "Column", ["width"] = "auto", ["items"] = new JsonArray((JsonNode)Text("${source}", subtle: true, size: "Small")) });
        }

        body.Add((JsonNode)new JsonObject { ["type"] = "ColumnSet", ["columns"] = header });
        body.Add((JsonNode)Text("${as_of}", subtle: true, size: "Small", when: "${stale}"));

        for (var i = 0; i < card.Blocks.Count; i++)
        {
            var key = "b" + i.ToString(CultureInfo.InvariantCulture);
            var (element, values) = Block(card.Blocks[i], key, resolver);
            if (element is null)
            {
                continue;
            }

            data[key] = values;
            body.Add((JsonNode)element);
        }

        var actions = new JsonArray();
        foreach (var a in card.Actions)
        {
            actions.Add((JsonNode)Action(c, a));
        }

        var template = new JsonObject
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["type"] = "AdaptiveCard",
            ["version"] = Version,
            ["body"] = body,
            ["selectAction"] = new JsonObject { ["type"] = "Action.OpenUrl", ["url"] = "tasktor://open?card=" + Uri.EscapeDataString(c.Id) },
        };
        if (actions.Count > 0)
        {
            template["actions"] = actions;
        }

        return new AdaptiveCardOutput(template, data);
    }

    /// <summary>
    /// One template for all three widget sizes (D-G5): each size's fitted body in a container shown with
    /// <c>$when</c> on <c>$host.widgetSize</c>, values from one merged data object.
    /// </summary>
    public AdaptiveCardOutput RenderAllSizes(Card card, BindingResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(card);
        var body = new JsonArray();
        var data = new JsonObject();
        JsonNode? actions = null;
        foreach (var (target, size) in new[] { (FitTarget.Small, "small"), (FitTarget.Medium, "medium"), (FitTarget.Large, "large") })
        {
            var one = Render(CardFitter.Fit(card, target, resolver), resolver);
            data[size] = one.Data;
            var items = Rebase(one.Template["body"]!.DeepClone(), size);
            body.Add((JsonNode)new JsonObject { ["type"] = "Container", ["$when"] = "${$host.widgetSize==\"" + size + "\"}", ["items"] = items });
            if (target == FitTarget.Large)
            {
                actions = one.Template["actions"]?.DeepClone();
            }
        }

        var template = new JsonObject
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["type"] = "AdaptiveCard",
            ["version"] = Version,
            ["body"] = body,
            ["selectAction"] = new JsonObject { ["type"] = "Action.OpenUrl", ["url"] = "tasktor://open?card=" + Uri.EscapeDataString(card.Id) },
        };
        if (actions is not null)
        {
            template["actions"] = actions;
        }

        return new AdaptiveCardOutput(template, data);
    }

    /// <summary>Prefix every <c>${name…}</c> reference (except <c>$host</c>, <c>$data</c>, <c>$index</c>) with the size's data scope.</summary>
    private static JsonNode Rebase(JsonNode node, string scope)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(kv => kv.Key).ToList())
                {
                    if (o[key] is { } child)
                    {
                        o[key] = Rebase(child.DeepClone(), scope);
                    }
                }

                return o;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    if (a[i] is { } child)
                    {
                        a[i] = Rebase(child.DeepClone(), scope);
                    }
                }

                return a;
            case JsonValue v when v.TryGetValue<string>(out var s) && s.Contains("${", StringComparison.Ordinal):
                var sb = new StringBuilder();
                var i0 = 0;
                while (true)
                {
                    var at = s.IndexOf("${", i0, StringComparison.Ordinal);
                    if (at < 0)
                    {
                        sb.Append(s, i0, s.Length - i0);
                        break;
                    }

                    sb.Append(s, i0, at - i0 + 2);
                    var rest = s.AsSpan(at + 2);
                    if (!rest.StartsWith("$") && !rest.StartsWith("!"))
                    {
                        sb.Append(scope).Append('.');
                    }

                    i0 = at + 2;
                }

                return JsonValue.Create(sb.ToString());
            default:
                return node;
        }
    }

    private static string Color(Severity s) => s switch { Severity.Critical => "Attention", Severity.Warn => "Warning", _ => "Default" };

    private static JsonObject Text(string text, string? weight = null, object? subtle = null, string? size = null, string? color = null, string? when = null, bool wrap = true)
    {
        var o = new JsonObject { ["type"] = "TextBlock", ["text"] = text, ["wrap"] = wrap };
        if (weight is not null)
        {
            o["weight"] = weight;
        }

        if (subtle is bool b)
        {
            o["isSubtle"] = b;
        }
        else if (subtle is string bind)
        {
            o["isSubtle"] = bind;
        }

        if (size is not null)
        {
            o["size"] = size;
        }

        if (color is not null)
        {
            o["color"] = color;
        }

        if (when is not null)
        {
            o["$when"] = when;
        }

        return o;
    }

    private (JsonObject? Element, JsonObject Values) Block(FittedBlock fitted, string key, BindingResolver r)
    {
        var b = fitted.Block;
        var values = new JsonObject();
        var sev = r.SeverityOf(b.Severity);
        var (sIcon, sWord) = SeverityLabel(sev);
        values["severity"] = sev == Severity.Normal ? string.Empty : sIcon + " " + sWord;
        var sevText = Text("${" + key + ".severity}", size: "Small", color: Color(sev), when: "${" + key + ".severity != ''}");
        JsonObject element;
        switch (b)
        {
            case MetricBlock m:
                values["label"] = m.Label;
                values["value"] = (r.Text(m.Value) + (m.Unit is { } unit ? " " + unit : string.Empty)).Trim();
                values["trend"] = m.Trend switch { Trend.Up => "↑", Trend.Down => "↓", _ => string.Empty };
                element = Column(
                    Text("${" + key + ".label}", subtle: true, size: "Small"),
                    Text("${" + key + ".value} ${" + key + ".trend}", size: "Large", weight: "Bolder", subtle: "${stale}"),
                    sevText);
                break;
            case BadgeBlock badge:
                values["text"] = r.Text(badge.Text);
                element = Column(Text("${" + key + ".text}", weight: "Bolder", subtle: "${stale}"), sevText);
                break;
            case AlertBlock alert:
                values["text"] = r.Text(alert.Text);
                values["icon"] = sev == Severity.Normal ? "ℹ" : sIcon;
                element = new JsonObject
                {
                    ["type"] = "Container",
                    ["style"] = sev == Severity.Critical ? "attention" : "warning",
                    ["items"] = new JsonArray((JsonNode)Text("${" + key + ".icon} ${" + key + ".text}"), sevText),
                };
                break;
            case CountdownBlock cd:
                values["label"] = cd.Label;
                values["text"] = Countdown(r.Resolve(cd.Until));
                element = Column(Text("${" + key + ".label}", subtle: true, size: "Small"), Text("${" + key + ".text}", size: "Large", weight: "Bolder"), sevText);
                break;
            case BarBlock bar:
                element = Meter(key, values, bar.Label, r.Number(bar.Value), r.Number(bar.Max) ?? 100, 0, bar.Unit, bar.Text is null ? null : r.Text(bar.Text), sevText);
                break;
            case GaugeBlock g:
                element = Meter(key, values, g.Label, r.Number(g.Value), g.Max, g.Min, g.Unit, null, sevText);
                break;
            case ProgressBlock p:
                var pct = r.Number(p.Pct) ?? (r.Number(p.Done) is { } d && r.Number(p.Total) is { } t && t > 0 ? 100 * d / t : null);
                var text = p.Text is null ? null : r.Text(p.Text);
                if (text is null && r.Number(p.Done) is { } done && r.Number(p.Total) is { } total)
                {
                    text = BindingResolver.Format(done) + " / " + BindingResolver.Format(total) + (p.Unit is { } pu ? " " + pu : string.Empty);
                }

                element = Meter(key, values, p.Label, pct, 100, 0, "%", text, sevText);
                break;
            case SparklineBlock sp:
                values["label"] = sp.Label ?? string.Empty;
                var points = sp.Points ?? [];
                values["spark"] = points.Count == 0 ? BindingResolver.Missing : Spark(points);
                values["last"] = points.Count == 0 ? string.Empty : BindingResolver.Format(JsonValue.Create(points[^1])) + (sp.Unit is { } su ? " " + su : string.Empty);
                element = Column(Text("${" + key + ".label}", subtle: true, size: "Small"), Text("${" + key + ".spark} ${" + key + ".last}", fontMonospace: true), sevText);
                break;
            case TableBlock table:
                element = Table(key, values, table, fitted.Rows, r);
                break;
            case ListBlock list:
                values["label"] = list.Label ?? string.Empty;
                var items = new JsonArray();
                foreach (var item in (r.Array(list.Items) ?? []).Take(fitted.Rows ?? int.MaxValue))
                {
                    items.Add((JsonNode)JsonValue.Create(BindingResolver.Format(item)));
                }

                values["items"] = items;
                element = Column(
                    Text("${" + key + ".label}", subtle: true, size: "Small", when: "${" + key + ".label != ''}"),
                    new JsonObject { ["type"] = "Container", ["$data"] = "${" + key + ".items}", ["items"] = new JsonArray((JsonNode)Text("• ${$data}", subtle: "${$root.stale}")) });
                break;
            case TreeBlock tree:
                values["label"] = (tree.Label ?? "Details") + ": open in Tasktor";
                element = Column(Text("${" + key + ".label}", subtle: true));
                break;
            default:
                return (null, values); // ActionsBlock: actions are rendered from the fitted action list
        }

        return (element, values);
    }

    private static JsonObject Column(params JsonObject[] items) => new() { ["type"] = "Container", ["items"] = new JsonArray([.. items.Select(i => (JsonNode)i)]) };

    private static JsonObject Text(string text, bool fontMonospace) => new() { ["type"] = "TextBlock", ["text"] = text, ["wrap"] = false, ["fontType"] = fontMonospace ? "Monospace" : "Default" };

    /// <summary>A bar without a chart element: two columns weighted by the value (1.5 has no progress control).</summary>
    private static JsonObject Meter(string key, JsonObject values, string label, double? value, double max, double min, string? unit, string? text, JsonObject sevText)
    {
        var span = max - min;
        var pct = value is { } v && span > 0 ? Math.Clamp((v - min) / span * 100, 0, 100) : 0;
        var filled = (int)Math.Round(pct);
        values["label"] = label;
        values["value"] = text ?? (value is { } vv ? BindingResolver.Format(JsonValue.Create(vv)) + (unit is { } u ? " " + u : string.Empty) : BindingResolver.Missing);
        values["fill"] = Math.Max(filled, 0);
        values["rest"] = Math.Max(100 - filled, 0);
        values["has_fill"] = filled > 0;
        values["has_rest"] = filled < 100;
        return new JsonObject
        {
            ["type"] = "Container",
            ["items"] = new JsonArray(
                (JsonNode)new JsonObject
                {
                    ["type"] = "ColumnSet",
                    ["columns"] = new JsonArray(
                        (JsonNode)new JsonObject { ["type"] = "Column", ["width"] = "stretch", ["items"] = new JsonArray((JsonNode)Text("${" + key + ".label}", subtle: true, size: "Small")) },
                        new JsonObject { ["type"] = "Column", ["width"] = "auto", ["items"] = new JsonArray((JsonNode)Text("${" + key + ".value}", weight: "Bolder")) }),
                },
                new JsonObject
                {
                    ["type"] = "ColumnSet",
                    ["minHeight"] = "6px",
                    ["columns"] = new JsonArray(
                        (JsonNode)new JsonObject { ["type"] = "Column", ["width"] = "${" + key + ".fill}", ["style"] = "accent", ["$when"] = "${" + key + ".has_fill}", ["items"] = new JsonArray() },
                        new JsonObject { ["type"] = "Column", ["width"] = "${" + key + ".rest}", ["style"] = "emphasis", ["$when"] = "${" + key + ".has_rest}", ["items"] = new JsonArray() }),
                },
                sevText),
        };
    }

    private static JsonObject Table(string key, JsonObject values, TableBlock table, int? rows, BindingResolver r)
    {
        values["label"] = table.Label ?? string.Empty;
        var outRows = new JsonArray();
        foreach (var row in (r.Array(table.Rows) ?? []).Take(rows ?? int.MaxValue))
        {
            var o = new JsonObject();
            foreach (var col in table.Columns)
            {
                var cell = row is JsonObject ro ? ro[col.Key] : null;
                o[col.Key] = cell is null ? BindingResolver.Missing : BindingResolver.Format(cell) + (col.Unit is { } u && cell is JsonValue cv && cv.GetValueKind() == System.Text.Json.JsonValueKind.Number ? " " + u : string.Empty);
            }

            if (table.RowKey is { } rk && row is JsonObject rko && rko[rk] is { } kv)
            {
                o["_key"] = BindingResolver.Format(kv);
            }

            outRows.Add((JsonNode)o);
        }

        values["rows"] = outRows;
        var headerCols = new JsonArray();
        var rowCols = new JsonArray();
        foreach (var (col, i) in table.Columns.Select((c, i) => (c, i)))
        {
            var width = i == 0 ? "stretch" : "auto";
            headerCols.Add((JsonNode)new JsonObject { ["type"] = "Column", ["width"] = width, ["items"] = new JsonArray((JsonNode)Text(col.Label, subtle: true, size: "Small", wrap: false)) });
            rowCols.Add((JsonNode)new JsonObject { ["type"] = "Column", ["width"] = width, ["items"] = new JsonArray((JsonNode)Text("${" + EscapeKey(col.Key) + "}", subtle: "${$root.stale}", wrap: false)) });
        }

        return new JsonObject
        {
            ["type"] = "Container",
            ["items"] = new JsonArray(
                (JsonNode)Text("${" + key + ".label}", subtle: true, size: "Small", when: "${" + key + ".label != ''}"),
                new JsonObject { ["type"] = "ColumnSet", ["columns"] = headerCols },
                new JsonObject { ["type"] = "ColumnSet", ["$data"] = "${" + key + ".rows}", ["columns"] = rowCols }),
        };
    }

    private static string EscapeKey(string k) => k.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_') ? k : "['" + k.Replace("'", "\\'", StringComparison.Ordinal) + "']";

    public static string Spark(IReadOnlyList<double> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var last = points.Count > 24 ? points.Skip(points.Count - 24).ToList() : [.. points];
        var lo = last.Min();
        var hi = last.Max();
        var sb = new StringBuilder(last.Count);
        foreach (var p in last)
        {
            var idx = hi > lo ? (int)Math.Round((p - lo) / (hi - lo) * (Sparks.Length - 1)) : 0;
            sb.Append(Sparks[idx]);
        }

        return sb.ToString();
    }

    /// <summary>"in 12 min", "in 2 h 5 min", "now", "5 min ago" for an ISO time (computed when drawn, never ticked).</summary>
    public string Countdown(JsonNode? until)
    {
        if (until is not JsonValue v || !v.TryGetValue<string>(out var s) || !DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
        {
            return BindingResolver.Missing;
        }

        var d = at - _now();
        var past = d < TimeSpan.Zero;
        var a = past ? -d : d;
        if (a < TimeSpan.FromMinutes(1))
        {
            return "now";
        }

        var text = a.TotalHours >= 24 ? (int)a.TotalDays + " d " + a.Hours + " h"
            : a.TotalHours >= 1 ? (int)a.TotalHours + " h " + a.Minutes + " min"
            : (int)a.TotalMinutes + " min";
        return past ? text + " ago" : "in " + text;
    }

    private JsonObject Action(Card card, CardAction a)
    {
        var id = a.Action ?? a.Id;
        var destructive = a.Destructive == true || _destructive(id);
        if (destructive)
        {
            // D-G5 hybrid: a destructive action deep-links to the flyout with the two-step confirm armed.
            var label = a.Label.EndsWith('…') ? a.Label : (a.Label.StartsWith("End", StringComparison.Ordinal) ? a.Label.TrimEnd('.') : a.Label) + "…";
            return new JsonObject
            {
                ["type"] = "Action.OpenUrl",
                ["title"] = label,
                ["url"] = "tasktor://open?card=" + Uri.EscapeDataString(card.Id) + "&action=" + Uri.EscapeDataString(id) + "&confirm=1",
            };
        }

        var dataObj = new JsonObject { ["card"] = card.Id, ["action"] = id };
        if (a.Target is { } t)
        {
            dataObj["target"] = new JsonObject([.. t.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value))]);
        }

        if (a.Args is { } args)
        {
            dataObj["args"] = new JsonObject([.. args.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value))]);
        }

        return new JsonObject { ["type"] = "Action.Execute", ["title"] = a.Label, ["verb"] = id, ["data"] = dataObj };
    }
}
