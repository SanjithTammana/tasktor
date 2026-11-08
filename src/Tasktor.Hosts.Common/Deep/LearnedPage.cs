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

    /// <summary>
    /// The Workflows tab (C7, C9, C10): one row per learned workflow with this week's time and the user's name, and a
    /// summary line (switches today, the model). Merged ones say where they went and offer nothing more.
    /// </summary>
    public static (string Summary, IReadOnlyList<WorkflowRow> Rows) Workflows(Contracts.Workflow.WorkflowsReport? report)
    {
        if (report?.Model is not { } model)
        {
            return ("Nothing learned yet: activities are shown by app until the first refit.", []);
        }

        var byId = report.Workflows.ToDictionary(w => w.Id, StringComparer.Ordinal);
        var live = report.Workflows.Where(w => w.MergedInto is null).ToList();
        var rows = report.Workflows.Select(w =>
        {
            string note;
            if (w.MergedInto is { } into)
            {
                note = "Merged into " + (byId.TryGetValue(into, out var target) ? target.Name : into);
            }
            else
            {
                note = Minutes(w.MinutesWeek) + " this week"
                    + (w.Status == "unstable" ? " · still settling: not shown yet" : w.Status == "hidden" ? " · hidden after “That's wrong”" : string.Empty)
                    + (w.Renamed ? " · named by you (was “" + w.AutoName + "”)" : string.Empty);
            }

            var targets = w.MergedInto is null ? live.Where(t => t.Id != w.Id).Select(t => (t.Id, t.Name)).ToList() : [];
            return new WorkflowRow(w.Id, w.Name, note, w.MergedInto is null, targets.Count > 0, targets);
        }).ToList();
        return (report.SwitchesToday.ToString(CultureInfo.InvariantCulture) + " switches today · model " + model, rows);
    }

    public static CardAction Rename(WorkflowRow row, string name)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new CardAction
        {
            Id = "rename",
            Label = "Rename",
            Action = "workflow.rename",
            Target = new Dictionary<string, string>(StringComparer.Ordinal) { ["workflow"] = row.Id },
            Args = new Dictionary<string, string>(StringComparer.Ordinal) { ["name"] = (name ?? string.Empty).Trim() },
        };
    }

    public static CardAction Merge(WorkflowRow row, string intoId)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new CardAction
        {
            Id = "merge",
            Label = "Merge",
            Action = "workflow.merge",
            Target = new Dictionary<string, string>(StringComparer.Ordinal) { ["workflow"] = row.Id },
            Args = new Dictionary<string, string>(StringComparer.Ordinal) { ["into"] = intoId },
        };
    }

    private static string Minutes(double minutes)
    {
        var m = (int)Math.Round(minutes);
        return m < 60 ? m.ToString(CultureInfo.InvariantCulture) + " min"
            : (m / 60).ToString(CultureInfo.InvariantCulture) + " h" + (m % 60 == 0 ? string.Empty : " " + (m % 60).ToString(CultureInfo.InvariantCulture) + " min");
    }
}

/// <summary>A learned workflow on the Workflows tab, with the workflows it may be merged into.</summary>
public sealed record WorkflowRow(string Id, string Title, string Note, bool CanRename, bool CanMerge, IReadOnlyList<(string Id, string Name)> MergeTargets);

