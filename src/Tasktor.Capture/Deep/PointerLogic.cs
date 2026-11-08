namespace Tasktor.Capture.Deep;

/// <summary>
/// A shaped pointer event; times use the hook's monotonic millisecond clock. <see cref="Target"/> is a click's resolved
/// target: it arrives after the click (C19.1, resolved off the loop), as its own event at the click's time.
/// </summary>
public enum PointerEventKind : byte { Move, Click, Wheel, Target }

public sealed record PointerEvent(PointerEventKind Kind, long TimeMs, int X, int Y, byte Button = 0, int WheelDelta = 0, string? Target = null);

/// <summary>
/// Shapes pointer movement (≤ 10 samples a second) and decodes clicks and wheel input on the capture logic thread. Never
/// inspects a window: a click's target is resolved off the loop afterwards (<see cref="DeepCapture.ClickTarget"/>).
/// </summary>
public sealed class PointerLogic
{
    public const int MinMoveGapMs = 100;
    private long? _lastMove;

    public PointerEvent? Process(RawInput input)
    {
        if (input.Kind != RawInputKind.Mouse) return null;
        if (input.MouseMessage == 0x0200)
        {
            if (_lastMove is { } last && input.TimeMs >= last && input.TimeMs - last < MinMoveGapMs) return null;
            _lastMove = input.TimeMs;
            return new(PointerEventKind.Move, input.TimeMs, input.MouseX, input.MouseY);
        }

        var button = input.MouseMessage switch { 0x0201 => (byte)1, 0x0204 => (byte)2, 0x0207 => (byte)3, 0x020b => (byte)4, _ => (byte)0 };
        if (button != 0)
            return new(PointerEventKind.Click, input.TimeMs, input.MouseX, input.MouseY, button);
        if (input.MouseMessage is 0x020a or 0x020e)
            return new(PointerEventKind.Wheel, input.TimeMs, input.MouseX, input.MouseY, WheelDelta: unchecked((short)(input.MouseData >> 16)));
        return null;
    }
}

/// <summary>
/// C19.3: what is kept of one activity's pointer input after the raw path's 7 days. Speeds are px/s between shaped
/// samples; a gap over <see cref="PointerFeatureBuilder.RestGapMs"/> is a rest, not movement. Dwell share is the share of
/// the activity the pointer was at rest.
/// </summary>
public sealed record PointerFeatures(double PathLengthPx, double SpeedP50, double SpeedP90, double ClicksPerMinute, double ScrollsPerMinute, double DwellShare);

/// <summary>Accumulates <see cref="PointerFeatures"/> for one activity, in bounded memory. Logic thread only.</summary>
public sealed class PointerFeatureBuilder
{
    public const long RestGapMs = 1000;
    public const int MaxSpeedSamples = 4096;

    private readonly List<double> _speeds = [];
    private int _stride = 1, _skip;
    private (long Ms, int X, int Y)? _last;
    private double _path;
    private long _movingMs;
    private int _clicks, _scrolls;
    private bool _any;

    public int SpeedSamples => _speeds.Count;

    public void Add(PointerEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        switch (e.Kind)
        {
            case PointerEventKind.Click: _clicks++; _any = true; return;
            case PointerEventKind.Wheel: _scrolls++; _any = true; return;
            case PointerEventKind.Move: break;
            default: return;
        }

        _any = true;
        if (_last is { } last && e.TimeMs > last.Ms)
        {
            var dx = (double)e.X - last.X;
            var dy = (double)e.Y - last.Y;
            var d = Math.Sqrt((dx * dx) + (dy * dy));
            _path += d;
            var gap = e.TimeMs - last.Ms;
            if (gap <= RestGapMs)
            {
                _movingMs += gap;
                Speed(d * 1000 / gap);
            }
        }

        _last = (e.TimeMs, e.X, e.Y);
    }

    /// <summary>The features over an activity of <paramref name="duration"/>, or null when it had no pointer input.</summary>
    public PointerFeatures? Build(TimeSpan duration)
    {
        if (!_any) return null;
        var minutes = Math.Max(duration.TotalMinutes, 1.0 / 60);
        var sorted = _speeds.Order().ToArray();
        var dwell = 1 - Math.Clamp(_movingMs / Math.Max(duration.TotalMilliseconds, 1), 0, 1);
        return new(_path, Percentile(sorted, 0.5), Percentile(sorted, 0.9), _clicks / minutes, _scrolls / minutes, dwell);
    }

    // Nearest rank; 0 with no samples.
    private static double Percentile(double[] sorted, double p) =>
        sorted.Length == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];

    // Bounded: when full, keep every other sample and take one in twice as many from then on (an even thin-out over time).
    private void Speed(double v)
    {
        if (_skip++ % _stride != 0) return;
        if (_speeds.Count >= MaxSpeedSamples)
        {
            for (var i = 0; i < _speeds.Count / 2; i++) _speeds[i] = _speeds[i * 2];
            _speeds.RemoveRange(_speeds.Count / 2, _speeds.Count - (_speeds.Count / 2));
            _stride *= 2;
            _skip = 1;
        }

        _speeds.Add(v);
    }
}
