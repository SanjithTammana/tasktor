using Tasktor.Capture.Indicator;

namespace Tasktor.Capture.Tests;

public sealed class IndicatorGateTests
{
    [Fact]
    public void Starts_capture_only_after_both_indicators_are_ready()
    {
        var events = new List<string>();
        using var gate = new IndicatorGate(new FakeIndicator(events, true), () => { events.Add("capture"); return new FakeLease(events); });
        Assert.True(gate.Start());
        Assert.Equal(new[] { "indicators", "capture" }, events);
    }

    [Fact]
    public void Failed_indicator_never_starts_capture_and_is_disposed()
    {
        var events = new List<string>();
        using var gate = new IndicatorGate(new FakeIndicator(events, false), () => { events.Add("capture"); return new FakeLease(events); });
        Assert.False(gate.Start());
        Assert.Equal(new[] { "indicators", "indicator disposed" }, events);
    }

    [Fact]
    public void Stopping_removes_capture_before_indicators()
    {
        var events = new List<string>();
        var gate = new IndicatorGate(new FakeIndicator(events, true), () => { events.Add("capture"); return new FakeLease(events); });
        Assert.True(gate.Start());
        gate.Dispose();
        Assert.Equal(new[] { "indicators", "capture", "capture disposed", "indicator disposed" }, events);
    }

    private sealed class FakeIndicator(List<string> events, bool succeeds) : IRecordingIndicator
    {
        public bool Start() { events.Add("indicators"); return succeeds; }
        public void Dispose() => events.Add("indicator disposed");
    }

    private sealed class FakeLease(List<string> events) : IDisposable
    {
        public void Dispose() => events.Add("capture disposed");
    }
}
