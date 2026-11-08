using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Snapshot;
using Tasktor.Core.Shapes;

namespace Tasktor.Core.Learning;

/// <summary>
/// A learned threshold with a fixed fallback, a clamp and a trust ramp (B D-B5 / D-B9; reused by A5, A6):
/// <c>T = clamp(w·L + (1−w)·fallback, min, max)</c>, <c>L = multiplier × max(ring)</c> (the p90 once n ≥ 10),
/// <c>w = clamp((n − rampStart) / (rampFull − rampStart), 0, 1)</c>, n = good runs. Every result carries its
/// source (default / blended / learned / fallback) and basis, so a toast can explain it.
/// </summary>
public sealed class LearnedThreshold
{
    public required string Policy { get; init; }

    public required TimeSpan Fallback { get; init; }

    public TimeSpan Min { get; init; } = TimeSpan.Zero;

    public TimeSpan Max { get; init; } = TimeSpan.MaxValue;

    public double Multiplier { get; init; } = 1.5;

    public int RampStart { get; init; } = 5;

    public int RampFull { get; init; } = 20;

    /// <summary>From this many good runs on, the p90 replaces the max.</summary>
    public int QuantileFromRuns { get; init; } = 10;

    public double Quantile { get; init; } = 0.9;

    /// <summary>Which ring of the stats this policy learns from (seconds).</summary>
    public Func<KindStats, RingBuffer<float>> Ring { get; init; } = s => s.QuietMax;

    /// <summary>The trust weight for <paramref name="goodRuns"/>.</summary>
    public double Weight(int goodRuns) =>
        RampFull <= RampStart ? (goodRuns >= RampFull ? 1 : 0) : Math.Clamp((goodRuns - RampStart) / (double)(RampFull - RampStart), 0, 1);

    /// <summary>The current threshold in seconds, with its source and basis.</summary>
    public LearnedValue Current(KindStats? stats, bool learningOn = true, bool outOfDistribution = false)
    {
        var fallbackS = Clamp(Fallback.TotalSeconds);
        if (stats is null || !learningOn)
        {
            return new LearnedValue(fallbackS, ValueSource.Default, stats?.GoodRuns ?? 0, Policy + ".fallback", Fallback.TotalSeconds);
        }

        var n = stats.GoodRuns;
        if (stats.Mode == PolicyMode.Fallback)
        {
            return new LearnedValue(fallbackS, ValueSource.FallbackGuardrail, n, Policy + ".fallback", Fallback.TotalSeconds);
        }

        if (outOfDistribution)
        {
            return new LearnedValue(fallbackS, ValueSource.FallbackOutOfRange, n, Policy + ".fallback", Fallback.TotalSeconds);
        }

        var w = Weight(n);
        var ring = Ring(stats);
        if (w <= 0 || ring.Count == 0)
        {
            return new LearnedValue(fallbackS, ValueSource.Default, n, Policy + ".fallback", Fallback.TotalSeconds);
        }

        var useQuantile = n >= QuantileFromRuns;
        var basis = useQuantile ? WindowStats.Quantile(ring, Quantile) : WindowStats.Max(ring);
        var learned = Multiplier * basis;
        var t = Clamp((w * learned) + ((1 - w) * Fallback.TotalSeconds));
        var source = w >= 1 ? ValueSource.Learned : ValueSource.Blended;
        return new LearnedValue(t, source, n, Policy + (useQuantile ? ".p90" : ".max"), basis);
    }

    private double Clamp(double seconds) => Math.Clamp(seconds, Min.TotalSeconds, Max == TimeSpan.MaxValue ? double.MaxValue : Max.TotalSeconds);
}
