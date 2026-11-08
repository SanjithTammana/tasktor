using System.Text;
using System.Text.Json;
using Tasktor.Contracts;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Rules;
using Tasktor.Core.Actions;
using Tasktor.Core.Time;

namespace Tasktor.Core.Rules;

/// <summary>
/// Parses and validates <c>rules.json</c> (JSON with comments) over the shipped defaults (U1 D-H6). Each rule is checked
/// on its own: a bad rule is disabled and listed with its line number; the rest keep running. Pure (text in, set out),
/// so hot reload is "load again, swap on the loop".
/// </summary>
public static class RulesLoader
{
    public const int MaxToastButtons = 3;

    /// <summary>A bare <c>{ "id", "enabled" }</c> entry: no condition and no actions.</summary>
    private static bool IsToggle(Rule r) => r.Then.Count == 0 && r.When == new Condition();

    private static readonly JsonDocumentOptions DocOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 32 };

    /// <param name="defaultsText">The shipped <c>rules.default.json</c> (read-only in the install dir).</param>
    /// <param name="userText">The user's <c>rules.json</c>, or null.</param>
    /// <summary>Why a rule that calls <c>arrange</c> waits (U3 OQ-W2: a one-time consent per rule).</summary>
    public const string NeedsArrangeConsent = "needs your one-time consent to arrange windows (Settings → Rules)";

    /// <param name="hasArrangeConsent">Whether the user gave the one-time consent for a rule that calls <c>arrange</c> (U3 OQ-W2).</param>
    /// <param name="packTexts">Ticked pack rules (between the defaults and the user's file, so the user still overrides them).</param>
    public static RuleSet Load(string? defaultsText, string? userText, Func<string, bool>? hasArrangeConsent = null, IReadOnlyList<string>? packTexts = null)
    {
        hasArrangeConsent ??= _ => false;
        var errors = new List<RuleError>();
        var defaults = Parse(defaultsText, RuleOrigin.Default, errors);
        var packs = (packTexts ?? []).Select(t => Parse(t, RuleOrigin.Pack, errors)).ToList();
        var user = Parse(userText, RuleOrigin.User, errors);

        var groups = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var src in packs.Prepend(defaults).Append(user))
        {
            if (src.File?.Groups is { } g)
            {
                foreach (var kv in g)
                {
                    groups[kv.Key] = kv.Value;
                }
            }
        }

        var grouping = new List<GroupingRule>();
        grouping.AddRange(user.File?.Grouping ?? []);
        grouping.AddRange(defaults.File?.Grouping ?? []);

        // Overlay: a user rule with the same id replaces the default; "enabled": false turns one off.
        var byId = new Dictionary<string, (Rule Rule, RuleOrigin Origin, int Line)>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var (entry, entryOrigin, entryLine) in defaults.Rules.Concat(packs.SelectMany(p => p.Rules)).Concat(user.Rules))
        {
            var (rule, origin, line) = (entry, entryOrigin, entryLine);
            if (IsToggle(rule))
            {
                if (byId.TryGetValue(rule.Id, out var prev))
                {
                    (rule, origin, line) = (prev.Rule with { Enabled = rule.Enabled }, prev.Origin, prev.Line); // the shipped rule, toggled
                    byId[rule.Id] = (rule, origin, line);
                    continue;
                }
                else if (rule.Enabled == true)
                {
                    errors.Add(new RuleError(rule.Id, origin, line, "no rule with this id to turn on"));
                    continue;
                }
            }

            if (!byId.ContainsKey(rule.Id))
            {
                order.Add(rule.Id);
            }
            else if (origin == byId[rule.Id].Origin)
            {
                errors.Add(new RuleError(rule.Id, origin, line, "duplicate id; the first one is used"));
                continue;
            }

            byId[rule.Id] = (rule, origin, line);
        }

        var compiled = new List<CompiledRule>();
        var disabled = new HashSet<string>(StringComparer.Ordinal);
        var off = new List<CompiledRule>();
        var awaiting = new List<CompiledRule>();
        foreach (var id in order)
        {
            var (rule, origin, line) = byId[id];
            if (rule.Enabled == false)
            {
                disabled.Add(id);
                off.Add(new CompiledRule(rule, origin, line, TimeSpan.Zero, TimeSpan.Zero));
                continue;
            }

            var problem = Validate(rule, origin, groups, hasArrangeConsent);
            if (problem == NeedsArrangeConsent)
            {
                awaiting.Add(new CompiledRule(rule, origin, line, Dur(rule.HoldFor), Dur(rule.Cooldown))); // listed for consent, not as an error
                continue;
            }

            if (problem is not null)
            {
                errors.Add(new RuleError(rule.Id, origin, line, problem));
                continue;
            }

            compiled.Add(new CompiledRule(rule, origin, line, Dur(rule.HoldFor), Dur(rule.Cooldown)));
        }

        return new RuleSet(compiled, errors, groups, grouping) { Disabled = disabled, OffRules = off, AwaitingArrangeConsent = awaiting };
    }

    private sealed record Parsed(RulesFile? File, List<(Rule Rule, RuleOrigin Origin, int Line)> Rules);

    private static Parsed Parse(string? text, RuleOrigin origin, List<RuleError> errors)
    {
        var rules = new List<(Rule, RuleOrigin, int)>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return new Parsed(null, rules);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, DocOptions);
        }
        catch (JsonException ex)
        {
            errors.Add(new RuleError(null, origin, (int)(ex.LineNumber ?? 0) + 1, "not valid JSON: " + FirstLine(ex.Message)));
            return new Parsed(null, rules);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new RuleError(null, origin, 1, "the file must hold one object"));
                return new Parsed(null, rules);
            }

            var lines = RuleLines(text);
            RulesFile? header = null;
            try
            {
                // Groups and grouping rules; the rules array is read one by one below.
                var withoutRules = new StringBuilder("{");
                var first = true;
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.NameEquals("rules"))
                    {
                        continue;
                    }

                    if (!first)
                    {
                        withoutRules.Append(',');
                    }

                    first = false;
                    withoutRules.Append(JsonSerializer.Serialize(prop.Name, ContractsJsonContext.Default.String)).Append(':').Append(prop.Value.GetRawText());
                }

                header = JsonSerializer.Deserialize(withoutRules.Append('}').ToString(), ContractsJsonContext.Default.RulesFile);
            }
            catch (JsonException ex)
            {
                errors.Add(new RuleError(null, origin, 1, "groups / grouping: " + FirstLine(ex.Message)));
            }

            if (header is { SchemaVersion: > 1 })
            {
                errors.Add(new RuleError(null, origin, 1, "schema_version " + header.SchemaVersion + " is newer than this Tasktor understands"));
                return new Parsed(null, rules);
            }

            if (doc.RootElement.TryGetProperty("rules", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                var i = 0;
                foreach (var el in arr.EnumerateArray())
                {
                    var line = i < lines.Count ? lines[i] : 0;
                    i++;
                    try
                    {
                        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("enabled", out var en) && en.ValueKind is JsonValueKind.False or JsonValueKind.True
                            && el.TryGetProperty("id", out var offId) && offId.ValueKind == JsonValueKind.String && !el.TryGetProperty("when", out _))
                        {
                            // { "id": "x", "enabled": false } turns a rule off without restating it (D-H6); "enabled": true turns
                            // a shipped-off rule on the same way (Settings' toggles write this).
                            rules.Add((new Rule { Id = offId.GetString()!, Enabled = en.ValueKind == JsonValueKind.True, When = new Condition(), Then = [] }, origin, line));
                            continue;
                        }

                        var rule = el.Deserialize(ContractsJsonContext.Default.Rule);
                        if (rule is null)
                        {
                            errors.Add(new RuleError(null, origin, line, "empty rule"));
                            continue;
                        }

                        rules.Add((rule, origin, line));
                    }
                    catch (JsonException ex)
                    {
                        var id = el.ValueKind == JsonValueKind.Object && el.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
                        errors.Add(new RuleError(id, origin, line, FirstLine(ex.Message)));
                    }
                }
            }

            return new Parsed(header, rules);
        }
    }

    /// <summary>The line of each element of the top-level <c>rules</c> array.</summary>
    private static List<int> RuleLines(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var lines = new List<int>();
        var inRules = false;
        var pendingRules = false;
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName when reader.CurrentDepth == 1:
                    pendingRules = reader.ValueTextEquals("rules");
                    break;
                case JsonTokenType.StartArray when reader.CurrentDepth == 1 && pendingRules:
                    inRules = true;
                    break;
                case JsonTokenType.EndArray when reader.CurrentDepth == 1 && inRules:
                    inRules = false;
                    pendingRules = false;
                    break;
                case JsonTokenType.StartObject when inRules && reader.CurrentDepth == 2:
                    lines.Add(1 + bytes.AsSpan(0, (int)reader.TokenStartIndex).Count((byte)'\n'));
                    break;
                case JsonTokenType.StartObject or JsonTokenType.StartArray or JsonTokenType.String or JsonTokenType.Number
                    or JsonTokenType.True or JsonTokenType.False or JsonTokenType.Null when reader.CurrentDepth == 1:
                    if (!(reader.TokenType == JsonTokenType.StartArray && pendingRules))
                    {
                        pendingRules = false;
                    }

                    break;
            }
        }

        return lines;
    }

    private static string? Validate(Rule rule, RuleOrigin origin, IReadOnlyDictionary<string, IReadOnlyList<string>> groups, Func<string, bool> hasArrangeConsent)
    {
        if (string.IsNullOrWhiteSpace(rule.Id))
        {
            return "id is required";
        }

        if (rule.Builtin == true && origin != RuleOrigin.Default)
        {
            return "\"builtin\" is reserved for shipped rules";
        }

        if (rule.HoldFor is { } f && !DurationText.TryParse(f, out _))
        {
            return "\"for\" is not a duration: " + f;
        }

        if (rule.Cooldown is { } c && !DurationText.TryParse(c, out _))
        {
            return "\"cooldown\" is not a duration: " + c;
        }

        if (ValidateCondition(rule.When, groups) is { } condProblem)
        {
            return condProblem;
        }

        if (rule.Then.Count == 0)
        {
            return "\"then\" needs at least one action";
        }

        foreach (var a in rule.Then)
        {
            var kinds = RuleActionMap.Kinds(a);
            if (kinds.Count != 1)
            {
                return kinds.Count == 0 ? "an action has no kind" : "an action has more than one kind: " + string.Join(", ", kinds);
            }

            if (a.Toast is not null)
            {
                var buttons = a.Buttons ?? [];
                if (buttons.Count > MaxToastButtons)
                {
                    return "a toast may have at most 3 buttons";
                }

                foreach (var b in buttons)
                {
                    if (ValidateButton(b, rule) is { } bp)
                    {
                        return bp;
                    }
                }

                continue;
            }

            var id = RuleActionMap.ActionId(a);
            var def = id is null ? null : ActionCatalog.Find(id);
            if (def is null)
            {
                return "unknown action target: " + (a.Pause ?? a.Resume ?? kinds[0]);
            }

            if (def.Class != ActionClass.Reversible || def.UserOnly)
            {
                return "rules may only call reversible actions (" + kinds[0] + " is not)";
            }

            if (def.LiftsBlock)
            {
                return "rules cannot lift a capture block";
            }

            if (def.BuiltinRulesOnly && !(origin == RuleOrigin.Default && rule.Builtin == true))
            {
                return kinds[0] + " is for built-in rules only";
            }

            if (a.Mode is { } mode && mode is not ("polite" or "freeze"))
            {
                return "mode must be polite or freeze";
            }

            if (a.SetPriority is { } p && p is not ("below_normal" or "idle" or "normal"))
            {
                return "set_priority must be below_normal, idle or normal";
            }

            if (a.CapCores is { Max: < 1 })
            {
                return "cap_cores max must be at least 1";
            }

            if (a.Target is { } t && t.StartsWith("group:", StringComparison.Ordinal) && !groups.ContainsKey(t[6..]))
            {
                return "unknown group: " + t[6..];
            }

            if (a.CapturePause is { } span && span != "until_resume" && !DurationText.TryParse(span, out _))
            {
                return "capture_pause takes a duration or until_resume";
            }

            if (a.Arrange is not null && !hasArrangeConsent(rule.Id))
            {
                return NeedsArrangeConsent;
            }
        }

        return null;
    }

    private static string? ValidateButton(string button, Rule rule)
    {
        var colon = button.IndexOf(':', StringComparison.Ordinal);
        var verb = colon > 0 ? button[..colon] : button;
        var arg = colon > 0 ? button[(colon + 1)..] : null;
        switch (verb)
        {
            case "run":
                if (arg is null || rule.Commands is null || !rule.Commands.ContainsKey(arg))
                {
                    return "button run:" + arg + " names no command in this rule's \"commands\"";
                }

                return rule.Commands[arg].Count == 0 ? "command " + arg + " is empty" : null;
            case "open":
                return arg is null ? "open: needs a card id" : null;
            case "snooze":
                return arg is null || !DurationText.TryParse(arg, out _) ? "snooze: needs a duration" : null;
            default:
                var def = ActionCatalog.Find(verb);
                if (def is null)
                {
                    return "unknown button action: " + verb;
                }

                return (def.Surfaces & Surface.Toast) == 0 ? verb + " cannot be a toast button" : null;
        }
    }

    private static string? ValidateCondition(Condition c, IReadOnlyDictionary<string, IReadOnlyList<string>> groups)
    {
        var kinds = new List<string>();
        if (c.Metric is not null) kinds.Add("metric");
        if (c.Learned is not null) kinds.Add("learned");
        if (c.Group is not null) kinds.Add("group");
        if (c.Process is not null) kinds.Add("process");
        if (c.State is not null) kinds.Add("state");
        if (c.Event is not null) kinds.Add("event");
        if (c.All is not null) kinds.Add("all");
        if (c.Any is not null) kinds.Add("any");
        if (c.Not is not null) kinds.Add("not");
        if (kinds.Count != 1)
        {
            return kinds.Count == 0 ? "a condition has no kind" : "a condition has more than one kind: " + string.Join(", ", kinds);
        }

        switch (kinds[0])
        {
            case "metric":
                if (!RuleCatalog.Metrics.Contains(c.Metric!))
                {
                    return "unknown metric: " + c.Metric;
                }

                return c.Above is null && c.Below is null ? "metric needs above or below" : null;
            case "learned":
                var l = c.Learned!;
                if (!RuleCatalog.LearnedStats.Contains(l.Stat))
                {
                    return "unknown learned stat: " + l.Stat;
                }

                if (!DurationText.TryParse(l.Fallback, out _))
                {
                    return "learned fallback is not a duration: " + l.Fallback;
                }

                if (l.Clamp is { } clamp && (clamp.Count != 2 || !DurationText.TryParse(clamp[0], out var lo) || !DurationText.TryParse(clamp[1], out var hi) || lo > hi))
                {
                    return "learned clamp must be [min, max] durations";
                }

                return null;
            case "group":
                if (!groups.ContainsKey(c.Group!))
                {
                    return "unknown group: " + c.Group;
                }

                return c.Running is null && c.CountAbove is null && c.BusyCountAbove is null ? "group needs running, count_above or busy_count_above" : null;
            case "process":
                if (c.AgeAbove is { } age && !DurationText.TryParse(age, out _))
                {
                    return "age_above is not a duration";
                }

                if (c.SilentFor is { } sil && !DurationText.TryParse(sil, out _))
                {
                    return "silent_for is not a duration";
                }

                return c.CpuAbove is null && c.AgeAbove is null && c.SilentFor is null ? "process needs cpu_above, age_above or silent_for" : null;
            case "state":
                return RuleCatalog.States.Contains(c.State!) ? null : "unknown state: " + c.State;
            case "event":
                return RuleCatalog.Events.Contains(c.Event!) ? null : "unknown event: " + c.Event;
            case "all" or "any":
                var list = c.All ?? c.Any!;
                if (list.Count == 0)
                {
                    return kinds[0] + " needs at least one condition";
                }

                foreach (var sub in list)
                {
                    if (ValidateCondition(sub, groups) is { } p)
                    {
                        return p;
                    }
                }

                return null;
            default:
                return ValidateCondition(c.Not!, groups);
        }
    }

    private static TimeSpan Dur(string? text) => text is not null && DurationText.TryParse(text, out var v) ? v : TimeSpan.Zero;

    private static string FirstLine(string message)
    {
        var i = message.IndexOf('\n', StringComparison.Ordinal);
        return i < 0 ? message : message[..i];
    }
}
