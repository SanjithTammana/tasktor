using Tasktor.Contracts.Workflow;

namespace Tasktor.Capture.Deep;

/// <summary>
/// D-C1 in the capture host, so track 1's document closes while capture runs: an activity is a same-app run in front.
/// Another app in front for <see cref="GlanceMax"/> ends it (coming back sooner absorbs the glance; a third app starts its
/// own count), and an activity ends at <see cref="Cap"/>. The thresholds are the preregistered ones the agent's episodes
/// use, so documents line up with C's activities. Idle is not seen here: an idle activity ends at the cap or the next switch.
/// Logic thread only.
/// </summary>
public sealed class ActivityBoundary
{
    public static readonly TimeSpan GlanceMax = TimeSpan.FromMilliseconds(new EpisodeThresholds().GlanceMaxMs);

    public static readonly TimeSpan Cap = TimeSpan.FromMilliseconds(new EpisodeThresholds().ActivityCapMs);

    private string? _app;
    private DateTimeOffset _start;
    private (string App, DateTimeOffset Since)? _visit;

    /// <summary>The app in front now (null: not known). True when the activity ended.</summary>
    public bool Foreground(string? app, DateTimeOffset at)
    {
        if (app is null)
        {
            return Tick(at);
        }

        if (_app is null)
        {
            (_app, _start) = (app, at);
            return false;
        }

        if (Same(app, _app))
        {
            _visit = null;
        }
        else if (_visit is not { } v || !Same(v.App, app))
        {
            _visit = (app, at);
        }

        return Tick(at);
    }

    /// <summary>Time passes. True when the activity ended (a switch that lasted, or the cap).</summary>
    public bool Tick(DateTimeOffset at)
    {
        if (_visit is { } v && at - v.Since >= GlanceMax)
        {
            (_app, _start, _visit) = (v.App, v.Since, null);
            return true;
        }

        if (_app is not null && at - _start >= Cap)
        {
            _start = at;
            return true;
        }

        return false;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
