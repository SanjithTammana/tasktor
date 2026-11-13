using System.Globalization;
using System.Text.Json.Nodes;
using Tasktor.Contracts.Cards;

namespace Tasktor.Hosts.Common.Deep;

/// <summary>One process the person may watch as a job, and the <c>job.pin</c> that does it.</summary>
public sealed record JobChoice(uint Pid, string Exe, string App, string Detail, CardAction Watch);

/// <summary>
/// B1 Jobs → "Watch a program as a job / Choose…": the agent's <c>processes</c> pull as a pick list. It reuses
/// <see cref="AppsTree"/> so the same processes are offered as on System: Windows' own and locked ones (elevated,
/// protected, Tasktor itself) are left out, busiest app first. Each choice pins by PID and creation time.
/// </summary>
public static class JobPicker
{
    public static IReadOnlyList<JobChoice> From(JsonNode? pulled) =>
        AppsTree.From(pulled)
            .Where(app => !app.System)
            .SelectMany(app => app.Processes.Where(p => p.Locked is null).Select(p => Choice(app, p)))
            .ToList();

    public static IReadOnlyList<JobChoice> Filter(IReadOnlyList<JobChoice> choices, string? text)
    {
        ArgumentNullException.ThrowIfNull(choices);
        var q = text?.Trim();
        return string.IsNullOrEmpty(q)
            ? choices
            : choices.Where(c => c.Exe.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.App.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.Pid.ToString(CultureInfo.InvariantCulture).Contains(q, StringComparison.Ordinal)).ToList();
    }

    private static JobChoice Choice(AppNode app, ProcessNode p)
    {
        var target = new Dictionary<string, string> { ["pid"] = p.Pid.ToString(CultureInfo.InvariantCulture) };
        if (p.Actions.FirstOrDefault()?.Target?.GetValueOrDefault("created") is { } created)
        {
            target["created"] = created;
        }

        var watch = new CardAction { Id = "watch", Label = $"Watch {p.Exe} as a job", Action = "job.pin", Target = target };
        return new JobChoice(p.Pid, p.Exe, app.Name, $"{app.Name} · {p.Cpu} CPU · {p.Memory}", watch);
    }
}
