using System.Globalization;
using System.Text.Json.Nodes;
using Tasktor.Contracts.Cards;
using Tasktor.Hosts.Common.Render;

namespace Tasktor.Hosts.Common.Deep;

/// <summary>
/// U4 D-G3 Focus page (E11.1, E11.2): the running block from <c>state.focus</c> with Pause / Resume, Skip, +5 min and Stop
/// (Next in place of Skip once a block is done), one section per preset to start from, the last blocks and the preset
/// comparison, both from the agent's <c>focus</c> pull. The notes editor (E11.3) is not here: note text never crosses the
/// pipe, so notes stay in their folder and on the card's pinned lines.
/// </summary>
public static class FocusPage
{
    public const string NoBlock = "No block running.";

    public static IReadOnlyList<CardRows> Sections(JsonNode? focus, JsonNode? pulled, DateTimeOffset now)
    {
        var running = focus is JsonObject f && Str(f, "preset") is not null && Str(f, "block") is not null;
        var sections = new List<CardRows> { Current(running ? (JsonObject)focus! : null, now) };
        if (pulled is not JsonObject root)
        {
            return sections;
        }

        foreach (var p in (root["presets"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (Str(p, "name") is not { } name)
            {
                continue;
            }

            sections.Add(new CardRows("focus.preset." + name, name, Severity.Normal, null, null,
                [new CardRow(RowKind.Value, "Blocks", Str(p, "summary") ?? BindingResolver.Missing)],
                [new CardAction { Id = "start", Label = running ? "Switch to this" : "Start", Action = "focus.start", Args = new Dictionary<string, string> { ["preset"] = name } }],
                []));
        }

        sections.Add(Recent(root["blocks"] as JsonArray));
        if (Str(root, "review") is { } review)
        {
            sections.Add(new CardRows("focus.review", "How your blocks went", Severity.Normal, null, null, [new CardRow(RowKind.Text, review)], [], []));
        }

        return sections;
    }

    private static CardRows Current(JsonObject? focus, DateTimeOffset now)
    {
        if (focus is null)
        {
            return new CardRows("focus.now", "Focus timer", Severity.Normal, null, null, [new CardRow(RowKind.Value, "Block", NoBlock)], [], []);
        }

        var kind = Str(focus, "block");
        var paused = focus["paused"] is JsonValue pv && pv.TryGetValue<bool>(out var pb) && pb;
        var rows = new List<CardRow> { new(RowKind.Value, "Preset", Str(focus, "preset")) };
        List<CardAction> actions;
        if (kind == "done")
        {
            rows.Add(new CardRow(RowKind.Value, "Block", "Done, waiting for you"));
            actions = [Act("next", "Next", "focus.next"), Act("extend", "+5 min", "focus.extend"), Act("stop", "Stop", "focus.stop")];
        }
        else
        {
            rows.Add(new CardRow(RowKind.Value, "Block", Word(kind) + (paused ? " (paused)" : string.Empty)));
            if (!paused)
            {
                rows.Add(new CardRow(RowKind.Value, "Time left", Left(Str(focus, "until"), now)));
            }

            actions = [Act("pause", paused ? "Resume" : "Pause", "focus.pause"), Act("skip", "Skip", "focus.skip"), Act("extend", "+5 min", "focus.extend"), Act("stop", "Stop", "focus.stop")];
        }

        return new CardRows("focus.now", "Focus timer", Severity.Normal, null, null, rows, actions, []);
    }

    private static CardRows Recent(JsonArray? blocks)
    {
        var list = (blocks ?? []).OfType<JsonObject>().ToList();
        if (list.Count == 0)
        {
            return new CardRows("focus.recent", "Recent blocks", Severity.Normal, null, null,
                [new CardRow(RowKind.Text, "No focus blocks yet. Finished blocks show up here.")], [], []);
        }

        var rows = new List<CardRow> { new(RowKind.TableHeader, string.Empty, Cells: ["When", "Preset", "Minutes", "Ended", "On course"]) };
        foreach (var b in list)
        {
            var when = Str(b, "at") is { } at && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
                ? t.ToLocalTime().ToString("d MMM H:mm", CultureInfo.InvariantCulture)
                : BindingResolver.Missing;
            var minutes = BindingResolver.AsDouble(b["minutes"]) is { } m ? ((long)m).ToString(CultureInfo.InvariantCulture) : BindingResolver.Missing;
            var onCourse = b["excluded"] is JsonValue ev && ev.TryGetValue<bool>(out var excluded) && excluded ? "left out"
                : BindingResolver.AsDouble(b["on_intent_share"]) is { } share ? Math.Round(share * 100).ToString("0", CultureInfo.InvariantCulture) + "%"
                : "–";
            rows.Add(new CardRow(RowKind.TableRow, when, Cells: [when, Str(b, "preset") ?? BindingResolver.Missing, minutes, Ended(Str(b, "end")), onCourse]));
        }

        return new CardRows("focus.recent", "Recent blocks", Severity.Normal, null, null, rows, [], []);
    }

    private static CardAction Act(string id, string label, string action) => new() { Id = id, Label = label, Action = action };

    private static string Word(string? kind) => kind switch { "focus" => "Focus", "long_break" => "Long break", "break" => "Break", _ => BindingResolver.Missing };

    private static string Ended(string? end) => end switch { "completed" => "Completed", "skipped" => "Skipped", "stopped" => "Stopped", _ => BindingResolver.Missing };

    private static string Left(string? until, DateTimeOffset now)
    {
        if (until is null || !DateTimeOffset.TryParse(until, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
        {
            return BindingResolver.Missing;
        }

        var left = at - now;
        return left <= TimeSpan.Zero ? "now" : Math.Ceiling(left.TotalMinutes).ToString("0", CultureInfo.InvariantCulture) + " min left";
    }

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
}
