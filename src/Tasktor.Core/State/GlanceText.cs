using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tasktor.Contracts.Cards;

namespace Tasktor.Core.State;

/// <summary>
/// The one-line glance (U4 D-G1 / D-G4): the top-severity card (else <c>core.system</c>), its priority-1 metrics with the
/// severity word, plus the top job's progress. ≤ 127 characters (the tray tooltip limit). The agent carries no renderer;
/// this is the only text it composes.
/// </summary>
public static class GlanceText
{
    public const int MaxLength = 127;

    public static string Compose(IReadOnlyList<Card> cards, JsonObject state)
    {
        ArgumentNullException.ThrowIfNull(cards);
        var root = new JsonObject { ["state"] = state.DeepClone() };
        Card? pick = null;
        var pickSev = -1;
        foreach (var c in cards)
        {
            var sev = SeverityRank(Resolve(c.Severity, root));
            if (sev > pickSev || (sev == pickSev && pick?.Id != "core.system" && c.Id == "core.system"))
            {
                pick = c;
                pickSev = sev;
            }
        }

        if (pick is null)
        {
            return "Tasktor";
        }

        var parts = new List<string>();
        var metrics = pick.Blocks.OfType<MetricBlock>().ToList();
        if (metrics.Count > 0)
        {
            var top = metrics.Min(m => m.Priority);
            foreach (var m in metrics.Where(m => m.Priority == top))
            {
                parts.Add(m.Label + " " + Format(Resolve(m.Value, root)) + UnitText(m.Unit));
            }
        }
        else
        {
            parts.Add(pick.Title);
        }

        if (pick.Id != "core.jobs" && state["jobs"] is JsonArray jobs)
        {
            foreach (var j in jobs)
            {
                if (j?["pct"] is JsonValue pct && j["label"]?.GetValue<string>() is { } label)
                {
                    parts.Add(label + " " + Format(pct) + "%");
                    break;
                }
            }
        }

        var text = string.Join(" · ", parts);
        var word = pickSev switch { 2 => "Critical: ", 1 => "Warning: ", _ => string.Empty };
        return Truncate(word + text);
    }

    public static string Truncate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length <= MaxLength ? text : text[..(MaxLength - 1)] + "…";
    }

    internal static JsonNode? Resolve(Value? value, JsonNode root)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Bind is { } ptr)
        {
            return JsonPointer.TryResolve(root, ptr, out var v) ? v : null;
        }

        return value.Raw is null ? null : JsonNode.Parse(value.Raw);
    }

    internal static string Format(JsonNode? node)
    {
        if (node is JsonValue v)
        {
            if (v.GetValueKind() == JsonValueKind.Number)
            {
                var d = v.GetValue<double>();
                return Math.Abs(d) >= 10 ? Math.Round(d).ToString("0", CultureInfo.InvariantCulture) : d.ToString("0.#", CultureInfo.InvariantCulture);
            }

            if (v.GetValueKind() == JsonValueKind.String)
            {
                return v.GetValue<string>();
            }

            return v.ToJsonString();
        }

        return node is null ? "—" : node.ToJsonString();
    }

    private static string UnitText(string? unit) => string.IsNullOrEmpty(unit) ? string.Empty : unit == "%" ? "%" : " " + unit;

    private static int SeverityRank(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String
            ? v.GetValue<string>() switch { "critical" => 2, "warn" => 1, _ => 0 }
            : 0;

}
