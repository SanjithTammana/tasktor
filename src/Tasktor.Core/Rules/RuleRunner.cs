using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Snapshot;
using Tasktor.Core.Actions;
using Tasktor.Core.Features;
using Tasktor.Core.Logging;
using Tasktor.Core.Loop;
using Tasktor.Core.Time;

namespace Tasktor.Core.Rules;

/// <summary>
/// Runs the rule set on the loop: collects every feature's inputs, evaluates (at most once a second, when inputs changed),
/// shows toasts (≤ 3 buttons, each registered as an offer), sends rule actions through the router with origin <c>rule</c>,
/// and keeps <c>state.alerts</c> and <c>state.guards</c> (active rules with undo). Snoozed rule subjects stay quiet.
/// </summary>
public sealed class RuleRunner
{
    private readonly LogicLoop _loop;
    private readonly ActionRouter _router;
    private readonly INotifier _notifier;
    private readonly LogSink _log;
    private readonly Func<IEnumerable<Feature>> _features;
    private readonly Action<Func<SnapshotState, SnapshotState>> _update;
    private readonly Dictionary<(string Rule, string Subject), DateTimeOffset> _snoozed = [];
    private readonly Dictionary<(string Rule, string Subject), AlertState> _alerts = [];
    private DateTimeOffset _last = DateTimeOffset.MinValue;
    private IDisposable? _scheduled;

    public RuleRunner(LogicLoop loop, RuleEngine engine, ActionRouter router, INotifier notifier, LogSink log, Func<IEnumerable<Feature>> features, Action<Func<SnapshotState, SnapshotState>> update)
    {
        _loop = loop;
        Engine = engine;
        _router = router;
        _notifier = notifier;
        _log = log;
        _features = features;
        _update = update;
        router.IsBuiltinRule = id => id is not null && Engine.Rules.Find(id)?.IsBuiltin == true;
        router.Register("alert.snooze", Snooze);
        router.Register("alerts.snooze_all", _ => new PreparedAction
        {
            Summary = "Snooze alerts for 1 hour",
            Immediate = new ActionResult { Action = "alerts.snooze_all", Status = ActionStatus.Ok, Message = "Alerts snoozed for 1 hour" },
            Done = _ => SnoozedAllUntil = _loop.Clock.UtcNow + TimeSpan.FromHours(1),
        });
    }

    public RuleEngine Engine { get; }

    public DateTimeOffset? SnoozedAllUntil { get; private set; }

    public long Evaluations { get; private set; }

    /// <summary>Extra snooze hooks for built-in watches (e.g. A5's disk-low): rule id → (subject, until).</summary>
    public Dictionary<string, Action<string, DateTimeOffset>> SnoozeHooks { get; } = new(StringComparer.Ordinal);

    /// <summary>Hot reload: swap the set; removed rules' undo runs.</summary>
    public void SetRules(RuleSet set)
    {
        Dispatch(Engine.SetRules(set));
        foreach (var e in set.Errors)
        {
            _log.Warn("rule.invalid", LogField.Of("rule", e.Id), LogField.Of("line", e.Line));
        }
    }

    /// <summary>Inputs changed: evaluate as soon as the 1 s limit allows.</summary>
    public void MarkDirty()
    {
        if (_scheduled is not null)
        {
            return;
        }

        var now = _loop.Clock.UtcNow;
        var due = now - _last >= TimeSpan.FromSeconds(1) ? now : _last + TimeSpan.FromSeconds(1);
        _scheduled = _loop.At(due, () =>
        {
            _scheduled = null;
            Evaluate();
        });
    }

