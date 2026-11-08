using Tasktor.Contracts.Snapshot;
using Tasktor.Contracts.Workflow;

namespace Tasktor.Core.Workflow;

/// <summary>One assignment result at one level (D-C1 fallback chain: workflow → activity cluster → app).</summary>
public sealed record LevelResult(string? Id, string? Name, double Confidence);

/// <summary>
/// C9 <c>CurrentWorkflow</c>: shows the highest level whose confidence clears the 0.5 floor, falling back workflow →
/// activity cluster → app; the shown value changes only after 2 agreeing results (hysteresis), so one odd title does not
/// flip the card. A cluster the user marked wrong too often is skipped (guardrail) until the next fit. Loop-only.
/// </summary>
public sealed class CurrentWorkflow
{
    private readonly AssignmentSpec _spec;
    private readonly HashSet<string> _suppressed = new(StringComparer.Ordinal);
    private (string Key, WorkflowState State)? _candidate;
    private int _agree;

    public CurrentWorkflow(AssignmentSpec spec) => _spec = spec;

    public WorkflowState? Shown { get; private set; }

    public void Suppress(string clusterId) => _suppressed.Add(clusterId);

    public void ClearSuppressed() => _suppressed.Clear();

    public bool IsSuppressed(string clusterId) => _suppressed.Contains(clusterId);

    /// <summary>Feed one round of results; returns true when the shown value changed.</summary>
    public bool Update(LevelResult? workflow, LevelResult? cluster, string app, string appName)
    {
        WorkflowState pick;
        if (workflow is { Id: { } w } && workflow.Confidence >= _spec.Floor && !_suppressed.Contains(w))
        {
            pick = new WorkflowState { Id = w, Name = workflow.Name ?? w, Confidence = Math.Round(workflow.Confidence, 3), Level = WorkflowLevel.Workflow, Source = WorkflowSource.Learned };
        }
        else if (cluster is { Id: { } c } && cluster.Confidence >= _spec.Floor && !_suppressed.Contains(c))
        {
            pick = new WorkflowState { Id = c, Name = cluster.Name ?? c, Confidence = Math.Round(cluster.Confidence, 3), Level = WorkflowLevel.Activity, Source = WorkflowSource.Learned };
        }
        else
        {
            pick = new WorkflowState { Id = "app:" + app, Name = appName, Confidence = 1, Level = WorkflowLevel.App, Source = WorkflowSource.Default };
        }

        if (Shown is null)
        {
            Shown = pick;
            return true;
        }

        if (pick.Id == Shown.Id)
        {
            _candidate = null;
            _agree = 0;
            var changed = Math.Abs(pick.Confidence - Shown.Confidence) >= 0.05;
            Shown = pick with { SwitchesToday = Shown.SwitchesToday };
            return changed;
        }

        if (_candidate?.Key == pick.Id)
        {
            _agree++;
        }
        else
        {
            _candidate = (pick.Id, pick);
            _agree = 1;
        }

        if (_agree < _spec.Hysteresis)
        {
            return false;
        }

        Shown = pick with { SwitchesToday = (Shown.SwitchesToday ?? 0) + 1 };
        _candidate = null;
        _agree = 0;
        return true;
    }
}

/// <summary>
/// C8 refit cadence (D-B9 shape): at most once a day, idle ≥ 10 min, on AC, not in a call or event mode, and ≥ 50 new
/// activities or the learned interval since the last fit (7 days, doubled after a calm refit up to 28, reset to 7 when
/// persistence drops or a cluster appears). P1: > 50% unassigned over 3 active days asks for an early refit.
/// </summary>
public sealed class RefitPlanner
{
    public static readonly TimeSpan MinGap = TimeSpan.FromDays(1);
    public static readonly TimeSpan Idle = TimeSpan.FromMinutes(10);

    public DateTimeOffset? LastFit { get; set; }

    public TimeSpan Interval { get; private set; } = TimeSpan.FromDays(7);

    public bool EarlyRequested { get; private set; }

    public bool Running { get; set; }

    /// <summary>Restore the persisted cadence (planner.json).</summary>
    public void Restore(DateTimeOffset? lastFit, double intervalDays)
    {
        LastFit = lastFit;
        Interval = TimeSpan.FromDays(Math.Clamp(intervalDays, 7, 28));
    }

    public bool Due(DateTimeOffset now, TimeSpan idleFor, bool onAc, bool busy, long newActivities, int activitiesTotal)
    {
        if (Running || !onAc || busy || idleFor < Idle || activitiesTotal < new GateThresholds().G0Activities)
        {
            return false;
        }

        if (LastFit is { } last && now - last < MinGap)
        {
            return false;
        }

        return LastFit is null || EarlyRequested || newActivities >= 50 || now - LastFit >= Interval;
    }

    public void RequestEarly(double unassignedShare3Days)
    {
        if (unassignedShare3Days > 0.5)
        {
            EarlyRequested = true;
        }
    }

    public void OnFitResult(DateTimeOffset now, double? persistence, bool newCluster)
    {
        LastFit = now;
        EarlyRequested = false;
        Running = false;
        Interval = persistence is { } p && p >= 0.95 && !newCluster
            ? TimeSpan.FromDays(Math.Min(28, Interval.TotalDays * 2))
            : (persistence is < 0.80 || newCluster ? TimeSpan.FromDays(7) : Interval);
    }
}
