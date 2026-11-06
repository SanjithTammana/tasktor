using System.Globalization;
using System.Text.Json.Nodes;
using Tasktor.Contracts.Cards;
using Tasktor.Hosts.Common.Render;

namespace Tasktor.Hosts.Common.Deep;

/// <summary>One process under an app: its row text, why it is locked (if it is), and what can be done to it.</summary>
public sealed record ProcessNode(uint Pid, string Exe, string Cpu, string Memory, string? Locked, IReadOnlyList<CardAction> Actions);

/// <summary>One app group: its totals, whether it is a system group, its processes, and app-wide actions.</summary>
public sealed record AppNode(string AppId, string Name, string Cpu, string Memory, int Count, bool System, IReadOnlyList<ProcessNode> Processes, IReadOnlyList<CardAction> Actions);

/// <summary>
/// U4 D-G3 System → apps table with groups → processes (A2/A3, the card's <c>Tree</c> block): the agent's
/// <c>processes</c> pull as a tree, busiest first. App actions target the group (<c>app</c>); process actions target one
/// process by PID and creation time, so a reused PID is refused by the guard. A locked process (elevated, protected,
/// Tasktor itself) offers no actions and says why (A3: never fail on click; say why first). System groups offer none.
/// </summary>
public static class AppsTree
{
    public static IReadOnlyList<AppNode> From(JsonNode? pulled)
    {
        if (pulled is not JsonArray groups)
        {
            return [];
        }

        var apps = new List<AppNode>();
        foreach (var g in groups.OfType<JsonObject>())
        {
            if (Str(g, "app_id") is not { } id)
            {
                continue;
            }

            var name = Str(g, "name") ?? id;
            var system = g["system"] is JsonValue sv && sv.TryGetValue<bool>(out var s) && s;
            var procs = (g["processes"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(p => Process(p, system))
                .OfType<ProcessNode>()
                .ToList();
            var app = new Dictionary<string, string> { ["app"] = id };
            IReadOnlyList<CardAction> actions = system
                ? []
                :
                [
                    new CardAction { Id = "lower", Label = $"Lower {name}'s priority", Action = "priority.set", Target = app, Args = new Dictionary<string, string> { ["priority"] = "below_normal" } },
                    new CardAction { Id = "eco", Label = $"Efficiency mode for {name}", Action = "efficiency.set", Target = app },
                    new CardAction { Id = "end", Label = $"End {name}…", Action = "app.end", Target = app, Destructive = true },
                ];
            apps.Add(new AppNode(id, name, Pct(g), Mb(g), (int)(BindingResolver.AsDouble(g["count"]) ?? procs.Count), system, procs, actions));
        }

        return apps;
    }

    private static ProcessNode? Process(JsonObject p, bool system)
    {
        if (BindingResolver.AsDouble(p["pid"]) is not { } pidValue || Str(p, "exe") is not { } exe)
        {
            return null;
        }

        var pid = (uint)pidValue;
        var locked = Str(p, "locked") ?? (system ? "Part of Windows" : null);
        var target = new Dictionary<string, string> { ["pid"] = pid.ToString(CultureInfo.InvariantCulture) };
        if (Str(p, "created") is { } created)
        {
            target["created"] = created;
        }

        IReadOnlyList<CardAction> actions = locked is not null
            ? []
            :
            [
                new CardAction { Id = "lower", Label = "Lower priority", Action = "priority.set", Target = target, Args = new Dictionary<string, string> { ["priority"] = "below_normal" } },
                new CardAction { Id = "end", Label = $"End {exe}…", Action = "proc.end", Target = target, Destructive = true },
            ];
        return new ProcessNode(pid, exe, Pct(p), Mb(p), locked, actions);
    }

    private static string Pct(JsonObject o) => BindingResolver.AsDouble(o["cpu_pct"]) is { } v ? BindingResolver.Format(JsonValue.Create(v)) + "%" : BindingResolver.Missing;

    private static string Mb(JsonObject o) => BindingResolver.AsDouble(o["private_mb"]) is { } v ? DataPage.Bytes((long)(v * 1024 * 1024)) : BindingResolver.Missing;

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
}
