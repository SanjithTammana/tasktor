using System.Globalization;
using System.Text;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Rules;
using Tasktor.Core.Actions;
using Tasktor.Core.Apps;
using Tasktor.Core.Loop;
using Tasktor.Core.Time;

namespace Tasktor.Core.Rules;

/// <summary>What one evaluation asks for: a toast to show, or an action request for the router (origin <c>rule</c>).</summary>
public sealed record RuleOutput(string RuleId, string Subject, ToastRequest? Toast, ActionRequest? Action, bool IsUndo = false);

/// <summary>
/// Evaluates the rule set (U1 D-H6, A's RuleEngine): pure, snapshot + events in, toasts and action requests out.
/// Each rule fires per subject (a job, a process, a volume, an event) when its condition has held for <c>for</c>,
/// at most once per <c>cooldown</c>, and re-arms when the condition clears; with <c>undo</c>, clearing emits the
/// reversing requests. Toasts carry ≤ 3 buttons; <c>run</c> exists only as a toast button. Loop-only.
/// </summary>
public sealed class RuleEngine
{
    private readonly Dictionary<(string Rule, string Subject), SubjectState> _states = [];
    private readonly Func<string, string?> _knownFolder;
    private RuleSet _set = RuleSet.Empty;

    /// <param name="knownFolder">Resolves <c>{Downloads}</c>-style names to paths (never literal paths in shipped rules).</param>
    public RuleEngine(Func<string, string?>? knownFolder = null) => _knownFolder = knownFolder ?? (_ => null);

    /// <summary>Resolves Known Folder names (<c>Downloads</c> → its path) for placeholders.</summary>
    public Func<string, string?> KnownFolder => _knownFolder;

    public RuleSet Rules => _set;

    /// <summary>Hot reload: swap the rule set. State of rules that still exist is kept; removed rules emit their undo.</summary>
    public List<RuleOutput> SetRules(RuleSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        var outputs = new List<RuleOutput>();
        var keep = set.Rules.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var key in _states.Keys.Where(k => !keep.Contains(k.Rule)).ToList())
        {
            outputs.AddRange(_states[key].Undo.Select(u => new RuleOutput(key.Rule, key.Subject, null, u, IsUndo: true)));
            _states.Remove(key);
        }

