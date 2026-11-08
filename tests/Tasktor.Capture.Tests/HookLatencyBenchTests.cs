using Tasktor.Capture.Deep;
using Tasktor.Capture.Hook;

namespace Tasktor.Capture.Tests;

/// <summary>C19 acceptance 3 on synthetic input: the hook's per-event work stays within p99.9 ≤ 50 µs.</summary>
public sealed class HookLatencyBenchTests
{
    [Fact]
    public void The_mouse_callback_work_is_within_budget()
    {
        var ring = new InputRing();
        var result = HookLatencyBench.Run(new RingHookSink(ring), 20_000, hz: 0);
        Assert.True(result.WithinBudget, result.ToString());
        Assert.True(ring.Written > 20_000); // warm-up plus timed events all reached the ring
    }

    [Fact]
    public void Paced_runs_take_their_time_and_report_ordered_percentiles()
    {
        var started = Environment.TickCount64;
        var result = HookLatencyBench.Run(new RingHookSink(new InputRing()), 200, hz: 1000);
        Assert.True(Environment.TickCount64 - started >= 150);
        Assert.Equal(200, result.Samples);
        Assert.True(result.P50Us <= result.P99Us && result.P99Us <= result.P999Us && result.P999Us <= result.MaxUs);
    }
}

public sealed class HookDevicesTests
{
    [Fact]
    public void A_hook_needs_at_least_one_known_device()
    {
        var sink = new RingHookSink(new InputRing());
        Assert.Throws<ArgumentOutOfRangeException>(() => new LowLevelInputHook(sink, () => { }, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LowLevelInputHook(sink, () => { }, (HookDevices)4));
        using var mouse = new LowLevelInputHook(sink, () => { }, HookDevices.Mouse);
        if (!OperatingSystem.IsWindows()) Assert.False(mouse.Start()); // nothing to hook off Windows
    }
}
