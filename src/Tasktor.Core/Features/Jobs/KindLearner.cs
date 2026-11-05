using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Snapshot;
using Tasktor.Core.Learning;
using Tasktor.Core.Shapes;

namespace Tasktor.Core.Features.Jobs;

/// <summary>One guardrail report (B6.3): the kind, when, what happened and why.</summary>
public sealed record GuardrailEntry(KindId Kind, DateTimeOffset At, string Action, string Reason);

/// <summary>
/// B's per-kind learner (Adaptive core, D-B9): runs only at job end and when an alarm is labelled, never on the tick.
/// Holds the silent / ETA / longer-than-usual / admission policies and the guardrails: P1 out-of-distribution → fallback for
/// that run; P2 a > 2× jump waits for 2 more agreeing runs; P3 clamps; P4 > 30 % false alarms or worse than the fallback →
/// REPLACE until 5 clean runs; P5 ≤ 256 kinds (LRU). Off → every policy returns its fallback.
/// </summary>
public sealed class KindLearner
{
    public const int GuardrailLogMax = 200;

    public static readonly LearnedThreshold Silent = new()
    {
        Policy = "silence",
        Fallback = TimeSpan.FromMinutes(10),
        Min = TimeSpan.FromMinutes(2),
        Max = TimeSpan.FromMinutes(60),
    };

    private readonly List<GuardrailEntry> _log = [];
    private readonly Dictionary<KindId, (double Value, int Agreeing)> _lastThreshold = [];

    public KindLearner(KindStore store) => Store = store;

    public KindStore Store { get; }

    public bool LearningOn
    {
        get => Store.LearningOn;
        set => Store.LearningOn = value;
    }

    public IReadOnlyList<GuardrailEntry> Guardrails => _log;

    public event Action<GuardrailEntry>? GuardrailFired;

    /// <summary>The silent threshold for a kind (seconds), with P2 smoothing of sudden jumps.</summary>
    public LearnedValue SilentFor(KindId kind, bool outOfDistribution)
    {
        var stats = Store.Find(kind);
        var v = Silent.Current(stats, LearningOn, outOfDistribution);
        if (v.Source is not (ValueSource.Learned or ValueSource.Blended))
        {
            return v;
        }

        if (_lastThreshold.TryGetValue(kind, out var last) && (v.Value > 2 * last.Value || v.Value < last.Value / 2))
        {
            return v with { Value = last.Value }; // P2: keep the previous value until 2 more runs agree
        }

        return v;
    }

    /// <summary>B1.10: "usually ~1 h 10 m (12 runs)": the median duration, shown only with ≥ 3 good runs.</summary>
    public LearnedValue? Eta(KindId kind)
    {
        if (!LearningOn || Store.Find(kind) is not { GoodRuns: >= 3 } s || s.Duration.Count < 3)
        {
            return null;
        }

        return new LearnedValue(WindowStats.Median(s.Duration), ValueSource.Learned, s.GoodRuns, "duration.median", WindowStats.Median(s.Duration));
    }

    /// <summary>B2.7: elapsed beyond 1.5 × the p90 of good runs (needs ≥ 5).</summary>
    public bool LongerThanUsual(KindId kind, TimeSpan elapsed) =>
        LearningOn && Store.Find(kind) is { GoodRuns: >= 5 } s && s.Duration.Count >= 5 && elapsed.TotalSeconds > 1.5 * WindowStats.Quantile(s.Duration, 0.9);

    /// <summary>P1: this run's cores outside [0.5 × p10, 2 × p90] of the kind's history (with ≥ 5 good runs).</summary>
    public bool OutOfDistribution(KindId kind, double coresMedian)
    {
        if (Store.Find(kind) is not { GoodRuns: >= 5 } s || s.CoresMedian.Count < 5)
        {
            return false;
        }

        var lo = 0.5 * WindowStats.Quantile(s.CoresMedian, 0.1);
        var hi = 2 * WindowStats.Quantile(s.CoresMedian, 0.9);
        return coresMedian < lo || coresMedian > hi;
    }

    /// <summary>B1.8: a kind seen idle ≥ 5 times that never became a job is probed every 10 min instead of every tick.</summary>
    public bool ProbeRarely(KindId kind) => LearningOn && Store.Find(kind) is { StayedIdle: >= 5, BecameJob: 0 };

