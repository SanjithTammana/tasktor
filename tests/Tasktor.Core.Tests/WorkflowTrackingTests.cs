using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Workflow;
using Tasktor.Core.Features;
using Tasktor.Core.Learning;
using Tasktor.Core.Workflow;
using Tasktor.Tests.Shared;

namespace Tasktor.Core.Tests;

/// <summary>C9 feature on fake sources: events reach the stores through the gate, the card falls back to the app, pause, disable.</summary>
public sealed class WorkflowTrackingTests : IDisposable
{
    private readonly FeatureRig _rig = new();
    private readonly Fake<WindowEvent> _win = new();
    private readonly Fake<PresenceEvent> _presence = new();
    private readonly WorkflowTracking _tracking;
    private bool _paused;

    public WorkflowTrackingTests()
    {
        var layout = _rig.Store.Layout;
        _tracking = new WorkflowTracking(_rig.Context, _win, _presence, () => new StoreSet(new EventStore(layout.EventsDb), new EpisodeStore(layout.EpisodesDb)), new KeyedHasher(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray(), "tok"))
        {
            PausedNow = () => _paused,
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

    private sealed class Fake<T> : IEventSource<T>
    {
        public Action<T>? Post { get; private set; }

        public string Name => "fake";

        public void Start(Action<T> post) => Post = post;

        public void Stop() => Post = null;
    }

    private void Focus(string exe, string title, uint pid)
    {
        _win.Post!(new WindowEvent(WindowEventKind.Foreground, (nint)pid, new ProcKey(pid, pid), 0, _rig.Clock.UtcNow) { Exe = exe, Title = Sensitive.Of(title) });
        _rig.Loop.RunPending();
    }

    [Fact]
    public void Without_a_model_the_card_shows_the_app_and_closed_activities_add_up_today()
    {
        Focus("Code.exe", "parser.rs - engine - Visual Studio Code", 10);
        _rig.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal("Code", _rig.State.Current.Workflow!.Name);
        Assert.Equal(Contracts.Snapshot.WorkflowLevel.App, _rig.State.Current.Workflow.Level);
        _rig.Advance(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(60));
        Focus("chrome.exe", "Docs - Google Chrome", 20);
        _rig.Advance(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(60));
        Assert.True(_tracking.TodayMs.Values.Sum() >= 600_000, $"acts={_tracking.Pipeline!.Builder.Activities.Count} open={_tracking.Pipeline.Builder.Open?.App} events={_tracking.Pipeline.Events} today={string.Join(",", _tracking.TodayMs)}");
        Assert.Equal("core.workflow", Assert.Single(_tracking.Cards()).Id);
    }

    [Fact]
    public void Pause_drops_events_and_disable_closes_both_stores()
    {
        Focus("code.exe", "a.rs", 10);
        _paused = true;
        _rig.Advance(TimeSpan.FromSeconds(61));
        Focus("code.exe", "FixtureTitleWhilePaused", 10);
        _paused = false;
        _rig.Advance(TimeSpan.FromSeconds(61));
        _tracking.Disable();
        using var events = new EventStore(_rig.Store.Layout.EventsDb); // reopen: the feature closed its connection
        var titles = new List<string?>();
        using (var s = events.Db.Prepare("SELECT title FROM event"))
        {
            while (s.Step())
            {
                titles.Add(s.Text(0));
            }
        }

        Assert.Contains("a.rs", titles);
        Assert.DoesNotContain("FixtureTitleWhilePaused", titles);
        Assert.Null(_rig.State.Current.Workflow);
    }

    [Fact]
    public void Lifecycle_events_for_presets_are_never_recorded()
    {
        Focus("code.exe", "a.rs", 10);
        foreach (var kind in new[] { WindowEventKind.Shown, WindowEventKind.Destroyed, WindowEventKind.MoveSizeStart, WindowEventKind.MoveSizeEnd })
        {
            _win.Post!(new WindowEvent(kind, 30, new ProcKey(30, 30), 0, _rig.Clock.UtcNow) { Exe = "notes.exe", Title = Sensitive.Of("LifecycleTitle") });
        }

        _rig.Loop.RunPending();
        _rig.Advance(TimeSpan.FromSeconds(61));
        _tracking.Disable();
        using var events = new EventStore(_rig.Store.Layout.EventsDb);
        var titles = new List<string?>();
        using (var s = events.Db.Prepare("SELECT title FROM event"))
        {
            while (s.Step())
            {
                titles.Add(s.Text(0));
            }
        }

        Assert.Contains("a.rs", titles);
        Assert.DoesNotContain("LifecycleTitle", titles);
    }
}

/// <summary>C7 rename / merge and the C9 summary, on the fixture model.</summary>
public sealed class WorkflowSummaryTests : IDisposable
{
    private static readonly string ModelDir = Path.Combine(RepoPaths.Root, "tests", "fixtures", "workflow", "model");
    private readonly FeatureRig _rig = new();
    private readonly Fake<WindowEvent> _win = new();

    public void Dispose() => _rig.Dispose();

    private sealed class Fake<T> : IEventSource<T>
    {
        public Action<T>? Post { get; private set; }

        public string Name => "fake";

        public void Start(Action<T> post) => Post = post;

        public void Stop() => Post = null;
    }

    private WorkflowTracking Tracking(WorkflowModel? model = null)
    {
        var layout = _rig.Store.Layout;
        var t = new WorkflowTracking(_rig.Context, _win, null, () => new StoreSet(new EventStore(layout.EventsDb), new EpisodeStore(layout.EpisodesDb)), new KeyedHasher(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray(), "tok"))
        {
            InlineStores = true,
            Model = model,
        };
        _rig.Add(t);
        return t;
    }

    private void Work(string exe, string title, uint pid, TimeSpan span)
    {
        _win.Post!(new WindowEvent(WindowEventKind.Foreground, (nint)pid, new ProcKey(pid, pid), 0, _rig.Clock.UtcNow) { Exe = exe, Title = Sensitive.Of(title) });
        _rig.Loop.RunPending();
        _rig.Advance(span, TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void The_summary_counts_minutes_per_workflow_including_earlier_runs_and_holds_no_titles()
    {
        var first = Tracking();
        Work("code.exe", "FixtureSecretTitle - engine", 10, TimeSpan.FromMinutes(20));
        Work("chrome.exe", "Docs", 20, TimeSpan.FromMinutes(10));
        Work("code.exe", "b.rs", 10, TimeSpan.FromMinutes(1));
        first.Disable();
        _rig.Features.Remove(first);

        var second = Tracking();
        _rig.Loop.RunPending();
        Work("chrome.exe", "Docs", 20, TimeSpan.FromMinutes(5));
        Work("code.exe", "c.rs", 10, TimeSpan.FromMinutes(1));
        var summary = second.SummaryNode("7d");
        var by = summary["by_workflow"]!.AsObject();
        Assert.True((double)by["Code"]! >= 20);
        Assert.True((double)by["Chrome"]! >= 14);
        Assert.Equal(1, (int)summary["active_days"]!);
        Assert.DoesNotContain("FixtureSecretTitle", summary.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Rename_and_merge_apply_live_and_persist_for_the_next_refit()
    {
        var model = WorkflowModel.Open(ModelDir);
        var t = Tracking(model);
        var (a, b) = (model.Manifest.Clusters[0].Id, model.Manifest.Clusters[1].Id);
        var r = _rig.Act(new Contracts.Actions.ActionRequest { Action = "workflow.rename", Origin = Contracts.Actions.Origin.Flyout, Target = new Dictionary<string, string> { ["workflow"] = a }, Args = new Dictionary<string, string> { ["name"] = "Thesis" } });
        Assert.Equal(Contracts.Actions.ActionStatus.Ok, r.Status);
        Assert.Equal("Thesis", t.NameOf(a));
        Assert.Equal(Contracts.Actions.ActionStatus.Ok, _rig.Act(new Contracts.Actions.ActionRequest { Action = "workflow.merge", Origin = Contracts.Actions.Origin.Flyout, Target = new Dictionary<string, string> { ["workflow"] = b }, Args = new Dictionary<string, string> { ["into"] = a } }).Status);
        Assert.Equal(Contracts.Actions.ActionStatus.Refused, _rig.Act(new Contracts.Actions.ActionRequest { Action = "workflow.merge", Origin = Contracts.Actions.Origin.Flyout, Target = new Dictionary<string, string> { ["workflow"] = a }, Args = new Dictionary<string, string> { ["into"] = b } }).Status); // no cycles
        Assert.Equal(Contracts.Actions.ActionStatus.Refused, _rig.Act(new Contracts.Actions.ActionRequest { Action = "workflow.rename", Origin = Contracts.Actions.Origin.Flyout, Target = new Dictionary<string, string> { ["workflow"] = "nope" }, Args = new Dictionary<string, string> { ["name"] = "x" } }).Status);
        var saved = System.Text.Json.JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(_rig.Store.Layout.ModelsDir, "overrides.json")), Contracts.ContractsJsonContext.Default.OverridesFile)!;
        Assert.Equal("Thesis", saved.Names[a]);
        Assert.Equal(a, saved.Merge[b]);
    }
}
