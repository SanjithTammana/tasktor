using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Presets;
using Tasktor.Contracts.Workflow;
using Tasktor.Core.Features.Windows;
using Tasktor.Core.Windows;
using Tasktor.Tests.Shared;

namespace Tasktor.Core.Tests;

/// <summary>U3 D-W5 on the one WinEventSource: launched windows matched on show events, user drags dropped, the lease held only while waiting.</summary>
public sealed class PresetEventTests : IDisposable
{
    private readonly FeatureRig _rig = new();
    private readonly Source _source = new();
    private readonly MonitorInfo _monitor = new("display", new RectPx(0, 0, 1000, 800), new RectPx(0, 0, 1000, 800), 96, true);
    private readonly WindowInfo _code = new() { Hwnd = 12, Pid = 7, AppId = "code.exe", FramePx = new RectPx(0, 0, 200, 200), WindowPx = new RectPx(0, 0, 200, 200) };
    private readonly List<WindowMove> _moves = [];
    private bool _open;
    private long _mono = 1_000;

    public void Dispose() => _rig.Dispose();

    /// <summary>The shared source as a feature sees it, plus the lifecycle lease count.</summary>
    private sealed class Source : IEventSource<WindowEvent>
    {
        private Action<WindowEvent>? _post;

        public int Leases { get; private set; }

        public int Taken { get; private set; }

        public string Name => "win-events";

        public void Start(Action<WindowEvent> post) => _post = post;

        public void Stop() => _post = null;

        public void Send(WindowEvent e) => _post?.Invoke(e);

        public IDisposable Lease()
        {
            Leases++;
            Taken++;
            return new Release(this);
        }

        private sealed class Release(Source owner) : IDisposable
        {
            private bool _done;

            public void Dispose()
            {
                if (!_done)
                {
                    _done = true;
                    owner.Leases--;
                }
            }
        }
    }

    private PresetRestorer Restorer()
    {
        var preset = new Preset { Name = "Work", Slots = [new PresetSlot { App = new PresetApp { AppId = "code.exe" }, Place = new PresetPlace { Monitor = "display", Rect = new DipRect { X = 50, Y = 50, W = 300, H = 300 } }, Launch = new PresetLaunch { Exe = "code.exe" } }] };
        new PresetStore(_rig.Context).Save(new PresetsFile { Presets = [preset] });
        var r = _rig.Add(new PresetRestorer(_rig.Context)
        {
            Events = _source,
            LeaseLifecycle = _source.Lease,
            Windows = () => _open ? [_code] : [],
            Monitors = () => [_monitor],
            Move = m => { _moves.Add(m); return true; },
            Launch = _ => true,
            Timestamp = () => _mono,
        });
        _rig.Pump();
        return r;
    }

    private ActionResult Apply() => _rig.Act(new ActionRequest { Action = "preset.apply", Origin = Origin.Flyout, Target = new Dictionary<string, string> { ["preset"] = "Work" } });

    private void Send(WindowEventKind kind, long hwnd) =>
        _source.Send(new WindowEvent(kind, (nint)hwnd, new ProcKey(7, 0), ++_mono, _rig.Clock.UtcNow));

    [Fact]
    public void A_window_shown_after_the_catch_up_pass_is_matched_on_its_show_event()
    {
        Restorer();
        Assert.Equal(ActionStatus.Ok, Apply().Status);
        Assert.Equal(1, _source.Leases); // taken before the launch
        _rig.Advance(TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(100)); // catch-up pass ran: nothing yet
        Assert.Empty(_moves);

        _open = true;
        _rig.Advance(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(100)); // no event, no poll: still untouched
        Assert.Empty(_moves);

        Send(WindowEventKind.Shown, 12);
        _rig.Advance(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(100));
        var move = Assert.Single(_moves);
        Assert.Equal(new RectPx(50, 50, 300, 300), move.Rect);
        Assert.Equal(0, _source.Leases); // every slot matched: the lease is released at once
    }

    [Fact]
    public void A_window_the_user_drags_during_the_restore_is_dropped()
    {
        Restorer();
        Apply();
        _open = true;
        Send(WindowEventKind.MoveSizeStart, 12); // the user grabs the new window before Tasktor places it
        Send(WindowEventKind.Shown, 12);
        _rig.Advance(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100));
        Assert.Empty(_moves);
        Assert.Equal(0, _source.Leases); // the slot is dropped, so nothing is left to wait for
    }

    [Fact]
    public void The_lease_ends_at_the_deadline_and_when_a_new_restore_replaces_the_wait()
    {
        Restorer();
        Apply();
        Assert.Equal(1, _source.Leases);
        Apply(); // a new restore cancels the first wait (and its lease) and takes its own
        Assert.Equal(1, _source.Leases);
        Assert.Equal(2, _source.Taken);
        _rig.Advance(TimeSpan.FromSeconds(21), TimeSpan.FromSeconds(1));
        Assert.Equal(0, _source.Leases);
        _open = true;
        Send(WindowEventKind.Shown, 12);
        _rig.Advance(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100));
        Assert.Empty(_moves); // after 20 s a new window is left alone
    }

    [Fact]
    public void An_arrange_takes_no_lease_and_disable_stops_the_events()
    {
        var r = Restorer();
        _open = true;
        var arranged = _rig.Act(new ActionRequest { Action = "preset.arrange", Origin = Origin.Rule, Rule = "focus", Target = new Dictionary<string, string> { ["preset"] = "Work" } });
        Assert.Equal(ActionStatus.Ok, arranged.Status);
        Assert.Single(_moves);
        Assert.Equal(0, _source.Taken);

        Apply();
        r.Disable();
        Assert.Equal(0, _source.Leases);
    }

    [Fact]
    public void Interventions_count_moves_sizes_and_closes_only()
    {
        var t = new WindowInterventions();
        WindowEvent E(WindowEventKind k, long h, long at) => new(k, (nint)h, new ProcKey(1, 0), at, DateTimeOffset.UnixEpoch);
        t.On(E(WindowEventKind.Shown, 1, 10));
        t.On(E(WindowEventKind.Foreground, 1, 11));
        Assert.False(t.ChangedSince(1, 0));

        t.On(E(WindowEventKind.MoveSizeStart, 1, 20));
        Assert.True(t.ChangedSince(1, 100)); // still being dragged
        t.On(E(WindowEventKind.MoveSizeEnd, 1, 30));
        Assert.True(t.ChangedSince(1, 25));
        Assert.False(t.ChangedSince(1, 31));

        t.On(E(WindowEventKind.Destroyed, 2, 40));
        Assert.True(t.ChangedSince(2, 40));
        Assert.False(t.ChangedSince(3, 0));
    }
}
