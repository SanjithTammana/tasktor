using System.Buffers.Binary;
using Tasktor.Core.Shapes;

namespace Tasktor.Core.Learning;

/// <summary>Which estimate a kind's policies currently use (guardrail REPLACE sets <see cref="Fallback"/>).</summary>
public enum PolicyMode : byte
{
    Learned = 0,
    Fallback = 1,
}

/// <summary>How one run ended, for the per-kind statistics.</summary>
public sealed record RunOutcome
{
    /// <summary>Good = exit 0, not ended by the user or Tasktor, not flagged true-silent.</summary>
    public required bool Good { get; init; }

    /// <summary>The run's longest quiet gap (sleep, GPU-busy and paused time excluded), seconds.</summary>
    public double QuietMaxS { get; init; }

    /// <summary>Wall time minus paused time, seconds.</summary>
    public double DurationS { get; init; }

    public double CoresMedian { get; init; }

    public bool LoweredByHand { get; init; }
}

/// <summary>
/// Per-key learned statistics (B Adaptive core): fixed-size rings of the last 20 good runs (exact quantiles), counters,
/// the last-10 alarm labels, and the guardrail mode. Numbers only; ~300 bytes; <see cref="ToBytes"/> / <see cref="FromBytes"/>
/// give a fixed binary layout for persistence.
/// </summary>
public sealed class KindStats
{
    public const int Window = 20;
    public const int AlarmWindow = 10;
    public const int ByteSize = 4 + (3 * (1 + (Window * 4))) + (4 * 9) + 2 + 2 + 1 + 1 + 1 + 1 + 8;
    private const int Version = 1;

    public int Runs { get; private set; }

    public int GoodRuns { get; private set; }

    public RingBuffer<float> QuietMax { get; } = new(Window);

    public RingBuffer<float> Duration { get; } = new(Window);

    public RingBuffer<float> CoresMedian { get; } = new(Window);

    public int BecameJob { get; private set; }

    public int StayedIdle { get; private set; }

    public int Alarms { get; private set; }

    public int FalseAlarms { get; private set; }

    public int ShadowFalseAlarms { get; private set; }

    /// <summary>Number of finished toasts dismissed in a row (B2.8).</summary>
    public int FinishedDismissed { get; private set; }

    public PolicyMode Mode { get; set; }

    public int CleanRunsSinceReplace { get; set; }

    public DateTimeOffset LastSeenUtc { get; set; }

    /// <summary>Recent admissions, newest in bit 0: 1 = the candidate became a job (B1.8 "≥ 2 of last 3").</summary>
    public byte AdmissionBits { get; private set; }

    public byte AdmissionCount { get; private set; }

    /// <summary>Last runs' manual lowering, newest in bit 0 (B3.4 "3 of last 5").</summary>
    public byte LoweredBits { get; private set; }

    public byte LoweredCount { get; private set; }

    /// <summary>Last 10 alarm labels, newest in bit 0: 1 = false alarm.</summary>
    public ushort AlarmBits { get; private set; }

    /// <summary>Last 10 alarms: 1 = the fallback would also have raised a false alarm on the same run.</summary>
    public ushort ShadowBits { get; private set; }

    public byte AlarmCount { get; private set; }

    public void RecordRun(RunOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        Runs++;
        if (outcome.Good)
        {
            GoodRuns++;
            QuietMax.Add((float)outcome.QuietMaxS);
            Duration.Add((float)outcome.DurationS);
            CoresMedian.Add((float)outcome.CoresMedian);
        }

        LoweredBits = (byte)(((LoweredBits << 1) | (outcome.LoweredByHand ? 1 : 0)) & 0x1F);
        LoweredCount = (byte)Math.Min(5, LoweredCount + 1);
    }

    public void RecordAdmission(bool becameJob)
    {
        if (becameJob)
        {
            BecameJob++;
        }
        else
        {
            StayedIdle++;
        }

        AdmissionBits = (byte)(((AdmissionBits << 1) | (becameJob ? 1 : 0)) & 0x07);
        AdmissionCount = (byte)Math.Min(3, AdmissionCount + 1);
    }

    /// <summary>Label one alarm: <paramref name="falseAlarm"/> per the user / outcome; <paramref name="shadowFalse"/> = the fallback would have raised a false alarm too.</summary>
    public void RecordAlarm(bool falseAlarm, bool shadowFalse)
    {
        Alarms++;
        if (falseAlarm)
        {
            FalseAlarms++;
        }

        if (shadowFalse)
        {
            ShadowFalseAlarms++;
        }

        AlarmBits = (ushort)(((AlarmBits << 1) | (falseAlarm ? 1 : 0)) & 0x3FF);
        ShadowBits = (ushort)(((ShadowBits << 1) | (shadowFalse ? 1 : 0)) & 0x3FF);
        AlarmCount = (byte)Math.Min(AlarmWindow, AlarmCount + 1);
    }

