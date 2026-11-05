namespace Tasktor.Core.Loop;

/// <summary>"At most once per X" (A D-A5 Event). O(1).</summary>
public sealed class Cooldown(TimeSpan period)
{
    private DateTimeOffset? _last;

    public TimeSpan Period { get; set; } = period;

    public DateTimeOffset? LastFired => _last;

    public bool Ready(DateTimeOffset now) => _last is null || now - _last.Value >= Period;

    /// <summary>Fire if ready; returns whether it fired.</summary>
    public bool TryFire(DateTimeOffset now)
    {
        if (!Ready(now))
        {
            return false;
        }

        _last = now;
        return true;
    }

    public void Reset() => _last = null;
}

/// <summary>"Only if true for D": a running "true since" timestamp, O(1) time and space (A's rule table).</summary>
public sealed class HoldFor(TimeSpan duration)
{
    private DateTimeOffset? _since;

    public TimeSpan Duration { get; set; } = duration;

    public DateTimeOffset? Since => _since;

    /// <summary>Feed the condition's current value; returns true once it has held for <see cref="Duration"/>.</summary>
    public bool Update(bool condition, DateTimeOffset now)
    {
        if (!condition)
        {
            _since = null;
            return false;
        }

        _since ??= now;
        return now - _since.Value >= Duration;
    }

    public void Reset() => _since = null;
}
