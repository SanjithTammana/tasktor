using Tasktor.Contracts.Platform;
using Tasktor.Core.Loop;
using Tasktor.Tests.Shared;

namespace Tasktor.Core.Tests;

public sealed class LoopTests
{
    [Fact]
    public void Posted_work_runs_in_order_and_only_on_the_loop()
    {
        var loop = new LogicLoop(new FakeClock());
        var seen = new List<int>();
        var ch = loop.CreateChannel<int>(seen.Add);
        ch.Post(1);
        ch.Post(2);
        loop.Post(() => seen.Add(3));
        Assert.Empty(seen); // sources only enqueue
        Assert.Equal(3, loop.RunPending());
        Assert.Equal([1, 2, 3], seen);
        Assert.Equal(2, ch.Posted);
    }

    [Fact]
    public void Sources_on_other_threads_only_enqueue()
    {
        var loop = new LogicLoop(new FakeClock());
        var total = 0;
        var ch = loop.CreateChannel<int>(v => total += v);
        Parallel.For(0, 1000, _ => ch.Post(1));
        loop.RunPending();
        Assert.Equal(1000, total);
    }

    [Fact]
    public void Timers_fire_when_due_and_skip_missed_periods()
    {
        var clock = new FakeClock();
        var loop = new LogicLoop(clock);
        var ticks = 0;
        var once = 0;
        loop.Every(TimeSpan.FromSeconds(60), () => ticks++);
        loop.At(clock.UtcNow + TimeSpan.FromSeconds(90), () => once++);
        loop.RunDue();
        Assert.Equal(0, ticks);
        clock.Advance(TimeSpan.FromSeconds(60));
        loop.RunDue();
        Assert.Equal(1, ticks);
        clock.Advance(TimeSpan.FromSeconds(30));
        loop.RunDue();
        Assert.Equal(1, once);
        clock.Advance(TimeSpan.FromHours(3)); // e.g. sleep: one tick, not 180
        loop.RunDue();
        Assert.Equal(2, ticks);
        Assert.Equal(1, once);
    }

    [Fact]
    public void Disposed_timer_stops()
    {
        var clock = new FakeClock();
        var loop = new LogicLoop(clock);
        var ticks = 0;
        var t = loop.Every(TimeSpan.FromSeconds(1), () => ticks++);
        clock.Advance(TimeSpan.FromSeconds(1));
        loop.RunDue();
        t.Dispose();
        clock.Advance(TimeSpan.FromSeconds(5));
        loop.RunDue();
        Assert.Equal(1, ticks);
        Assert.Null(loop.NextDue);
    }

    [Fact]
    public void A_failing_item_does_not_stop_the_loop()
    {
        var errors = new List<Exception>();
        var loop = new LogicLoop(new FakeClock(), errors.Add);
        var ran = false;
        loop.Post(() => throw new InvalidOperationException("boom"));
        loop.Post(() => ran = true);
        loop.RunPending();
        Assert.True(ran);
        Assert.Single(errors);
    }

    [Fact]
    public async Task Thread_mode_runs_posted_work_and_timers()
    {
        using var loop = new LogicLoop(Tasktor.Core.Time.SystemClock.Instance);
        loop.Start();
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loop.At(DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(50), () => fired.TrySetResult());
        var v = await loop.InvokeAsync(() => 41 + 1);
        Assert.Equal(42, v);
        await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}

public sealed class LoopShutdownTests
{
    [Fact]
    public async Task TryStop_reports_whether_the_thread_exited_and_Completion_waits_for_a_blocked_callback()
    {
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var loop = new LogicLoop(new FakeClock());
        loop.Start("test-loop");
        loop.Post(() =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        Assert.False(loop.TryStop(TimeSpan.FromMilliseconds(100))); // the callback still holds the thread
        Assert.False(loop.Completion.IsCompleted);

        release.Set();
        await loop.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(loop.TryStop(TimeSpan.Zero)); // already gone: true at once
        loop.Dispose();
    }

    [Fact]
    public void A_loop_never_started_is_complete_once_stopped_and_stop_from_its_own_thread_is_not_a_proof()
    {
        var idle = new LogicLoop(new FakeClock());
        Assert.False(idle.Completion.IsCompleted);
        Assert.True(idle.TryStop(TimeSpan.Zero));
        Assert.True(idle.Completion.IsCompleted);

        var loop = new LogicLoop(new FakeClock());
        loop.Start();
        var fromInside = loop.InvokeAsync(() => loop.TryStop(TimeSpan.FromSeconds(1))).GetAwaiter().GetResult();
        Assert.False(fromInside); // a thread cannot prove its own exit
        Assert.True(loop.Completion.Wait(TimeSpan.FromSeconds(5)));
    }
}

public sealed class SchedulerTests
{
    private readonly FakeClock _clock = new();
    private readonly List<ColumnSet> _runs = [];
    private int _released;
    private readonly Scheduler _s;

