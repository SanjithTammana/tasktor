namespace Tasktor.Capture.Indicator;

/// <summary>The complete recording indication, including the tray and all monitor edges.</summary>
public interface IRecordingIndicator : IDisposable
{
    /// <summary>Returns true only after every required indicator is visible.</summary>
    bool Start();
}

/// <summary>Starts capture only after its indicators, and stops capture before removing them.</summary>
public sealed class IndicatorGate(IRecordingIndicator indicator, Func<IDisposable> startCapture) : IDisposable
{
    private IDisposable? _capture;
    private bool _attempted;
    private bool _disposed;

    public bool Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_attempted)
        {
            throw new InvalidOperationException("Indicator startup was already attempted.");
        }

        _attempted = true;
        try
        {
            if (!indicator.Start())
            {
                Dispose();
                return false;
            }

            _capture = startCapture() ?? throw new InvalidOperationException("Capture did not start.");
            return true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // Keep the signal visible if capture could not be stopped; a later Dispose can retry.
        _capture?.Dispose();
        _capture = null;
        indicator.Dispose();
        _disposed = true;
    }
}

