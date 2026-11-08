using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Snapshot;
using Tasktor.Contracts.Workflow;
using Tasktor.Core.Features;
using Tasktor.Core.Learning;
using Tasktor.Core.Storage;
using Tasktor.Core.Workflow;
using Tasktor.Tests.Shared;

namespace Tasktor.Core.Tests;

/// <summary>C7 feedback guardrail (pure).</summary>
public sealed class ClusterFeedbackTests
{
    [Fact]
    public void A_cluster_hides_only_after_5_shown_with_more_than_30_percent_wrong()
    {
        var f = new ClusterFeedback();
        f.Shown("a1");
        Assert.False(f.Wrong("a1")); // 1 of 1 wrong, but fewer than 5 shown
        for (var i = 0; i < 3; i++)
        {
            f.Shown("a1");
        }

        f.Shown("a1");
        Assert.False(f.ShouldHide("a1")); // 1 of 5 = 20%
        Assert.True(f.Wrong("a1"));       // 2 of 5 = 40%
        Assert.Equal(0.4, f.WrongShare("a1"), 6);
        for (var i = 0; i < 10; i++)
        {
            f.Shown("a1");
        }

        Assert.False(f.ShouldHide("a1")); // the last 10 labels are all right again
        f.Reset();
        Assert.Equal(0, f.WrongShare("a1"));
    }
}

/// <summary>C8 / C9 / E11.2 inside the tracking feature: refit end to end with a fake worker, closed-activity assignment, focus measures.</summary>
public sealed class WorkflowRefitTests : IDisposable
{
    private static readonly string ModelFixture = Path.Combine(RepoPaths.Root, "tests", "fixtures", "workflow", "model");
    private readonly FeatureRig _rig = new();
    private readonly WorkflowTracking _tracking;
    private readonly List<WorkerJob> _jobs = [];
    private int _exit;

    public WorkflowRefitTests()
    {
        var layout = _rig.Store.Layout;
        _tracking = new WorkflowTracking(_rig.Context, null, null, () => new StoreSet(new EventStore(layout.EventsDb), new EpisodeStore(layout.EpisodesDb)), new KeyedHasher(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray(), "tok"))
        {
            InlineStores = true,
            PreregPath = Path.Combine(RepoPaths.Root, "worker", "prereg.json"),
            RunWorker = (job, _) =>
            {
                _jobs.Add(job);
                if (_exit == 0)
                {
                    Directory.CreateDirectory(job.Output);
                    foreach (var f in Directory.GetFiles(ModelFixture))
                    {
                        File.Copy(f, Path.Combine(job.Output, Path.GetFileName(f)));
                    }
                }

                return Task.FromResult(_exit);
            },
        };
        _rig.Add(_tracking);
    }

    public void Dispose()
    {
        if (_tracking.Enabled)
        {
            _tracking.Disable();
        }

        _rig.Dispose();
    }

    private void Focus(string exe, string title, uint pid)
    {
        _tracking.OnWindow(new WindowEvent(WindowEventKind.Foreground, (nint)pid, new ProcKey(pid, pid), 0, _rig.Clock.UtcNow) { Exe = exe, Title = Sensitive.Of(title) });
        _rig.Loop.RunPending();
    }

    [Fact]
    public void A_refit_extracts_runs_the_worker_validates_and_swaps_the_model_in_and_persists_the_cadence()
    {
        Assert.Null(_tracking.Model);
        Assert.True(_tracking.StartRefit());
        _rig.Loop.RunPending();
        _rig.Loop.RunPending();
        var job = Assert.Single(_jobs);
        Assert.StartsWith(_rig.Store.Layout.ExtractDir, job.Extract, StringComparison.Ordinal);
        Assert.NotNull(_tracking.Model);
        Assert.Equal("v1", _tracking.Model!.Version);
        Assert.Equal(1, _tracking.Refits);
        Assert.False(_tracking.Planner.Running);
        Assert.Equal(_rig.Clock.UtcNow, _tracking.Planner.LastFit);
        Assert.False(Directory.Exists(job.Output)); // staging moved into models/
        Assert.False(Directory.Exists(job.Extract)); // the extract is not kept
        Assert.True(File.Exists(Path.Combine(_rig.Store.Layout.ModelsDir, "planner.json")));
        Assert.DoesNotContain(_tracking.Cards()[0].Blocks, b => b is Contracts.Cards.BadgeBlock bb && bb.Text.ToString().Contains("Learning", StringComparison.Ordinal));

        // The next start loads the current model without a refit.
        _tracking.Disable();
        _tracking.Model = null;
        _tracking.Enable();
        Assert.Equal("v1", _tracking.Model?.Version);
        Assert.Equal(_rig.Clock.UtcNow, _tracking.Planner.LastFit);
    }