        _set = set;
        return outputs;
    }

    /// <summary>Active (fired, not yet cleared) rule subjects: for the guards card and "Undo".</summary>
    public IEnumerable<(string Rule, string Subject, DateTimeOffset Since)> Active =>
        _states.Where(kv => kv.Value.Active && !kv.Value.Overridden).Select(kv => (kv.Key.Rule, kv.Key.Subject, kv.Value.FiredAt));

    /// <summary>
    /// The user's "Undo" on an active guard: emit its undo now and keep it from firing again until its condition clears
    /// (otherwise it would re-apply at the next evaluation).
    /// </summary>
    public List<RuleOutput> UndoNow(string ruleId)
    {
        var outputs = new List<RuleOutput>();
        foreach (var (key, st) in _states.Where(kv => kv.Key.Rule == ruleId && kv.Value.Active && !kv.Value.Overridden))
        {
            outputs.AddRange(st.Undo.Select(u => new RuleOutput(key.Rule, key.Subject, null, u, IsUndo: true)));
            st.Undo.Clear();
            st.Overridden = true;
        }

        return outputs;
    }

    public List<RuleOutput> Evaluate(DateTimeOffset now, RuleInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var outputs = new List<RuleOutput>();
        var seen = new HashSet<(string, string)>();
        foreach (var rule in _set.Rules)
        {
            var isEvent = Uses(rule.Rule.When, c => c.Event is not null);
            foreach (var subject in Subjects(rule.Rule.When, inputs))
            {
                var key = (rule.Id, subject.Id);
                seen.Add(key);
                var fields = new Dictionary<string, string>(inputs.Fields, StringComparer.Ordinal);
                foreach (var kv in subject.Fields)
                {
                    fields[kv.Key] = kv.Value;
                }

                var matched = Eval(rule.Rule.When, inputs, subject, fields);
                if (!_states.TryGetValue(key, out var st))
                {
                    st = new SubjectState(rule.HoldFor, rule.Cooldown);
                    _states[key] = st;
                }

                var held = isEvent ? matched : st.Hold.Update(matched, now);
                if (held && !st.Active)
                {
                    if (st.Cooldown.TryFire(now))
                    {
                        st.Active = !isEvent;
                        st.FiredAt = now;
                        st.Undo.Clear();
                        Fire(rule, subject, fields, outputs, st);
                    }
                }
                else if (!matched && st.Active)
                {
                    st.Active = false;
                    st.Overridden = false;
                    if (rule.Rule.Undo == true)
                    {
                        outputs.AddRange(st.Undo.Select(u => new RuleOutput(rule.Id, subject.Id, null, u, IsUndo: true)));
                    }

                    st.Undo.Clear();
                }
            }
        }

        // Subjects that went away (a job ended, a process exited): undo and forget their state.
        foreach (var key in _states.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            var st = _states[key];
            if (st.Active && _set.Find(key.Rule)?.Rule.Undo == true)
            {
                outputs.AddRange(st.Undo.Select(u => new RuleOutput(key.Rule, key.Subject, null, u, IsUndo: true)));
            }

            if (st.Cooldown.LastFired is null || st.Cooldown.Ready(now))
            {
                _states.Remove(key);
            }
            else
            {
                st.Active = false;
                st.Undo.Clear();
                st.Hold.Reset();
            }
        }

        return outputs;
    }

    /// <summary>Forget all firing state (e.g. alerts snoozed, rules reloaded from scratch).</summary>
    public void Reset() => _states.Clear();

    private void Fire(CompiledRule rule, RuleSubject subject, Dictionary<string, string> fields, List<RuleOutput> outputs, SubjectState st)
    {
        string Fill(string text) => Placeholders.Fill(text, fields, _knownFolder);
        foreach (var a in rule.Rule.Then)
        {
            if (a.Toast is { } toastText)
            {
                outputs.Add(new RuleOutput(rule.Id, subject.Id, BuildToast(rule, subject, Fill(toastText), a, fields), null));
                continue;
            }

            var req = RuleActionMap.ToRequest(a, rule.Id, subject, Fill);
            outputs.Add(new RuleOutput(rule.Id, subject.Id, null, req));
            if (rule.Rule.Undo == true && ActionCatalog.Find(req.Action)?.Undo is { } undoId)
            {
                st.Undo.Insert(0, req with { Action = undoId, Args = null, Confirm = null });
            }
        }
    }

    private ToastRequest BuildToast(CompiledRule rule, RuleSubject subject, string text, RuleAction a, Dictionary<string, string> fields)
    {
        var tag = rule.Id + ":" + subject.Id;
        var buttons = new List<ToastButton>();
        foreach (var b in a.Buttons ?? [])
        {
            var colon = b.IndexOf(':', StringComparison.Ordinal);
            var verb = colon > 0 ? b[..colon] : b;
            var arg = colon > 0 ? b[(colon + 1)..] : null;
            ActionRequest req;
            string label;
            switch (verb)
            {
                case "open":
                    req = new ActionRequest { Action = "open", Target = Map("card", arg!), Origin = Origin.Toast, Toast = tag };
                    label = "Show";
                    break;
                case "run":
                    var args = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var k in new[] { "path", "file", "instance", "process", "job" })
                    {
                        if (fields.TryGetValue(k, out var v))
                        {
                            args[k] = v;
                        }
                    }

                    req = new ActionRequest
                    {
                        Action = "run",
                        Target = new Dictionary<string, string>(StringComparer.Ordinal) { ["rule"] = rule.Id, ["command"] = arg! },
                        Args = args,
                        Origin = Origin.Toast,
                        Toast = tag,
                    };
                    label = char.ToUpperInvariant(arg![0]) + arg[1..];
                    break;
                case "snooze":
                    req = new ActionRequest
                    {
                        Action = "alert.snooze",
                        Target = new Dictionary<string, string>(StringComparer.Ordinal) { ["rule"] = rule.Id, ["subject"] = subject.Id },
                        Args = Map("for", arg!),
                        Origin = Origin.Toast,
                        Toast = tag,
                    };
                    label = "Snooze " + DurationText.Humanize(DurationText.Parse(arg!));
                    break;
                default:
                    var target = subject.Target is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(subject.Target, StringComparer.Ordinal);
                    target["rule"] = rule.Id;
                    req = new ActionRequest { Action = verb, Target = target, Origin = Origin.Toast, Toast = tag };
                    label = ActionCatalog.Find(verb)?.Label ?? verb;
                    break;
            }

            buttons.Add(new ToastButton { Label = label, Request = req });
        }

        var severity = a.Severity switch { "critical" => Severity.Critical, "warn" => Severity.Warn, _ => Severity.Normal };
        return new ToastRequest { Tag = tag, Title = text, Severity = severity, Buttons = buttons };
    }

    private static Dictionary<string, string> Map(string k, string v) => new(StringComparer.Ordinal) { [k] = v };

    /// <summary>The subjects a rule iterates: jobs (learned), processes, events, metric instances, or one global subject.</summary>
    private static IEnumerable<RuleSubject> Subjects(Condition c, RuleInputs inputs)
    {
        if (Find(c, x => x.Learned is not null) is not null)
        {
            return inputs.Jobs;
        }

        if (Find(c, x => x.Process is not null) is { } p)
        {
            return inputs.Processes.Where(s => s.Name is not null && Glob.IsMatch(p.Process!, s.Name));
        }

        if (Find(c, x => x.Event is not null) is { } e)
        {
            return inputs.Events.Where(ev => ev.Name == e.Event).Select(ev => new RuleSubject { Id = ev.Id, Fields = ev.Fields, Target = ev.Target, Name = ev.Name });
        }

        if (Find(c, x => x.Metric is not null && x.Instance is null) is { } m)
        {
            var subjects = inputs.Metrics.Keys.Where(k => k.Id == m.Metric).Select(k => new RuleSubject
            {
                Id = k.Instance,
                Fields = new Dictionary<string, string>(StringComparer.Ordinal) { ["instance"] = k.Instance },
                Target = k.Instance.Length == 0 ? inputs.Target : new Dictionary<string, string>(StringComparer.Ordinal) { ["instance"] = k.Instance },
            }).ToList();
            return subjects.Count > 0 ? subjects : [new RuleSubject { Id = string.Empty, Target = inputs.Target }];
        }

        return [new RuleSubject { Id = string.Empty, Target = inputs.Target }];
    }

    private static Condition? Find(Condition c, Func<Condition, bool> pred)
    {
        if (pred(c))
        {
            return c;
        }

        foreach (var sub in (c.All ?? []).Concat(c.Any ?? []).Append(c.Not))
        {
            if (sub is not null && Find(sub, pred) is { } hit)
            {
                return hit;
            }
        }

        return null;
    }

    private static bool Uses(Condition c, Func<Condition, bool> pred) => Find(c, pred) is not null;

    private bool Eval(Condition c, RuleInputs inputs, RuleSubject s, Dictionary<string, string> fields)
    {
        if (c.All is { } all)
        {
            return all.All(x => Eval(x, inputs, s, fields));
        }

        if (c.Any is { } any)
        {
            return any.Any(x => Eval(x, inputs, s, fields));
        }

        if (c.Not is { } not)
        {
            return !Eval(not, inputs, s, fields);
        }

        if (c.Metric is { } metric)
        {
            var instance = c.Instance ?? (fields.TryGetValue("instance", out var i) ? i : string.Empty);
            if (!inputs.Metrics.TryGetValue(new MetricKey(metric, instance), out var v))
            {
                return false;
            }

            fields["value"] = Num(v);
            fields["instance"] = instance;
            return (c.Above is null || v > c.Above) && (c.Below is null || v < c.Below);
        }

        if (c.Learned is { } learned)
        {
            if (!s.Values.TryGetValue(learned.Stat, out var v) || !s.Thresholds.TryGetValue(learned.Stat, out var t))
            {
                return false;
            }

            fields["value"] = DurationText.Humanize(TimeSpan.FromSeconds(v));
            fields["threshold"] = DurationText.Humanize(TimeSpan.FromSeconds(t.Value));
            fields["source"] = t.Source.ToString().ToLowerInvariant();
            return v >= t.Value;
        }

        if (c.Group is { } group)
        {
            var g = inputs.Groups.GetValueOrDefault(group) ?? new GroupStatus(0, 0);
            fields["count"] = (c.BusyCountAbove is not null ? g.Busy : g.Count).ToString(CultureInfo.InvariantCulture);
            return (c.Running is null || (g.Count > 0) == c.Running)
                && (c.CountAbove is null || g.Count > c.CountAbove)
                && (c.BusyCountAbove is null || g.Busy > c.BusyCountAbove);
        }

        if (c.Process is not null)
        {
            if (s.Name is null || !Glob.IsMatch(c.Process, s.Name))
            {
                return false;
            }

            fields["process"] = s.Name;
            return (c.CpuAbove is null || s.Values.GetValueOrDefault("cores") > c.CpuAbove)
                && (c.AgeAbove is null || s.Values.GetValueOrDefault("age_s") > DurationText.Parse(c.AgeAbove).TotalSeconds)
                && (c.SilentFor is null || s.Values.GetValueOrDefault("silent_s") > DurationText.Parse(c.SilentFor).TotalSeconds);
        }

        if (c.State is { } state)
        {
            var value = inputs.States.GetValueOrDefault(state);
            return c.Is is { } expected ? string.Equals(value, expected, StringComparison.Ordinal) : value == "true";
        }

        if (c.Event is { } evt)
        {
            if (s.Name != evt)
            {
                return false;
            }

            return Filter(c.Folder, "folder", s, exact: true) && Filter(c.Glob, "file", s, exact: false)
                && Filter(c.App, "app", s, exact: false) && Filter(c.Title, "title", s, exact: false)
                && Filter(c.Agent, "agent", s, exact: false);
        }

        return false;
    }

    private bool Filter(string? want, string field, RuleSubject s, bool exact)
    {
        if (want is null)
        {
            return true;
        }

        if (!s.Fields.TryGetValue(field, out var have))
        {
            return false;
        }

        if (exact)
        {
            // Known Folder names ({Downloads}) expand here; compare case-insensitively, ignoring a trailing slash.
            var path = Placeholders.Fill(want, new Dictionary<string, string>(), _knownFolder);
            return string.Equals(have.TrimEnd('/', '\\'), path.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);
        }

        return Glob.IsMatch(want, have);
    }

    private static string Num(double v) => Math.Abs(v) >= 10 ? Math.Round(v).ToString("0", CultureInfo.InvariantCulture) : v.ToString("0.#", CultureInfo.InvariantCulture);

    private sealed class SubjectState(TimeSpan hold, TimeSpan cooldown)
    {
        public HoldFor Hold { get; } = new(hold);

        public Cooldown Cooldown { get; } = new(cooldown);

        public bool Active { get; set; }

        public DateTimeOffset FiredAt { get; set; }

        public List<ActionRequest> Undo { get; } = [];

        /// <summary>Undone by the user while still matching: stays quiet until the condition clears.</summary>
        public bool Overridden { get; set; }
    }
}

/// <summary>Fills <c>{value}</c>-style placeholders and Known Folder names.</summary>
public static class Placeholders
{
    public static string Fill(string text, IReadOnlyDictionary<string, string> fields, Func<string, string?> knownFolder)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(knownFolder);
        if (!text.Contains('{', StringComparison.Ordinal))
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var open = text.IndexOf('{', i);
            if (open < 0)
            {
                sb.Append(text, i, text.Length - i);
                break;
            }

            var close = text.IndexOf('}', open);
            if (close < 0)
            {
                sb.Append(text, i, text.Length - i);
                break;
            }

            sb.Append(text, i, open - i);
            var name = text[(open + 1)..close];
            if (fields.TryGetValue(name, out var v))
            {
                sb.Append(v);
            }
            else if (RuleCatalog.KnownFolders.Contains(name) && knownFolder(name) is { } path)
            {
                sb.Append(path);
            }
            else
            {
                sb.Append(text, open, close - open + 1);
            }

            i = close + 1;
        }

        return sb.ToString();
    }
}
