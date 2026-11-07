using Tasktor.Contracts.Jobs;

namespace Tasktor.Contracts.Platform;

/// <summary>Where a progress row comes from (B D-B11): a progress file, a worker's stdout, or Tasktor itself (B5).</summary>
public enum ProgressSource
{
    File,
    Worker,
    Internal,
}

/// <summary>The generic "long task" row (B D-B11): progress files, refits (C8) and the folder scan (A5.3) use the same shape.</summary>
public sealed record ProgressRow
{
    public required string Id { get; init; }

    public uint? Pid { get; init; }

    public required string Label { get; init; }

    public double? Pct { get; init; }

    public long? Done { get; init; }

    public long? Total { get; init; }

    public string? Unit { get; init; }

    public string? Stage { get; init; }

    public string? Msg { get; init; }

    public long? EtaS { get; init; }

    public string? LogPath { get; init; }

    public ProgressState State { get; init; } = ProgressState.Running;

    public int? HeartbeatS { get; init; }

    public bool Paused { get; init; }

    public required DateTimeOffset UpdatedUtc { get; init; }

    public DateTimeOffset? StartedUtc { get; init; }

    public ProgressSource Source { get; init; }

    /// <summary>For invalid progress files: why the file was rejected (shown as text, never a toast).</summary>
    public string? Invalid { get; init; }
}
