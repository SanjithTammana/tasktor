namespace Tasktor.Capture.Indicator;

/// <summary>Host lifetime: a failed indicator refuses capture and a later indicator failure requests exit.</summary>
public static class IndicatorHost
{
    /// <param name="stop">False for a deliberate stop; true for an indicator failure.</param>
    public static async Task<int> RunAsync(IRecordingIndicator indicator, Func<IDisposable> startCapture, Task<bool> stop)
    {
        using var gate = new IndicatorGate(indicator, () => stop.IsCompleted ? NoCapture.Instance : startCapture());
        if (!gate.Start()) return 1;
        return await stop.ConfigureAwait(false) ? 1 : 0;
    }
    private sealed class NoCapture : IDisposable
    {
        public static NoCapture Instance { get; } = new();
        public void Dispose() { }
    }
}

