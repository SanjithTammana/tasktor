namespace Tasktor.Core.Shapes;

/// <summary>
/// Turns a cumulative counter (bytes, CPU time) into a per-second rate (A D-A5). The first read has no rate;
/// a counter that goes backwards (reset, PID reuse) restarts.
/// </summary>
public sealed class RateCounter
{
    private double _last;
    private DateTimeOffset _lastAt;
    private bool _has;

    public double? Rate { get; private set; }

    public double? Update(double cumulative, DateTimeOffset at)
    {
        if (!_has || cumulative < _last || at <= _lastAt)
        {
            if (!_has || cumulative < _last)
            {
                Rate = null;
            }

            _last = cumulative;
            _lastAt = at;
            _has = true;
            return Rate;
        }

        Rate = (cumulative - _last) / (at - _lastAt).TotalSeconds;
        _last = cumulative;
        _lastAt = at;
        return Rate;
    }

    public void Reset()
    {
        _has = false;
        Rate = null;
    }
}