    public void RecordFinishedToast(bool dismissed) => FinishedDismissed = dismissed ? FinishedDismissed + 1 : 0;

    public int RecentFalseAlarms => System.Numerics.BitOperations.PopCount(AlarmBits);

    public int RecentShadowFalseAlarms => System.Numerics.BitOperations.PopCount(ShadowBits);

    public int RecentLowered => System.Numerics.BitOperations.PopCount(LoweredBits);

    public int RecentBecameJob => System.Numerics.BitOperations.PopCount(AdmissionBits);

    /// <summary>RETRAIN (B guardrails): clear the rings, keep the counts.</summary>
    public void Relearn()
    {
        QuietMax.Clear();
        Duration.Clear();
        CoresMedian.Clear();
        GoodRuns = 0;
        Mode = PolicyMode.Learned;
        CleanRunsSinceReplace = 0;
        AlarmBits = 0;
        ShadowBits = 0;
        AlarmCount = 0;
    }

    public byte[] ToBytes()
    {
        var b = new byte[ByteSize];
        var s = b.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(s, Version);
        var o = 4;
        foreach (var ring in new[] { QuietMax, Duration, CoresMedian })
        {
            s[o++] = (byte)ring.Count;
            for (var i = 0; i < Window; i++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(s[o..], i < ring.Count ? ring[i] : 0f);
                o += 4;
            }
        }

        foreach (var v in new[] { Runs, GoodRuns, BecameJob, StayedIdle, Alarms, FalseAlarms, ShadowFalseAlarms, FinishedDismissed, CleanRunsSinceReplace })
        {
            BinaryPrimitives.WriteInt32LittleEndian(s[o..], v);
            o += 4;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(s[o..], AlarmBits);
        o += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(s[o..], ShadowBits);
        o += 2;
        s[o++] = AlarmCount;
        s[o++] = (byte)((LoweredBits & 0x1F) | (LoweredCount << 5));
        s[o++] = (byte)((AdmissionBits & 0x07) | (AdmissionCount << 3) | ((byte)Mode << 6));
        s[o++] = 0;
        BinaryPrimitives.WriteInt64LittleEndian(s[o..], LastSeenUtc.UtcTicks);
        return b;
    }

    public static KindStats FromBytes(ReadOnlySpan<byte> s)
    {
        if (s.Length != ByteSize || BinaryPrimitives.ReadInt32LittleEndian(s) != Version)
        {
            throw new FormatException("KindStats: bad size or version");
        }

        var k = new KindStats();
        var o = 4;
        foreach (var ring in new[] { k.QuietMax, k.Duration, k.CoresMedian })
        {
            int n = s[o++];
            if (n > Window)
            {
                throw new FormatException("KindStats: ring count");
            }

            for (var i = 0; i < Window; i++)
            {
                var v = BinaryPrimitives.ReadSingleLittleEndian(s[o..]);
                o += 4;
                if (i < n)
                {
                    ring.Add(v);
                }
            }
        }

        int Next(ref int off, ReadOnlySpan<byte> src)
        {
            var v = BinaryPrimitives.ReadInt32LittleEndian(src[off..]);
            off += 4;
            return v;
        }

        k.Runs = Next(ref o, s);
        k.GoodRuns = Next(ref o, s);
        k.BecameJob = Next(ref o, s);
        k.StayedIdle = Next(ref o, s);
        k.Alarms = Next(ref o, s);
        k.FalseAlarms = Next(ref o, s);
        k.ShadowFalseAlarms = Next(ref o, s);
        k.FinishedDismissed = Next(ref o, s);
        k.CleanRunsSinceReplace = Next(ref o, s);
        k.AlarmBits = BinaryPrimitives.ReadUInt16LittleEndian(s[o..]);
        o += 2;
        k.ShadowBits = BinaryPrimitives.ReadUInt16LittleEndian(s[o..]);
        o += 2;
        k.AlarmCount = s[o++];
        var lowered = s[o++];
        k.LoweredBits = (byte)(lowered & 0x1F);
        k.LoweredCount = (byte)(lowered >> 5);
        var adm = s[o++];
        k.AdmissionBits = (byte)(adm & 0x07);
        k.AdmissionCount = (byte)((adm >> 3) & 0x07);
        k.Mode = (PolicyMode)((adm >> 6) & 0x01);
        o++;
        k.LastSeenUtc = new DateTimeOffset(BinaryPrimitives.ReadInt64LittleEndian(s[o..]), TimeSpan.Zero);
        return k;
    }
}
