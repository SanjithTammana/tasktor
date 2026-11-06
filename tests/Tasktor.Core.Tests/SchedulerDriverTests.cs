using Tasktor.Contracts.Platform;
using Tasktor.Core.Loop;
using Tasktor.Tests.Shared;

namespace Tasktor.Core.Tests;

/// <summary>The scheduler runs on the loop by itself: the first lease samples at once, then at the lease's interval.</summary>
public sealed class SchedulerDriverTests
{
    private readonly FakeClock _clock = new();
    private readonly LogicLoop _loop;
    private readonly Scheduler _scheduler;
    private readonly SchedulerDriver _driver;
    private int _runs;

    public SchedulerDriverTests()
    {
        _loop = new LogicLoop(_clock);
        _scheduler = new Scheduler(_clock);
        _scheduler.Register("system", _ => _runs++);
        _driver = new SchedulerDriver(_loop, _scheduler);
    }

    private void Advance(TimeSpan by)
    {
        _clock.Advance(by);
        _loop.RunDue();
    }

    [Fact]
    public void A_lease_samples_at_once_and_then_every_interval()
    {
        using var lease = _scheduler.Lease("system", TimeSpan.FromSeconds(60));
        _loop.RunDue();
        Assert.Equal(1, _runs);
        Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(1, _runs);
        Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, _runs);
        Advance(TimeSpan.FromSeconds(120));
        Assert.Equal(3, _runs); // a long gap (sleep) is one run, not a backlog
        Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(4, _runs);
    }

    [Fact]
    public void A_faster_lease_speeds_it_up_and_releasing_it_slows_it_down()
    {
        using var tick = _scheduler.Lease("system", TimeSpan.FromSeconds(60));
        _loop.RunDue();
        var live = _scheduler.Lease("system", TimeSpan.FromSeconds(1));
        _loop.RunDue();
        Assert.Equal(2, _runs); // the faster lease runs at once
        for (var i = 0; i < 5; i++)
        {
            Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(7, _runs);
        live.Dispose();
        Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(8, _runs); // the run already due a second out still happens once
        Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(8, _runs); // then back to the 60 s lease
        Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(9, _runs);
    }

    [Fact]
    public void Nothing_runs_without_a_lease_and_after_dispose()
    {
        Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(0, _runs);
        using var lease = _scheduler.Lease("system", TimeSpan.FromSeconds(10));
        _loop.RunDue();
        _driver.Dispose();
        Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(1, _runs);
    }
}
