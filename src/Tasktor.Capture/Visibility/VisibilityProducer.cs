using System.Diagnostics;
using Tasktor.Capture.Deep;
using Tasktor.Contracts.Presets;
using Tasktor.Core.Windows;

namespace Tasktor.Capture.Visibility;

/// <summary>
/// Reads the desktop's geometry for C19.2: monitors (physical pixels), top-level windows front to back and the foreground
/// window. Numbers and the app (from the pid) only: an implementation never reads a title.
/// </summary>
public interface IWindowGeometry
{
    IReadOnlyList<RectPx> Monitors();

    IReadOnlyList<WindowInfo> Windows();

    long Foreground();
}

/// <summary>
/// C19.2 producer: top-level window changes (foreground, show / hide, minimize / restore, move and resize) are debounced
/// (<see cref="DebounceMs"/> after the last one, at most <see cref="MaxDelayMs"/> after the first), then the windows are
/// enumerated and <see cref="VisibleWindowLogic"/> computes the set; only a set that differs from the last one is posted.
/// Single-threaded: the event hook thread calls <see cref="Changed"/> and <see cref="Tick"/>.
/// </summary>
public sealed class VisibilityProducer(IWindowGeometry geometry, Action<VisibleSet> post, Func<DateTimeOffset> now)
{
    public const long DebounceMs = 250;
    public const long MaxDelayMs = 1000;

    private const uint EventSystemForeground = 0x0003, EventSystemMoveSizeEnd = 0x000B, EventSystemMinimizeStart = 0x0016,
        EventSystemMinimizeEnd = 0x0017, EventObjectShow = 0x8002, EventObjectHide = 0x8003, EventObjectLocationChange = 0x800B;

    private readonly IWindowGeometry _geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));
    private readonly Action<VisibleSet> _post = post ?? throw new ArgumentNullException(nameof(post));
    private readonly Func<DateTimeOffset> _now = now ?? throw new ArgumentNullException(nameof(now));
    private long? _first, _last;
    private VisibleSet? _posted;

    /// <summary>When the pending change is due (hook-clock ms), or null when nothing is pending.</summary>
    public long? DueAtMs => _first is { } first && _last is { } last ? Math.Min(last + DebounceMs, first + MaxDelayMs) : null;

    public long Posted { get; private set; }

    /// <summary>
    /// Whether a WinEvent counts: the system events for any window; show, hide and location changes only for a top-level
    /// window itself (<c>OBJID_WINDOW</c>, no child id), so the cursor, the caret and child controls never wake it.
    /// <paramref name="isTopLevel"/> is asked only when needed.
    /// </summary>
    public static bool Counts(uint winEvent, int idObject, int idChild, nint hwnd, Func<nint, bool> isTopLevel)
    {
        ArgumentNullException.ThrowIfNull(isTopLevel);
        return winEvent switch
        {
            EventSystemForeground or EventSystemMoveSizeEnd or EventSystemMinimizeStart or EventSystemMinimizeEnd => true,
            EventObjectShow or EventObjectHide or EventObjectLocationChange => idObject == 0 && idChild == 0 && isTopLevel(hwnd),
            _ => false,
        };
    }

    public void Changed(long ms)
    {
        _first ??= ms;
        _last = ms;
    }

    /// <summary>Compute and post when due; true when it computed.</summary>
    public bool Tick(long ms)
    {
        if (DueAtMs is not { } due || ms < due) return false;
        _first = _last = null;
        var set = Recompute();
        if (_posted is null || !Same(_posted, set))
        {
            _posted = set;
            Posted++;
            _post(set);
        }

        return true;
    }

    public VisibleSet Recompute() => VisibleWindowLogic.Compute(_now(), _geometry.Monitors(), _geometry.Windows(), _geometry.Foreground());

    private static bool Same(VisibleSet a, VisibleSet b) => a.ScreenPx == b.ScreenPx && a.Windows.SequenceEqual(b.Windows);
}

/// <summary>
/// The #7 timing check: real enumeration plus geometry, <c>runs</c> times, timed apart. Only timings and window counts
/// come out, never a title or an app.
/// </summary>
public static class VisibilityBench
{
    public const double TargetUs = 1000;

    public readonly record struct Timings(double P50Us, double P95Us, double P99Us, double MaxUs)
    {
        public override string ToString() => FormattableString.Invariant($"p50 {P50Us:0.0} µs, p95 {P95Us:0.0} µs, p99 {P99Us:0.0} µs, max {MaxUs:0.0} µs");
    }

    public sealed record Result(int Runs, int WindowsMax, Timings Enumerate, Timings Geometry, Timings Total)
    {
        public bool WithinTarget => Total.P95Us < TargetUs;

        public override string ToString() =>
            $"runs {Runs}, windows ≤ {WindowsMax}\nenumerate: {Enumerate}\ngeometry:  {Geometry}\ntotal:     {Total} (target p95 < {TargetUs} µs)";
    }

    public static Result Run(IWindowGeometry geometry, int runs)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentOutOfRangeException.ThrowIfLessThan(runs, 1);
        var enumerate = new long[runs];
        var compute = new long[runs];
        var total = new long[runs];
        var windows = 0;
        for (var i = 0; i < runs; i++)
        {
            var t0 = Stopwatch.GetTimestamp();
            var monitors = geometry.Monitors();
            var list = geometry.Windows();
            var fg = geometry.Foreground();
            var t1 = Stopwatch.GetTimestamp();
            VisibleWindowLogic.Compute(DateTimeOffset.UnixEpoch, monitors, list, fg);
            var t2 = Stopwatch.GetTimestamp();
            (enumerate[i], compute[i], total[i]) = (t1 - t0, t2 - t1, t2 - t0);
            windows = Math.Max(windows, list.Count);
        }

        return new Result(runs, windows, Of(enumerate), Of(compute), Of(total));
    }

    private static Timings Of(long[] ticks)
    {
        Array.Sort(ticks);
        double Us(long t) => t * 1_000_000.0 / Stopwatch.Frequency;
        double At(double p) => Us(ticks[Math.Clamp((int)Math.Ceiling(p * ticks.Length) - 1, 0, ticks.Length - 1)]);
        return new Timings(At(0.5), At(0.95), At(0.99), Us(ticks[^1]));
    }
}