    public List<RuleOutput> Evaluate()
    {
        var now = _loop.Clock.UtcNow;
        _last = now;
        Evaluations++;
        var inputs = new RuleInputs();
        foreach (var f in _features())
        {
            if (f.Enabled)
            {
                f.Contribute(inputs, now);
            }
        }

        var outputs = Engine.Evaluate(now, inputs);
        Dispatch(outputs);

        // Alerts last while their rule subject stays active; guards are active rules with undo.
        var active = Engine.Active.Select(a => (a.Rule, a.Subject)).ToHashSet();
        foreach (var key in _alerts.Keys.Where(k => !active.Contains(k)).ToList())
        {
            _alerts.Remove(key);
        }

        var guards = Engine.Active
            .Where(a => Engine.Rules.Find(a.Rule)?.Rule.Undo == true)
            .Select(a => new GuardState { Rule = a.Rule, Text = Engine.Rules.Find(a.Rule)?.Rule.Description ?? a.Rule, Since = a.Since })
            .ToList();
        _update(s => s with
        {
            Alerts = [.. (s.Alerts ?? []).Where(x => !IsEngineRule(x.Rule)), .. _alerts.Values.OrderByDescending(x => x.Since)],
            Guards = guards.Count == 0 ? null : guards,
        });
        return outputs;
    }

    private bool IsEngineRule(string id) => Engine.Rules.Find(id) is not null;

    private void Dispatch(List<RuleOutput> outputs)
    {
        var now = _loop.Clock.UtcNow;
        foreach (var o in outputs)
        {
            if (o.Toast is { } toast)
            {
                if (_snoozed.TryGetValue((o.RuleId, o.Subject), out var until) && now < until)
                {
                    continue;
                }

                var card = toast.Buttons.Select(b => b.Request).FirstOrDefault(r => r.Action == "open")?.Target?.GetValueOrDefault("card");
                _alerts[(o.RuleId, o.Subject)] = new AlertState { Rule = o.RuleId, Severity = toast.Severity, Card = card, Since = now, Text = toast.Title };
                if (SnoozedAllUntil is { } all && now < all)
                {
                    continue;
                }

                foreach (var b in toast.Buttons)
                {
                    _router.Toasts.Offer(toast.Tag, b.Request);
                }

                _notifier.Show(toast);
                _log.Info("rule.fired", LogField.Of("rule", o.RuleId), LogField.Of("severity", CardKit.SeverityWord(toast.Severity)));
            }
            else if (o.Action is { } req)
            {
                _router.Submit(req).ContinueWith(
                    t => _loop.Post(() =>
                    {
                        if (t.IsCompletedSuccessfully && t.Result.Status is ActionStatus.Refused or ActionStatus.Failed)
                        {
                            _log.Warn("rule.action_refused", LogField.Of("rule", o.RuleId), LogField.Of("action", req.Action), LogField.Of("reason", t.Result.Reason));
                        }
                    }),
                    TaskScheduler.Default);
                if (o.IsUndo)
                {
                    _alerts.Remove((o.RuleId, o.Subject));
                }
            }
        }
    }

    private PreparedAction Snooze(ActionRequest req)
    {
        var rule = req.Target?.GetValueOrDefault("rule");
        var subject = req.Target?.GetValueOrDefault("subject") ?? string.Empty;
        var span = DurationText.TryParse(req.Args?.GetValueOrDefault("for") ?? "1h", out var s) ? s : TimeSpan.FromHours(1);
        if (rule is null)
        {
            return new PreparedAction { Summary = "Snooze", Refusal = "Which alert?" };
        }

        var label = DurationText.Humanize(span);
        return new PreparedAction
        {
            Summary = "Snooze " + rule + " for " + label,
            Immediate = new ActionResult { Action = "alert.snooze", Status = ActionStatus.Ok, Message = "Snoozed for " + label },
            Done = _ =>
            {
                var until = _loop.Clock.UtcNow + span;
                _snoozed[(rule, subject)] = until;
                if (SnoozeHooks.TryGetValue(rule, out var hook))
                {
                    hook(subject, until);
                }

                _notifier.Withdraw(rule + ":" + subject);
                _router.Toasts.Withdraw(rule + ":" + subject);
            },
        };
    }
}
