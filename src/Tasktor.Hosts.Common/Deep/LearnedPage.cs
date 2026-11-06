using System.Globalization;
using System.Text.Json.Nodes;
using Tasktor.Contracts.Cards;
using Tasktor.Hosts.Common.Render;

namespace Tasktor.Hosts.Common.Deep;

/// <summary>
/// U4 D-G3 "What Tasktor learned", Jobs tab (B6): the agent's <c>learned</c> pull as the deep window's rows. A section
/// for learning itself (on / off), one per job kind (runs, usual duration, when a quiet job is flagged and whether that is
/// learned or the default, false alarms) with Relearn and a two-step Forget, and the latest guardrail actions.
/// </summary>
public static class LearnedPage
{
    public const int GuardrailRows = 10;

    public static IReadOnlyList<CardRows> Sections(JsonNode? learned)
    {
        if (learned is not JsonObject root)
        {
            return [];
        }

        var on = root["learning"] is JsonValue lv && lv.TryGetValue<bool>(out var b) && b;
        var sections = new List<CardRows>
        {
            new(
                "learned.learning",
                "Learning from your jobs",
                Severity.Normal,
                null,
                null,
                [new CardRow(RowKind.Value, "Learning", on ? "On" : "Off")],
                [new CardAction { Id = "learning", Label = on ? "Stop learning" : "Learn from my jobs", Action = "learning.set", Args = new Dictionary<string, string> { ["on"] = on ? "false" : "true" } }],
                []),
        };

        var kinds = (root["kinds"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var k in kinds)
        {
            if (Str(k, "kind") is not { } id)
            {
                continue;
            }

            var name = Str(k, "name") ?? "Job kind";
            names[id] = name;
            var rows = new List<CardRow>
            {
                new(RowKind.Value, "Runs", Num(k, "runs") + " (" + Num(k, "good_runs") + " finished well)"),
            };
            if (Dbl(k, "usual_duration_s") is { } usual)
            {
                rows.Add(new CardRow(RowKind.Value, "Usually takes", Duration(usual)));
            }

            if (Dbl(k, "silent_threshold_s") is { } silent)
            {
                rows.Add(new CardRow(RowKind.Value, "Flagged when quiet for", Duration(silent) + (Str(k, "source") == "learned" ? " (learned)" : " (default)")));
            }

            if (Num(k, "false_alarms") is { } fa && fa != "0")
            {
                rows.Add(new CardRow(RowKind.Value, "False alarms", fa));
            }

            var target = new Dictionary<string, string> { ["kind"] = id };
            sections.Add(new CardRows("learned.kind." + id, name, Severity.Normal, null, null, rows,
            [
                new CardAction { Id = "relearn", Label = "Relearn", Action = "learned.relearn", Target = target },
                new CardAction { Id = "forget", Label = "Forget…", Action = "learned.forget", Target = target, Destructive = true },
            ], []));
        }

        if (kinds.Count == 0)
        {
            sections.Add(new CardRows("learned.none", "Job kinds", Severity.Normal, null, null,
                [new CardRow(RowKind.Text, "Nothing learned yet. Tasktor learns from the jobs it watches finish.")], [], []));
        }

        var log = (root["guardrails"] as JsonArray ?? []).OfType<JsonObject>().TakeLast(GuardrailRows).Reverse().ToList();
        if (log.Count > 0)
        {
            var rows = new List<CardRow> { new(RowKind.TableHeader, string.Empty, Cells: ["When", "Kind", "What", "Why"]) };
            foreach (var g in log)
            {
                var when = Str(g, "at") is { } at && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
                    ? t.ToLocalTime().ToString("d MMM H:mm", CultureInfo.InvariantCulture)
                    : BindingResolver.Missing;
                var kind = Str(g, "kind") is { } kid && names.TryGetValue(kid, out var n) ? n : "a kind";
                rows.Add(new CardRow(RowKind.TableRow, when, Cells: [when, kind, Str(g, "action") ?? BindingResolver.Missing, Str(g, "reason") ?? string.Empty]));
            }

            sections.Add(new CardRows("learned.guardrails", "Guardrails", Severity.Normal, null, null, rows, [], []));
        }

        return sections;
    }

    /// <summary>"45 s", "18 min", "1 h 5 min".</summary>
    public static string Duration(double seconds)
    {
        var s = Math.Max(0, Math.Round(seconds));
        if (s < 60)
        {
            return s.ToString(CultureInfo.InvariantCulture) + " s";
        }

        var min = (long)Math.Round(s / 60);
        return min < 60 ? min.ToString(CultureInfo.InvariantCulture) + " min"
            : (min / 60).ToString(CultureInfo.InvariantCulture) + " h" + (min % 60 == 0 ? string.Empty : " " + (min % 60).ToString(CultureInfo.InvariantCulture) + " min");
    }

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    private static double? Dbl(JsonObject o, string key) => BindingResolver.AsDouble(o[key]);

    private static string? Num(JsonObject o, string key) => Dbl(o, key) is { } d ? ((long)d).ToString(CultureInfo.InvariantCulture) : null;
}
