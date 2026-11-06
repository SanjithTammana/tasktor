using System.Globalization;
using System.Text.Json.Nodes;
using Tasktor.Hosts.Common.Render;

namespace Tasktor.Hosts.Common.Shelf;

/// <summary>One row of the strip: per-minute values 0–1, oldest first, ending at "now".</summary>
public sealed record TimelineRow(string Label, IReadOnlyList<double> Values, bool Lead);

/// <summary>Something that happened in the hour, at <see cref="At"/> (0 = an hour ago, 1 = now).</summary>
public sealed record TimelineEvent(double At, string Label);

/// <summary>The lead row carried past "now": one value per minute up to <see cref="TimelineStrip.Future"/>, and its label.</summary>
public sealed record TimelineForecast(IReadOnlyList<double> Values, string? Label);

/// <summary>Everything the shelf's timeline strip draws.</summary>
public sealed record TimelineModel(IReadOnlyList<TimelineRow> Rows, IReadOnlyList<TimelineEvent> Events, TimelineForecast? Forecast, string Start, string End);

/// <summary>
/// U4 amended D-G11: the shelf's timeline strip. The hour of CPU, RAM, GPU, disk and network from the agent's
/// <c>series</c> pull, one row each; the row highest right now leads (full ink) and is carried 15 minutes past "now" as
/// a dotted line, a linear trend over its last ten minutes, labelled when it reaches 100 % inside that window. Alerts
/// that started within the hour are written on the strip (the three most recent). Network is scaled to its own hour
/// peak (never below 1 Mbit/s, so an idle link stays low).
/// </summary>
public static class TimelineStrip
{
    public static readonly TimeSpan Past = TimeSpan.FromHours(1);
    public static readonly TimeSpan Future = TimeSpan.FromMinutes(15);
    public const int Minutes = 60;
    public const int MaxEvents = 3;
    public const int MaxLabel = 32;
    private const double NetFloorBps = 125_000; // 1 Mbit/s in bytes per second

    /// <summary>The series the strip pulls, in row order.</summary>
    public static IReadOnlyList<(string Id, string Label)> Series { get; } =
    [
        ("system.cpu_pct", "CPU"), ("system.ram_used_pct", "RAM"), ("system.gpu_pct", "GPU"),
        ("system.disk_active_pct", "Disk"), ("system.net_rx_bps", "Net"),
    ];

    public static TimelineModel Compose(IReadOnlyDictionary<string, JsonNode?> pulled, JsonArray? alerts, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(pulled);
        ArgumentNullException.ThrowIfNull(zone);
        var rows = new List<(string Label, List<double> Values)>();
        foreach (var (id, label) in Series)
        {
            if (!pulled.TryGetValue(id, out var node) || node?["hour"] is not JsonArray hour || hour.Count == 0)
            {
                continue;
            }

            var raw = hour.TakeLast(Minutes).Select(v => BindingResolver.AsDouble(v) ?? 0).ToList();
            var values = id.EndsWith("_bps", StringComparison.Ordinal)
                ? raw.Select(v => v / Math.Max(NetFloorBps, raw.Max())).ToList()
                : raw.Select(v => v / 100.0).ToList();
            rows.Add((label, values.Select(v => Math.Clamp(double.IsNaN(v) ? 0 : v, 0, 1)).ToList()));
        }

        var lead = rows.Count == 0 ? -1 : rows.Select((r, i) => (r, i)).MaxBy(x => x.r.Values[^1]).i;
        var model = rows.Select((r, i) => new TimelineRow(r.Label, r.Values, i == lead)).ToList();
        var forecast = lead < 0 ? null : Forecast(model[lead], now, zone);
        return new TimelineModel(model, Events(alerts, now), forecast, Clock(now - Past, zone), Clock(now + Future, zone));
    }

    /// <summary>Where value <paramref name="index"/> of <paramref name="count"/> sits on the past span (0–1): the newest at 1.</summary>
    public static double At(int index, int count) => (index + (Minutes - Math.Min(count, Minutes))) / (double)(Minutes - 1);

    private static TimelineForecast Forecast(TimelineRow row, DateTimeOffset now, TimeZoneInfo zone)
    {
        var tail = row.Values.TakeLast(10).ToList();
        var slope = Slope(tail);
        var last = row.Values[^1];
        var minutes = (int)Future.TotalMinutes;
        var values = Enumerable.Range(1, minutes).Select(k => Math.Clamp(last + (slope * k), 0, 1)).ToList();
        string? label = null;
        if (slope > 0 && last < 1)
        {
            var toFull = (1 - last) / slope;
            if (toFull <= minutes)
            {
                var at = Clock(now + TimeSpan.FromMinutes(Math.Round(toFull)), zone); // an ETA reads to the nearest minute
                label = row.Label == "RAM" ? "full ≈" + at : "100% ≈" + at;
            }
        }

        return new TimelineForecast(values, label);
    }

    private static List<TimelineEvent> Events(JsonArray? alerts, DateTimeOffset now)
    {
        var from = now - Past;
        return (alerts ?? [])
            .OfType<JsonObject>()
            .Select(a => (Since: a["since"] is JsonValue v && v.TryGetValue<string>(out var s) && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : (DateTimeOffset?)null,
                Text: Text(a)))
            .Where(a => a.Since is { } t && t >= from && t <= now && a.Text is not null)
            .OrderByDescending(a => a.Since)
            .Take(MaxEvents)
            .OrderBy(a => a.Since)
            .Select(a => new TimelineEvent((a.Since!.Value - from) / Past, a.Text!))
            .ToList();
    }

    private static string? Text(JsonObject alert)
    {
        var text = (alert["text"] ?? alert["rule"]) is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null;
        return text is not { Length: > 0 } ? null : text.Length <= MaxLabel ? text : text[..(MaxLabel - 1)] + "…";
    }

    private static double Slope(IReadOnlyList<double> ys)
    {
        var n = ys.Count;
        if (n < 2)
        {
            return 0;
        }

        var mx = (n - 1) / 2.0;
        var my = ys.Average();
        double num = 0, den = 0;
        for (var i = 0; i < n; i++)
        {
            num += (i - mx) * (ys[i] - my);
            den += (i - mx) * (i - mx);
        }

        return num / den;
    }

    private static string Clock(DateTimeOffset at, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(at, zone).ToString("H:mm", CultureInfo.InvariantCulture);
}
