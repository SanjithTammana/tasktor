using System.Text;
using Tasktor.Contracts.Snapshot;
using Tasktor.Core.Learning;

namespace Tasktor.Core.Tests;

public sealed class LearnedThresholdTests
{
    private static readonly LearnedThreshold Silent = new()
    {
        Policy = "silent",
        Fallback = TimeSpan.FromMinutes(10),
        Min = TimeSpan.FromMinutes(2),
        Max = TimeSpan.FromMinutes(60),
    };

    private static KindStats WithRuns(int n, double quietMaxS)
    {
        var s = new KindStats();
        for (var i = 0; i < n; i++)
        {
            s.RecordRun(new RunOutcome { Good = true, QuietMaxS = quietMaxS, DurationS = 3600, CoresMedian = 1 });
        }

        return s;
    }

    [Fact]
    public void New_kind_uses_the_fallback()
    {
        // B adaptive acceptance 2: a new kind (n < 5) is flagged at 10 min.
        var v = Silent.Current(WithRuns(4, 12 * 60));
        Assert.Equal(600, v.Value);
        Assert.Equal(ValueSource.Default, v.Source);
    }

    [Fact]
    public void Twenty_runs_with_12_min_pauses_give_18_min()
    {
        var v = Silent.Current(WithRuns(20, 12 * 60));
        Assert.Equal(18 * 60, v.Value, 3);
        Assert.Equal(ValueSource.Learned, v.Source);
        Assert.True(v.Value > 12 * 60); // a 12-min pause is not flagged
    }

    [Fact]
    public void Twelve_runs_sit_between_fallback_and_learned()
    {
        var v = Silent.Current(WithRuns(12, 12 * 60));
        Assert.InRange(v.Value, 13.6 * 60, 13.8 * 60); // ≈ 13.7 min
        Assert.Equal(ValueSource.Blended, v.Source);
        Assert.Equal("silent.p90", v.BasisKey);
    }

    [Fact]
    public void Never_quiet_kind_is_clamped_to_two_minutes_once_learned()
    {
        // B adaptive acceptance 3.
        Assert.Equal(120, Silent.Current(WithRuns(20, 30)).Value);
        Assert.Equal(600, Silent.Current(WithRuns(4, 30)).Value);
    }

    [Fact]
    public void Learning_off_guardrail_and_out_of_range_use_the_fallback()
    {
        var stats = WithRuns(20, 12 * 60);
        Assert.Equal(ValueSource.Default, Silent.Current(stats, learningOn: false).Source);
        Assert.Equal(ValueSource.FallbackOutOfRange, Silent.Current(stats, outOfDistribution: true).Source);
        stats.Mode = PolicyMode.Fallback;
        var v = Silent.Current(stats);
        Assert.Equal(ValueSource.FallbackGuardrail, v.Source);
        Assert.Equal(600, v.Value);
    }
}

public sealed class KindStatsTests
{
    [Fact]
    public void Bytes_round_trip_and_size_is_about_300()
    {
        var s = new KindStats { LastSeenUtc = new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero) };
        for (var i = 0; i < 25; i++)
        {
            s.RecordRun(new RunOutcome { Good = i % 5 != 0, QuietMaxS = i, DurationS = i * 10, CoresMedian = 0.5 + i, LoweredByHand = i % 2 == 0 });
        }

        s.RecordAdmission(true);
        s.RecordAdmission(false);
        s.RecordAlarm(true, false);
        s.RecordAlarm(false, true);
        s.RecordFinishedToast(true);
        s.Mode = PolicyMode.Fallback;
        s.CleanRunsSinceReplace = 2;

        var bytes = s.ToBytes();
        Assert.InRange(bytes.Length, 250, 320);
        var t = KindStats.FromBytes(bytes);
        Assert.Equal(bytes, t.ToBytes());
        Assert.Equal(s.GoodRuns, t.GoodRuns);
        Assert.Equal(s.QuietMax.ToArray(), t.QuietMax.ToArray());
        Assert.Equal(PolicyMode.Fallback, t.Mode);
        Assert.Equal(s.RecentLowered, t.RecentLowered);
        Assert.Equal(s.LastSeenUtc, t.LastSeenUtc);
    }

    [Fact]
    public void Rings_hold_the_last_twenty_good_runs()
    {
        var s = new KindStats();
        for (var i = 0; i < 30; i++)
        {
            s.RecordRun(new RunOutcome { Good = true, DurationS = i });
        }

        Assert.Equal(20, s.Duration.Count);
        Assert.Equal(10, s.Duration[0]);
    }

    [Fact]
    public void Recent_windows_count_bits()
    {
        var s = new KindStats();
        for (var i = 0; i < 12; i++)
        {
            s.RecordAlarm(falseAlarm: i < 4, shadowFalse: false);
        }

        Assert.Equal(2, s.RecentFalseAlarms); // only the last 10 count
        Assert.Equal(4, s.FalseAlarms);
    }

    [Fact]
    public void Corrupt_bytes_are_rejected() => Assert.Throws<FormatException>(() => KindStats.FromBytes(new byte[10]));
}

public sealed class KeyedHasherTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void Same_input_same_id_domains_differ()
    {
        var a = new KeyedHasher(Key, "job.kind");
        var b = new KeyedHasher(Key, "volume");
        Assert.Equal(a.Hash("python.exe|train.py"), a.Hash("python.exe|train.py"));
        Assert.NotEqual(a.Hash("python.exe|train.py"), b.Hash("python.exe|train.py"));
        Assert.NotEqual(a.Hash("python.exe|train.py"), a.Hash("python.exe|embed.py"));
    }

    [Fact]
    public void Id_text_contains_no_name()
    {
        var id = new KeyedHasher(Key, "job.kind").Hash("python.exe|train.py").ToString();
        Assert.Equal(32, id.Length);
        Assert.DoesNotContain("train", id, StringComparison.OrdinalIgnoreCase);
        Assert.True(KindId.TryParse(id, out var back));
        Assert.Equal(id, back.ToString());
        Assert.Equal(-1, Convert.FromHexString(id).AsSpan().IndexOf(Encoding.UTF8.GetBytes("train")));
    }
}
