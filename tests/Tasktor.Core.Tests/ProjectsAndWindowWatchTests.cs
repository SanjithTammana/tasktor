using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Workflow;
using Tasktor.Core.Features;
using Tasktor.Core.Features.Projects;
using Tasktor.Core.Storage;
using Tasktor.Tests.Shared;

namespace Tasktor.Core.Tests;

/// <summary>U2 I6 (time per project) and I11 (required-window watch).</summary>
public sealed class ProjectsAndWindowWatchTests : IDisposable
{
    private readonly FeatureRig _rig = new();
    private readonly List<ExportTableSource> _exports = [];

    public void Dispose() => _rig.Dispose();

    private FeatureContext Ctx() => new()
    {
        Loop = _rig.Context.Loop,
        Scheduler = _rig.Context.Scheduler,
        Router = _rig.Context.Router,
        Log = _rig.Context.Log,
        Settings = _rig.Context.Settings,
        Store = _rig.Context.Store,
        UpdateState = _rig.Context.UpdateState,
        State = _rig.Context.State,
        RegisterPull = _rig.Context.RegisterPull,
        OffLoop = _rig.Context.OffLoop,
        RulesDirty = _rig.Context.RulesDirty,
        Persist = _rig.Context.Persist,
        RegisterExport = _exports.Add,
    };

    [Fact]
    public void Project_time_maps_workflows_to_projects_by_week_with_unassigned_and_the_mapping_persists()
    {
        // The rig's clock is Wednesday 2026-11-04 09:00Z.
        var map = new ProjectMap(_rig.Store.Layout.Projects);
        var p = _rig.Add(new ProjectTime(Ctx(), map, TimeZoneInfo.Utc));
        var now = _rig.Clock.UtcNow;
        p.Add("w1", 3_600_000, now.AddHours(-1));
        p.Add("w2", 1_800_000, now.AddDays(-1));
        p.Add("a7", 600_000, now.AddDays(-8)); // last week
        p.Add(null, 300_000, now);
        Assert.Equal(ActionStatus.Ok, _rig.Act(new ActionRequest { Action = "project.map", Origin = Origin.Flyout, Target = new Dictionary<string, string> { ["workflow"] = "w1" }, Args = new Dictionary<string, string> { ["project"] = "Thesis" } }).Status);
        _rig.Act(new ActionRequest { Action = "project.map", Origin = Origin.Flyout, Target = new Dictionary<string, string> { ["workflow"] = "w2" }, Args = new Dictionary<string, string> { ["project"] = "Thesis" } });
        _rig.Act(new ActionRequest { Action = "project.map", Origin = Origin.Cli, Target = new Dictionary<string, string> { ["workflow"] = "a7" }, Args = new Dictionary<string, string> { ["project"] = "Engine" } });

        Assert.Equal(new Dictionary<string, long> { ["Thesis"] = 5_400_000, ["Unassigned"] = 300_000 }, p.ThisWeek());
        Assert.Equal(new Dictionary<string, long> { ["Engine"] = 600_000 }, p.LastWeek());
        Assert.Equal(3_900_000, p.Today().Values.Sum());
        Assert.Equal("2026-11-02", p.WeekOf(now));

        var reloaded = new ProjectMap(_rig.Store.Layout.Projects);
        Assert.Equal("Thesis", reloaded.ProjectOf("w1")); // survives a restart (and a refit: ids are stable)
        var card = Assert.Single(p.Cards());
        Assert.Equal("module.projects", card.Id);
        var rows = Assert.Single(_exports, e => e.Name == "project_time_weekly").Rows(null).ToList();
        Assert.Contains(rows, r => (string)r[1]! == "Thesis" && (double)r[2]! == 90.0);

        _rig.Act(new ActionRequest { Action = "project.map", Origin = Origin.Flyout, Target = new Dictionary<string, string> { ["workflow"] = "w2" }, Args = new Dictionary<string, string> { ["project"] = "" } });
        Assert.Equal(1_800_000 + 300_000, p.ThisWeek()["Unassigned"]);
    }

    [Fact]
    public void Forget_tracking_removes_the_project_map()
    {
        Assert.Contains(_rig.Store.Layout.Projects, new Forgetter(_rig.Store.Layout).PathsFor(ForgetScope.Tracking));
    }

    private sealed class FakeWindows : IEventSource<WindowEvent>
    {
        public Action<WindowEvent>? Post { get; private set; }

        public string Name => "fake-windows";

        public void Start(Action<WindowEvent> post) => Post = post;

        public void Stop() => Post = null;
    }

    private WindowEvent Win(WindowEventKind kind, nint hwnd, string exe, string title) =>
        new(kind, hwnd, new ProcKey(10, 1), 0, _rig.Clock.UtcNow) { Exe = exe, Title = Sensitive.Of(title) };

    [Fact]
    public void A_watched_window_gone_30_seconds_raises_window_missing_once_with_no_other_titles()
    {
        _rig.LoadRules();
        var source = new FakeWindows();
        var w = _rig.Add(new WindowWatch(Ctx(), source) { Watches = () => [new WindowWatchSpec("chrome.exe", "*Notebook*")] });
        Assert.True(w.Listening);
        source.Post!(Win(WindowEventKind.Foreground, 5, "chrome.exe", "Training run Notebook - Google Chrome"));
        _rig.Loop.RunPending();
        source.Post!(Win(WindowEventKind.TitleChanged, 5, "chrome.exe", "FixtureOtherTabTitle - Google Chrome"));
        _rig.Loop.RunPending();
        _rig.Advance(TimeSpan.FromSeconds(20));
        Assert.DoesNotContain(_rig.Rules.Evaluate(), o => o.Toast is not null);
        _rig.Advance(TimeSpan.FromSeconds(11));
        var toast = Assert.Single(_rig.Rules.Evaluate(), o => o.Toast is not null).Toast!;
        Assert.Equal("chrome window closed?", toast.Title);
        Assert.DoesNotContain("FixtureOtherTabTitle", toast.Title, StringComparison.Ordinal);
        Assert.True(w.Status.Single().Missing);
        _rig.Advance(TimeSpan.FromMinutes(5));
        Assert.DoesNotContain(_rig.Rules.Evaluate(), o => o.Toast is not null); // once

        w.Snapshot([("chrome.exe", "Training run Notebook")]);
        Assert.False(w.Status.Single().Missing);
        w.Snapshot([]);
        _rig.Advance(TimeSpan.FromSeconds(31));
        Assert.Single(_rig.Rules.Evaluate(), o => o.Toast is not null); // re-armed after it came back
        Assert.DoesNotContain(_rig.LogLines.Lines, l => l.Contains("Notebook", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_a_watch_no_window_events_are_taken_and_a_never_seen_window_is_not_missing()
    {
        var source = new FakeWindows();
        var none = _rig.Add(new WindowWatch(Ctx(), source));
        Assert.False(none.Listening);
        Assert.Null(source.Post);

        var w = new WindowWatch(Ctx(), new FakeWindows()) { Watches = () => [new WindowWatchSpec("excel.exe", "*budget*")] };
        _rig.Add(w);
        w.Snapshot([("code.exe", "x")]);
        _rig.Advance(TimeSpan.FromMinutes(1));
        Assert.False(w.Status.Single().Missing);
    }
}
