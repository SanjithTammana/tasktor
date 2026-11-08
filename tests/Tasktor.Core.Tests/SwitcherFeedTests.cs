using System.Text.Json;
using Tasktor.Contracts;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Config;
using Tasktor.Contracts.Pipe;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Snapshot;
using Tasktor.Contracts.Windows;
using Tasktor.Contracts.Workflow;
using Tasktor.Core.Features;
using Tasktor.Core.Features.Windows;
using Tasktor.Tests.Shared;

namespace Tasktor.Core.Tests;

/// <summary>F2 (U3 D-W4): the agent feeds the switcher the MRU order and job / frozen badges, by handle and pid only.</summary>
public sealed class SwitcherFeedTests : IDisposable
{
    private readonly FeatureRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private sealed class Windows : IEventSource<WindowEvent>
    {
        public Action<WindowEvent>? Post { get; private set; }

        public string Name => "fake-windows";

        public void Start(Action<WindowEvent> post) => Post = post;

        public void Stop() => Post = null;
    }

    private SwitcherFeed Pull() =>
        JsonSerializer.Deserialize(_rig.Pulls["switcher"](new PullMessage { Id = "1", Table = "switcher" })!, ContractsJsonContext.Default.SwitcherFeed)!;

    private WindowEvent Win(WindowEventKind kind, nint hwnd) =>
        new(kind, hwnd, new ProcKey(10, 1), 0, _rig.Clock.UtcNow) { Exe = "app.exe", Title = Sensitive.Of("FixtureTitleNeverFed") };