    public SchedulerTests()
    {
        _s = new Scheduler(_clock);
        _s.Register("procs", _runs.Add, () => _released++);
    }

    [Fact]
    public void First_lease_runs_at_once_then_at_its_interval()
    {
        using var l = _s.Lease("procs", TimeSpan.FromSeconds(60), ColumnSet.Names);
        Assert.Equal(1, _s.Tick());
        Assert.Equal(0, _s.Tick());
        _clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(0, _s.Tick());
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, _s.Tick());
    }

    [Fact]
    public void Runs_at_the_shortest_interval_with_the_union_of_columns()
    {
        using var tick = _s.Lease("procs", TimeSpan.FromSeconds(60), ColumnSet.Names);
        _s.Tick();
        using var viewer = _s.Lease("procs", TimeSpan.FromSeconds(1), ColumnSet.Usage);
        Assert.Equal(TimeSpan.FromSeconds(1), _s.Interval("procs"));
        Assert.Equal(ColumnSet.Names | ColumnSet.Usage, _s.Columns("procs"));
        _s.Tick();
        Assert.Equal(ColumnSet.Names | ColumnSet.Usage, _runs[^1]);
        for (var i = 0; i < 5; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            _s.Tick();
        }

        Assert.Equal(7, _runs.Count);
    }

    [Fact]
    public void Closing_the_last_viewer_stops_fast_sampling_within_one_interval()
    {
        // A1 acceptance 6 (sample counter): 1 s sampling stops; only the 60 s tick remains.
        using var tick = _s.Lease("procs", TimeSpan.FromSeconds(60), ColumnSet.Names);
        _s.Tick();
        var viewer = _s.Lease("procs", TimeSpan.FromSeconds(1), ColumnSet.All);
        for (var i = 0; i < 10; i++)
        {
            _s.Tick();
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        viewer.Dispose();
        var before = _s.Runs("procs");
        for (var i = 0; i < 30; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            _s.Tick();
        }

        Assert.True(_s.Runs("procs") - before <= 1);
        Assert.Equal(TimeSpan.FromSeconds(60), _s.Interval("procs"));
        Assert.Equal(ColumnSet.Names, _s.Columns("procs"));
    }

    [Fact]
    public void Zero_leases_stop_the_sampler_and_free_its_buffers()
    {
        var a = _s.Lease("procs", TimeSpan.FromSeconds(2));
        var b = _s.Lease("procs", TimeSpan.FromSeconds(2));
        a.Dispose();
        Assert.Equal(0, _released);
        b.Dispose();
        b.Dispose();
        Assert.Equal(1, _released);
        Assert.Null(_s.NextDue);
        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(0, _s.Tick());
    }
}

public sealed class GateTests
{
    [Fact]
    public void HoldFor_ignores_a_short_spike()
    {
        // A6 acceptance 2: a 10 s spike above the threshold raises nothing for a 30 s hold.
        var clock = new FakeClock();
        var hold = new HoldFor(TimeSpan.FromSeconds(30));
        for (var s = 0; s < 10; s++)
        {
            Assert.False(hold.Update(true, clock.UtcNow));
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.False(hold.Update(false, clock.UtcNow));
        for (var s = 0; s <= 30; s++)
        {
            var fired = hold.Update(true, clock.UtcNow);
            Assert.Equal(s >= 30, fired);
            clock.Advance(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public void Cooldown_fires_at_most_once_per_period()
    {
        var clock = new FakeClock();
        var c = new Cooldown(TimeSpan.FromMinutes(30));
        Assert.True(c.TryFire(clock.UtcNow));
        clock.Advance(TimeSpan.FromMinutes(29));
        Assert.False(c.TryFire(clock.UtcNow));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(c.TryFire(clock.UtcNow));
    }
}
