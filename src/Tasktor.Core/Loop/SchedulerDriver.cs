namespace Tasktor.Core.Loop;

/// <summary>
/// Runs the <see cref="Scheduler"/> on the logic loop: one loop timer armed at <see cref="Scheduler.NextDue"/>, re-armed
/// after each tick and whenever a lease is taken or released. Without it nothing samples (the agent shipped without one
/// until a Windows check found an empty System card; the test rig ticked the scheduler by hand). Loop-only.
/// </summary>
public sealed class SchedulerDriver : IDisposable
{
    private readonly LogicLoop _loop;
    private readonly Scheduler _scheduler;
    private IDisposable? _timer;
    private DateTimeOffset? _armedFor;
    private bool _disposed;

    public SchedulerDriver(LogicLoop loop, Scheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(scheduler);
        _loop = loop;
        _scheduler = scheduler;
        _scheduler.Changed += Arm;
        Arm();
    }

    /// <summary>How many times the scheduler has been ticked (for tests and the self-use counters).</summary>
    public long Ticks { get; private set; }

    public void Dispose()
    {
        _disposed = true;
        _scheduler.Changed -= Arm;
        _timer?.Dispose();
        _timer = null;
    }

    private void Arm()
    {
        if (_disposed)
        {
            return;
        }

        var due = _scheduler.NextDue;
        if (due == _armedFor && _timer is not null)
        {
            return; // already waiting for that moment
        }

        _timer?.Dispose();
        _timer = null;
        _armedFor = due;
        if (due is { } at)
        {
            _timer = _loop.At(at, () =>
            {
                _timer = null;
                _armedFor = null;
                _scheduler.Tick();
                Ticks++;
                Arm();
            });
        }
    }
}