    [Fact]
    public void Foreground_events_order_the_windows_most_recent_first_and_closed_ones_leave()
    {
        var source = new Windows();
        _rig.Add(new SwitcherFeedFeature(_rig.Context, source));
        foreach (var h in new nint[] { 1, 2, 3, 2 })
        {
            source.Post!(Win(WindowEventKind.Foreground, h));
        }

        source.Post!(Win(WindowEventKind.Destroyed, 3));
        _rig.Loop.RunPending();

        var feed = Pull();
        Assert.Equal([2L, 1L], feed.Mru);
        Assert.DoesNotContain("FixtureTitleNeverFed", _rig.Pulls["switcher"](new PullMessage { Id = "2", Table = "switcher" })!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Jobs_and_frozen_jobs_are_badged_by_pid_and_settings_ride_along()
    {
        _rig.Add(new SwitcherFeedFeature(_rig.Context, new Windows())
        {
            Jobs = () => [new SwitcherJob { Id = "j1", Label = "training", Frozen = true, Pids = [40, 41] }],
            Settings = () => new WindowsSettings { Hotkey = "Ctrl+Alt+Q", ThisDesktopOnly = true },
        });

        var feed = Pull();
        var job = Assert.Single(feed.Jobs);
        Assert.Equal(("j1", true), (job.Id, job.Frozen));
        Assert.Equal([40u, 41u], job.Pids);
        Assert.Equal(("Ctrl+Alt+Q", true), (feed.Hotkey, feed.ThisDesktopOnly));
    }

    [Fact]
    public void The_order_is_bounded_and_defaults_to_alt_backtick()
    {
        var source = new Windows();
        _rig.Add(new SwitcherFeedFeature(_rig.Context, source));
        for (nint h = 1; h <= SwitcherFeedFeature.MaxWindows + 50; h++)
        {
            source.Post!(Win(WindowEventKind.Foreground, h));
        }

        _rig.Loop.RunPending();
        var feed = Pull();
        Assert.Equal(SwitcherFeedFeature.MaxWindows, feed.Mru.Count);
        Assert.Equal(SwitcherFeedFeature.MaxWindows + 50, feed.Mru[0]);
        Assert.Equal("Alt+`", feed.Hotkey);
    }

    private ActionResult Act(string action, Dictionary<string, string>? args = null, Origin origin = Origin.Flyout)
    {
        var r = _rig.Act(new ActionRequest { Action = action, Origin = origin, Args = args });
        _rig.Loop.RunPending();
        return r;
    }

    [Fact]
    public void The_module_shows_its_settings_in_the_snapshot_and_clears_them_when_off()
    {
        var feed = _rig.Add(new SwitcherFeedFeature(_rig.Context, new Windows()));
        var state = _rig.State.Current.Switcher!;
        Assert.Equal(("Alt+`", (bool?)null, false), (state.Keys, state.Registered, state.ThisDesktopOnly)); // registered: unknown until the shell says
        feed.Disable();
        Assert.Null(_rig.State.Current.Switcher);
    }

    [Fact]
    public void Settings_change_the_hotkey_and_this_desktop_only_and_refuse_bad_or_taken_keys()
    {
        _rig.Add(new SwitcherFeedFeature(_rig.Context, new Windows()));
        Assert.Equal(ActionStatus.Ok, Act("switcher.settings", new() { ["keys"] = "ctrl+alt+q" }).Status);
        Assert.Equal("Ctrl+Alt+Q", _rig.Settings.Current.Windows!.Hotkey);
        Assert.Equal("Ctrl+Alt+Q", _rig.State.Current.Switcher!.Keys);

        Assert.Equal(ActionStatus.Ok, Act("switcher.settings", new() { ["this_desktop_only"] = "true" }).Status);
        Assert.True(_rig.Settings.Current.Windows!.ThisDesktopOnly);
        Assert.Equal("Ctrl+Alt+Q", _rig.Settings.Current.Windows!.Hotkey); // the other setting is kept

        Assert.False(Pull().ReplaceAltTab); // off by default (D-W3)
        Assert.Equal(ActionStatus.Ok, Act("switcher.settings", new() { ["replace_alt_tab"] = "true" }).Status);
        Assert.True(_rig.Settings.Current.Windows!.ReplaceAltTab);
        Assert.True(Pull().ReplaceAltTab);
        Assert.True(_rig.State.Current.Switcher!.ReplaceAltTab);
        Assert.Equal(ActionStatus.Refused, Act("switcher.settings", new() { ["replace_alt_tab"] = "yes" }).Status);
        Assert.Equal(ActionStatus.Refused, Act("switcher.settings", new() { ["replace_alt_tab"] = "false" }, Origin.Rule).Status); // people only

        Assert.Equal(ActionStatus.Refused, Act("switcher.settings", new() { ["keys"] = "Q" }).Status); // needs Ctrl, Alt or Win
        Assert.Equal(ActionStatus.Refused, Act("switcher.settings", new() { ["keys"] = "Win+Alt+T" }).Status); // the command line's
        Assert.Equal(ActionStatus.Refused, Act("switcher.settings", new() { ["keys"] = "Alt+Q" }, Origin.Rule).Status); // people only
        Assert.Equal(ActionStatus.Refused, Act("switcher.settings", []).Status); // nothing to change
    }

    [Fact]
    public void Only_the_shell_reports_whether_its_hotkey_registered()
    {
        _rig.Add(new SwitcherFeedFeature(_rig.Context, new Windows()));
        Assert.Equal(ActionStatus.Ok, Act("switcher.status", new() { ["keys"] = "Alt+`", ["registered"] = "false" }, Origin.Shell).Status);
        Assert.False(_rig.State.Current.Switcher!.Registered);
        Assert.Equal(ActionStatus.Refused, Act("switcher.status", new() { ["keys"] = "Alt+`", ["registered"] = "true" }).Status); // not the shell
        Assert.False(_rig.State.Current.Switcher!.Registered);
        Act("switcher.status", new() { ["keys"] = "Ctrl+Alt+Z", ["registered"] = "true" }, Origin.Shell); // a stale report for other keys
        Assert.False(_rig.State.Current.Switcher!.Registered);
    }

    [Fact]
    public void Each_window_keeps_the_workflow_shown_when_it_was_last_in_front_never_an_app_fallback()
    {
        var source = new Windows();
        _rig.Add(new SwitcherFeedFeature(_rig.Context, source));
        void Front(nint hwnd, string? workflow, WorkflowLevel level = WorkflowLevel.Workflow)
        {
            _rig.Context.UpdateState(s => s with { Workflow = workflow is null ? null : new WorkflowState { Id = "w-" + workflow, Name = workflow, Level = level } });
            source.Post!(Win(WindowEventKind.Foreground, hwnd));
            _rig.Loop.RunPending();
        }

        Front(1, "Thesis");
        Front(2, "Email triage");
        Front(3, "chrome.exe", WorkflowLevel.App); // the app fallback is not a workflow
        Front(1, null); // no model shown now: the window keeps what it had
        var feed = Pull();
        Assert.Equal("Thesis", feed.Workflows[1]);
        Assert.Equal("Email triage", feed.Workflows[2]);
        Assert.False(feed.Workflows.ContainsKey(3));

        source.Post!(Win(WindowEventKind.Destroyed, 2));
        _rig.Loop.RunPending();
        Assert.False(Pull().Workflows.ContainsKey(2)); // a closed window is forgotten
    }
}
