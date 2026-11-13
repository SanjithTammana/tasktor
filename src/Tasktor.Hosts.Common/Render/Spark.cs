using System.Text.Json.Nodes;

namespace Tasktor.Hosts.Common.Render;

/// <summary>A sparkline ready to draw: values 0–1 oldest first, the latest value and the peak as text.</summary>
public sealed record Spark(IReadOnlyList<double> Values, string Last, string Peak);

/// <summary>
/// U4 D-G1: a <see cref="Contracts.Cards.SparklineBlock"/> as a line. A plugin's inline <c>points</c> win; otherwise the
/// agent's <c>series</c> pull (its <c>hour</c>, one point a minute). Percentages keep a fixed 0–100 scale so two charts
/// compare; anything else scales to its own peak.
/// </summary>
public static class Sparks
{
    public static Spark? From(IReadOnlyList<double>? points, JsonNode? pulled, string? unit)
    {
        var raw = points is { Count: > 0 }
            ? points.ToList()
            // Only an object has "hour": indexing any other shape throws, and on the UI thread that ends the viewer.
            : pulled is JsonObject series && series["hour"] is JsonArray hour ? hour.Select(v => BindingResolver.AsDouble(v) ?? 0).ToList() : [];
        raw = [.. raw.Select(v => double.IsNaN(v) || double.IsInfinity(v) ? 0 : v)];
        if (raw.Count == 0)
        {
            return null;
        }

        var peak = raw.Max();
        var scale = unit == "%" ? 100 : Math.Max(peak, 1e-9);
        string Text(double v) => BindingResolver.Format(JsonValue.Create(v)) + (string.IsNullOrEmpty(unit) ? string.Empty : unit == "%" ? "%" : " " + unit);
        return new Spark([.. raw.Select(v => Math.Clamp(v / scale, 0, 1))], Text(raw[^1]), "peak " + Text(peak));
    }
}