    /// <summary>B1.8: a kind that became a job in ≥ 2 of its last 3 admissions shows as "starting" at 60 s.</summary>
    public bool ShowStarting(KindId kind) => LearningOn && Store.Find(kind) is { AdmissionCount: >= 2 } s && s.RecentBecameJob >= 2;

    public void OnAdmission(KindId kind, bool becameJob, DateTimeOffset now)
    {
        Store.Get(kind, now).RecordAdmission(becameJob);
        Store.MarkDirty();
    }

    /// <summary>A job of this kind ended: update the rings and run the guardrails for this kind only.</summary>
    public void OnRunEnded(KindId kind, RunOutcome outcome, bool hadFalseAlarm, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (!LearningOn)
        {
            return;
        }

        var s = Store.Get(kind, now);
        var before = Silent.Current(s).Value;
        s.RecordRun(outcome);
        if (s.Mode == PolicyMode.Fallback)
        {
            s.CleanRunsSinceReplace = outcome.Good && !hadFalseAlarm ? s.CleanRunsSinceReplace + 1 : 0;
            if (s.CleanRunsSinceReplace >= 5)
            {
                s.Mode = PolicyMode.Learned;
                s.CleanRunsSinceReplace = 0;
                Report(kind, now, "back to learned", "5 clean runs since the switch to the default");
            }
        }

        var after = Silent.Current(s).Value;
        if (_lastThreshold.TryGetValue(kind, out var last) && (after > 2 * last.Value || after < last.Value / 2))
        {
            var agreeing = last.Agreeing + 1;
            if (agreeing >= 2)
            {
                _lastThreshold[kind] = (after, 0);
                Report(kind, now, "accepted a large change", "2 more runs agreed with the new threshold");
            }
            else
            {
                _lastThreshold[kind] = (last.Value, agreeing);
                Report(kind, now, "held the previous threshold", "the learned threshold changed by more than 2× (P2)");
            }
        }
        else
        {
            _lastThreshold[kind] = (after, 0);
        }

        _ = before;
        Store.MarkDirty();
    }

    /// <summary>A silent alarm was labelled: false (resumed and exited 0, or It's fine) or true (ended, failed).</summary>
    public void OnAlarmLabelled(KindId kind, bool falseAlarm, bool shadowFalse, DateTimeOffset now)
    {
        if (!LearningOn)
        {
            return;
        }

        var s = Store.Get(kind, now);
        s.RecordAlarm(falseAlarm, shadowFalse);
        if (s.Mode == PolicyMode.Learned && s.AlarmCount > 0)
        {
            var rate = s.RecentFalseAlarms / (double)s.AlarmCount;
            if (rate > 0.3 || s.RecentFalseAlarms > s.RecentShadowFalseAlarms)
            {
                s.Mode = PolicyMode.Fallback;
                s.CleanRunsSinceReplace = 0;
                Report(kind, now, "switched to default", s.RecentFalseAlarms + " of last " + s.AlarmCount + " alarms were false");
            }
        }

        Store.MarkDirty();
    }

    /// <summary>RETRAIN: clear the kind's rings, keep the counts.</summary>
    public void Relearn(KindId kind, DateTimeOffset now)
    {
        if (Store.Find(kind) is { } s)
        {
            s.Relearn();
            Report(kind, now, "relearn", "asked to relearn");
            Store.MarkDirty();
        }
    }

    /// <summary>REPLACE on request (the built-in rule action <c>use_fallback</c>).</summary>
    public void UseFallback(KindId kind, DateTimeOffset now, string reason)
    {
        var s = Store.Get(kind, now);
        s.Mode = PolicyMode.Fallback;
        s.CleanRunsSinceReplace = 0;
        Report(kind, now, "switched to default", reason);
    }

    private void Report(KindId kind, DateTimeOffset now, string action, string reason)
    {
        var e = new GuardrailEntry(kind, now, action, reason);
        _log.Add(e);
        if (_log.Count > GuardrailLogMax)
        {
            _log.RemoveAt(0);
        }

        GuardrailFired?.Invoke(e);
    }
}