    [Fact]
    public void A_failed_worker_keeps_the_current_model_and_counts_as_todays_attempt()
    {
        _exit = 3;
        _tracking.StartRefit();
        _rig.Loop.RunPending();
        _rig.Loop.RunPending();
        Assert.Null(_tracking.Model);
        Assert.False(_tracking.Planner.Running);
        Assert.False(_tracking.Planner.Due(_rig.Clock.UtcNow.AddHours(1), TimeSpan.FromHours(1), true, false, 500, 500)); // not twice a day
        Assert.True(_tracking.Planner.Due(_rig.Clock.UtcNow.AddDays(1.1), TimeSpan.FromHours(1), true, false, 500, 500));
    }

    [Fact]
    public void Closed_activities_are_assigned_with_the_model_and_feed_totals_and_listeners()
    {
        _tracking.StartRefit();
        _rig.Loop.RunPending();
        _rig.Loop.RunPending();
        var assigned = new List<(string Label, string Name, double Conf)>();
        _tracking.ActivityAssigned += (_, label, name, conf) => assigned.Add((label, name, conf));
        Focus("code.exe", "parser.rs - engine - Visual Studio Code", 10);
        _rig.Advance(TimeSpan.FromMinutes(12), TimeSpan.FromSeconds(60));
        Focus("winword.exe", "Chapter 2 methods draft - Word", 20);
        _rig.Advance(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(60));
        var (label, name, conf) = Assert.Single(assigned);
        Assert.False(string.IsNullOrEmpty(name));
        Assert.True(label.StartsWith("app:", StringComparison.Ordinal) || conf >= 0.5, label + " " + conf);
        Assert.Equal(12 * 60_000, _tracking.TodayMs.Values.Sum());
        using var episodes = new EpisodeStore(_rig.Store.Layout.EpisodesDb);
        Assert.Equal(1, episodes.Db.Scalar("SELECT COUNT(*) FROM assignment WHERE model = 'v1'"));
    }

    [Fact]
    public void Focus_measures_are_overlap_weighted_on_intent_share_switches_idle_and_confidence()
    {
        var t0 = _rig.Clock.UtcNow;
        Focus("code.exe", "a.rs", 10);
        _rig.Advance(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(60));
        Focus("chrome.exe", "Docs", 20);
        _rig.Advance(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(60));
        Focus("code.exe", "b.rs", 10);
        _rig.Advance(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(60));
        Focus("slack.exe", "x", 30);
        _rig.Advance(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(60));
        var m = _tracking.Measure(t0, t0.AddMinutes(25), "app:code.exe");
        Assert.NotNull(m);
        Assert.Equal(0.8, m!.OnIntentShare, 3); // 20 of 25 minutes in Code
        Assert.Equal(2, m.Switches);           // Code → Chrome → Code
        Assert.Equal(0, m.IdleShare);
        Assert.Equal(0, m.Confidence);          // app-level labels carry no learned confidence
        Assert.Null(_tracking.Measure(t0.AddDays(-2), t0.AddDays(-1), null));
        Assert.Equal(0.8, _tracking.Measure(t0, t0.AddMinutes(25), null)!.OnIntentShare, 3); // no intent: the block's main label
    }

    [Fact]
    public async Task Forget_hooks_run_before_and_after_the_delete()
    {
        var order = new List<string>();
        var forgetter = new Forgetter(_rig.Store.Layout);
        forgetter.Before[ForgetScope.Tracking].Add(() =>
        {
            order.Add("before:" + Directory.Exists(_rig.Store.Layout.TrackDir));
            _tracking.Disable();
            return Task.CompletedTask;
        });
        forgetter.After[ForgetScope.Tracking].Add(() =>
        {
            order.Add("after:" + Directory.Exists(_rig.Store.Layout.TrackDir));
            _tracking.Enable();
            return Task.CompletedTask;
        });
        await forgetter.ForgetAsync(ForgetScope.Tracking);
        Assert.Equal(["before:True", "after:False"], order);
        Assert.True(_tracking.Enabled);
        Assert.True(File.Exists(_rig.Store.Layout.EventsDb)); // fresh stores
    }
}
