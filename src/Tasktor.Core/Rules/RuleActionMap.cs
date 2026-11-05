using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Rules;

namespace Tasktor.Core.Rules;

/// <summary>Maps a rule action (D-H6 grammar) onto an <see cref="ActionRequest"/> for the router, with origin <c>rule</c>.</summary>
public static class RuleActionMap
{
    /// <summary>The kinds present on one action object (the loader requires exactly one, plus its modifiers).</summary>
    public static List<string> Kinds(RuleAction a)
    {
        ArgumentNullException.ThrowIfNull(a);
        var k = new List<string>();
        if (a.Toast is not null) k.Add("toast");
        if (a.Badge is not null) k.Add("badge");
        if (a.Card is not null) k.Add("card");
        if (a.Pause is not null) k.Add("pause");
        if (a.Resume is not null) k.Add("resume");
        if (a.SetPriority is not null) k.Add("set_priority");
        if (a.Efficiency is not null) k.Add("efficiency");
        if (a.CapCores is not null) k.Add("cap_cores");
        if (a.PluginAction is not null) k.Add("plugin_action");
        if (a.Arrange is not null) k.Add("arrange");
        if (a.CapturePause is not null) k.Add("capture_pause");
        if (a.CaptureExclude is not null) k.Add("capture_exclude");
        if (a.UseFallback is not null) k.Add("use_fallback");
        if (a.Relearn is not null) k.Add("relearn");
        if (a.Snooze is not null) k.Add("snooze");
        return k;
    }

    /// <summary>The catalog action id an action object becomes (null for toasts, which the engine shows itself).</summary>
    public static string? ActionId(RuleAction a)
    {
        ArgumentNullException.ThrowIfNull(a);
        if (a.Pause is { } p)
        {
            return PauseId(p, pause: true);
        }

        if (a.Resume is { } r)
        {
            return PauseId(r, pause: false);
        }

        return a switch
        {
            { Toast: not null } => null,
            { Badge: not null } => "badge.set",
            { Card: not null } => "card.pin",
            { SetPriority: not null } => "priority.set",
            { Efficiency: not null } => "efficiency.set",
            { CapCores: not null } => "cores.cap",
            { PluginAction: not null } => "plugin.action",
            { Arrange: not null } => "preset.arrange",
            { CapturePause: not null } => "capture.pause",
            { CaptureExclude: not null } => "capture.exclude",
            { UseFallback: not null } => "learned.use_fallback",
            { Relearn: not null } => "learned.relearn",
            { Snooze: not null } => "alert.snooze",
            _ => "?",
        };
    }

    private static string PauseId(string target, bool pause)
    {
        var verb = pause ? ".pause" : ".resume";
        if (target is "refits" or "content")
        {
            return "refits" + verb;
        }

        if (target is "tracking" or "capture")
        {
            return target + verb;
        }

        if (target.StartsWith("plugin:", StringComparison.Ordinal))
        {
            return "plugin" + verb;
        }

        if (target.StartsWith("job:", StringComparison.Ordinal) || target.StartsWith("group:", StringComparison.Ordinal))
        {
            return "job" + verb;
        }

        return "?";
    }

    /// <summary>Parse a target string (<c>group:x</c>, <c>job:id</c>, <c>process:name</c>, <c>plugin:id</c>, <c>pid:n</c>) into a target map.</summary>
    public static Dictionary<string, string>? ParseTarget(string? target)
    {
        if (string.IsNullOrEmpty(target))
        {
            return null;
        }

        var colon = target.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal) { ["what"] = target };
        }

        return new Dictionary<string, string>(StringComparer.Ordinal) { [target[..colon]] = target[(colon + 1)..] };
    }

    public static ActionRequest ToRequest(RuleAction a, string ruleId, RuleSubject? subject, Func<string, string> fill)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(fill);
        var id = ActionId(a) ?? throw new ArgumentException("toast is not an action");
        var args = new Dictionary<string, string>(StringComparer.Ordinal);
        Dictionary<string, string>? target = null;
        switch (a)
        {
            case { Pause: { } p }:
                target = p is "refits" or "tracking" or "capture" ? null : ParseTarget(p);
                if (p == "content")
                {
                    target = new Dictionary<string, string>(StringComparer.Ordinal) { ["worker"] = "content" };
                }

                if (a.Mode is { } mode)
                {
                    args["mode"] = mode;
                }

                break;
            case { Resume: { } r }:
                target = r is "refits" or "tracking" or "capture" ? null : ParseTarget(r);
                break;
            case { SetPriority: { } prio }:
                args["priority"] = prio;
                break;
            case { Efficiency: { } on }:
                args["on"] = on ? "true" : "false";
                break;
            case { CapCores: { } cap }:
                args["max"] = cap.Max.ToString(System.Globalization.CultureInfo.InvariantCulture);
                break;
            case { PluginAction: { } pa }:
                var c = pa.IndexOf(':', StringComparison.Ordinal);
                target = new Dictionary<string, string>(StringComparer.Ordinal) { ["plugin"] = c > 0 ? pa[..c] : pa };
                args["action"] = c > 0 ? pa[(c + 1)..] : string.Empty;
                break;
            case { Arrange: { } preset }:
                target = new Dictionary<string, string>(StringComparer.Ordinal) { ["preset"] = preset };
                break;
            case { CapturePause: { } span }:
                args["for"] = span;
                break;
            case { CaptureExclude: { } what }:
                var cc = what.IndexOf(':', StringComparison.Ordinal);
                args[cc > 0 ? what[..cc] : "app"] = cc > 0 ? what[(cc + 1)..] : what;
                break;
            case { UseFallback: { } policy }:
                args["policy"] = policy;
                break;
            case { Relearn: { } policy2 }:
                args["policy"] = policy2;
                break;
            case { Snooze: { } snooze }:
                args["for"] = snooze;
                target = new Dictionary<string, string>(StringComparer.Ordinal) { ["rule"] = ruleId };
                break;
            case { Badge: { } badge }:
                args["text"] = fill(badge);
                target = new Dictionary<string, string>(StringComparer.Ordinal) { ["rule"] = ruleId };
                break;
            case { Card: { } card }:
                target = new Dictionary<string, string>(StringComparer.Ordinal) { ["card"] = card };
                break;
        }

        target ??= ParseTarget(a.Target);
        if (target is null && subject?.Target is { } st)
        {
            target = new Dictionary<string, string>(st, StringComparer.Ordinal);
        }

        return new ActionRequest
        {
            Action = id,
            Target = target,
            Args = args.Count == 0 ? null : args,
            Origin = Origin.Rule,
            Rule = ruleId,
        };
    }
}
