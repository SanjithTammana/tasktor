using Tasktor.Capture.Indicator;

namespace Tasktor.Capture.Tests;

public sealed class IndicatorHostTests
{
    [Fact]
    public async Task Failure_to_create_indicators_exits_without_starting_capture()
    {
        var indicator = new FakeIndicator(false);
        var starts = 0;
        var result = await IndicatorHost.RunAsync(indicator, () => { starts++; return new Lease(); }, Task.FromResult(false));
        Assert.Equal(1, result);
        Assert.Equal(0, starts);
        Assert.True(indicator.Disposed);
    }

    [Fact]
    public async Task Stop_waits_to_remove_indicators_until_capture_has_stopped()
    {
        var indicator = new FakeIndicator(true);
        var lease = new Lease();
        var stop = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = IndicatorHost.RunAsync(indicator, () => lease, stop.Task);
        Assert.False(indicator.Disposed);
        stop.SetResult(true);
        Assert.Equal(1, await running);
        Assert.True(lease.Disposed);
        Assert.True(indicator.Disposed);
    }

    [Fact]
    public async Task Indicator_loss_before_capture_start_does_not_start_capture()
    {
        var indicator = new FakeIndicator(true);
        var starts = 0;
        var result = await IndicatorHost.RunAsync(indicator, () => { starts++; return new Lease(); }, Task.FromResult(true));
        Assert.Equal(1, result);
        Assert.Equal(0, starts);
    }
    private sealed class FakeIndicator(bool succeeds) : IRecordingIndicator
    {
        public bool Disposed { get; private set; }
        public bool Start() => succeeds;
        public void Dispose() => Disposed = true;
    }

    private sealed class Lease : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}

