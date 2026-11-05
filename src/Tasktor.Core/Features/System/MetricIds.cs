namespace Tasktor.Core.Features.System;

/// <summary>Metric ids the system sources fill (A1 data sources). Instances: adapter LUID, disk, adapter, volume.</summary>
public static class MetricIds
{
    public const string CpuPct = "cpu.pct";
    public const string CpuCorePct = "cpu.core_pct";
    public const string RamAvailableMb = "ram.available_mb";
    public const string RamTotalMb = "ram.total_mb";
    public const string CommitBytes = "commit.bytes";
    public const string CommitLimitBytes = "commit.limit_bytes";
    public const string GpuPct = "gpu.pct";
    public const string GpuDedicatedMb = "gpu.dedicated_mb";
    public const string GpuSharedMb = "gpu.shared_mb";
    public const string DiskIdlePct = "disk.idle_pct";
    public const string DiskReadBps = "disk.read_bps";
    public const string DiskWriteBps = "disk.write_bps";
    public const string NetRxBytes = "net.rx_bytes";
    public const string NetTxBytes = "net.tx_bytes";

    /// <summary>Network rates when the source measures them directly (PDH) instead of cumulative bytes.</summary>
    public const string NetRxBps = "net.rx_bps";

    public const string NetTxBps = "net.tx_bps";
    public const string VolumeFreeBytes = "volume.free_bytes";
    public const string VolumeTotalBytes = "volume.total_bytes";
}

/// <summary>A colour that changes only after the new level has held for a while (A1.7: 10 s, no flicker).</summary>
public sealed class SeverityHold(TimeSpan hold)
{
    private Contracts.Cards.Severity _pending;
    private DateTimeOffset _pendingSince;

    public Contracts.Cards.Severity Current { get; private set; }

    public Contracts.Cards.Severity Update(Contracts.Cards.Severity raw, DateTimeOffset now)
    {
        if (raw == Current)
        {
            _pending = raw;
            return Current;
        }

        if (raw != _pending)
        {
            _pending = raw;
            _pendingSince = now;
        }

        if (now - _pendingSince >= hold)
        {
            Current = raw;
        }

        return Current;
    }

    public static Contracts.Cards.Severity Classify(double value, Contracts.Config.Threshold t) =>
        value >= t.Critical ? Contracts.Cards.Severity.Critical : value >= t.Warn ? Contracts.Cards.Severity.Warn : Contracts.Cards.Severity.Normal;
}
