using System.Globalization;
using System.Text.Json.Nodes;
using Tasktor.Contracts.Cards;
using Tasktor.Hosts.Common.Render;

namespace Tasktor.Hosts.Common.Deep;

/// <summary>
/// U4 D-G3 Home: "Undo for actions in the last 30 min" from the snapshot's <c>recent_actions</c>. Each undoable entry is
/// a row with how long ago it happened, who did it when it was a rule, and an Undo action (the catalog's reversing
/// action on the same target). Entries already undone are not offered again.
/// </summary>
public static class HomePage
{
    public static CardRows? Undo(JsonNode? recentActions, DateTimeOffset now)
    {
        if (recentActions is not JsonArray list)
        {
            return null;
        }

        var rows = new List<CardRow>();
        var actions = new List<CardAction>();
        foreach (var item in list.OfType<JsonObject>())
        {
            if (Str(item, "undo") is not { } undo || Str(item, "text") is not { } text || Str(item, "at") is not { } atText
                || !DateTimeOffset.TryParse(atText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
            {
                continue;
            }

            var who = Str(item, "origin") == "rule" ? "a rule, " : string.Empty;
            var key = actions.Count.ToString(CultureInfo.InvariantCulture);
            rows.Add(new CardRow(RowKind.TableRow, text, Cells: [text, who + Ago(now - at)], RowKey: key));
            var target = item["target"] is JsonObject t
                ? t.Where(kv => kv.Value is JsonValue).ToDictionary(kv => kv.Key, kv => kv.Value!.ToString(), StringComparer.Ordinal)
                : null;
            actions.Add(new CardAction { Id = "undo-" + key, Label = "Undo " + text, Action = undo, Target = target });
        }

        return rows.Count == 0 ? null : new CardRows("home.undo", "Undo", Severity.Normal, null, null, rows, actions, []);
    }

    public static string Ago(TimeSpan span) =>
        span < TimeSpan.FromMinutes(1) ? "just now"
        : span < TimeSpan.FromHours(1) ? ((int)span.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min ago"
        : ((int)span.TotalHours).ToString(CultureInfo.InvariantCulture) + " h ago";

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
}
