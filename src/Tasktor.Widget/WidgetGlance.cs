using System.Globalization;
using System.Text.Json.Nodes;
using Tasktor.Hosts.Common.Render;

namespace Tasktor.Widget;

/// <summary>Builds a privacy-safe widget glance from aggregate snapshot values only.</summary>
public static class WidgetGlance
{
    public static AdaptiveCardOutput Render(bool connected, JsonNode? snapshot, JsonNode? cpuSeries, string? definition = "TasktorSystem")
    {
        // Never copy arbitrary snapshot text into the card. The Widget host can display cached
        // data while locked, and neither app names nor job names belong in that cache.
        var cpu = Number(snapshot?["state"]?["system"]?["cpu_pct"]);
        var ram = Number(snapshot?["state"]?["system"]?["ram_used_pct"]);
        var sentence = !connected ? "Tasktor isn't running." : definition switch
        {
            "TasktorSystem" => cpu is null && ram is null ? "System readings are unavailable." :
                string.Join(" ", new[] { Format("CPU", cpu), Format("Memory", ram) }.Where(x => x is not null)),
            "TasktorJobs" => Jobs(snapshot),
            "TasktorWorkflow" => Workflow(snapshot),
            _ => "Widget data is unavailable.",
        };
        var load = connected && definition == "TasktorSystem" ? EdgeGrain.LoadFromSeries(cpuSeries) : [];
        var data = new JsonObject
        {
            ["sentence"] = sentence,
            ["strip"] = load.Count > 0 ? AdaptiveCardRenderer.Spark(load) : string.Empty,
            ["hasStrip"] = load.Count > 0,
        };
        var template = new JsonObject
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["type"] = "AdaptiveCard",
            ["version"] = "1.5",
            ["body"] = new JsonArray
            {
                new JsonObject { ["type"] = "TextBlock", ["text"] = "${sentence}", ["wrap"] = true },
                new JsonObject { ["type"] = "TextBlock", ["text"] = "${strip}", ["$when"] = "${hasStrip}", ["isSubtle"] = true, ["wrap"] = false },
            },
        };
        return new AdaptiveCardOutput(template, data);
    }

    private static double? Number(JsonNode? node)
    {
        var value = BindingResolver.AsDouble(node);
        return value is >= 0 and <= 100 && double.IsFinite(value.Value) ? value : null;
    }

    private static string? Format(string label, double? value) => value is null ? null :
        label + " " + value.Value.ToString("0.#", CultureInfo.InvariantCulture) + "%.";

    private static string Jobs(JsonNode? snapshot)
    {
        if (snapshot?["state"]?["jobs"] is not JsonArray jobs) return "Job readings are unavailable.";
        var live = 0;
        var silent = 0;
        foreach (var row in jobs)
        {
            var status = row?["status"] is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;
            if (status is not ("starting" or "working" or "working_gpu" or "quiet" or "silent" or "stuck_busy" or "paused" or "done" or "failed" or "ended"))
                return "Job readings are unavailable.";
            if (status is "starting" or "working" or "working_gpu" or "quiet" or "silent" or "stuck_busy" or "paused") live++;
            if (status == "silent") silent++;
        }
        return $"{live} active {(live == 1 ? "job" : "jobs")}." + (silent > 0 ? $" {silent} silent." : string.Empty);
    }

    private static string Workflow(JsonNode? snapshot)
    {
        if (snapshot?["state"]?["workflow"]?["switches_today"] is not JsonValue node ||
            !node.TryGetValue<long>(out var value) || value < 0)
            return "Workflow readings are unavailable.";
        return value.ToString(CultureInfo.InvariantCulture) + " workflow switches today.";
    }
}
