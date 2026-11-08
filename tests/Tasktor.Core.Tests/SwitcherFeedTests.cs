using System.Text.Json;
using Tasktor.Contracts;
using Tasktor.Contracts.Config;
using Tasktor.Contracts.Pipe;
using Tasktor.Contracts.Platform;
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
}
