using Tasktor.Contracts.Snapshot;

namespace Tasktor.Contracts.Platform;

/// <summary>
/// One process from <c>ITableSource&lt;ProcKey, ProcessRow&gt;</c>. A names-only read (<see cref="ColumnSet.Names"/>)
/// fills <see cref="Key"/> (creation time may be 0), <see cref="ParentPid"/> and <see cref="Exe"/>; the other columns
/// are filled only when asked for.
/// </summary>
public sealed record ProcessRow
{
    public required ProcKey Key { get; init; }

    public uint ParentPid { get; init; }

    /// <summary>Exe file name, e.g. <c>chrome.exe</c>.</summary>
    public required string Exe { get; init; }

    /// <summary>Full image path, when readable. Paths under the profile are personal data in logs.</summary>
    public string? Path { get; init; }

    /// <summary>Package family name for packaged apps.</summary>
    public string? Package { get; init; }

    public bool OwnUser { get; init; } = true;

    public bool Elevated { get; init; }

    public bool Protected { get; init; }

    public int Threads { get; init; }

    public ProcessPriority Priority { get; init; } = ProcessPriority.Normal;

    public bool Eco { get; init; }

    /// <summary>Cumulative CPU time (user + kernel), seconds.</summary>
    public double CpuTimeS { get; init; }

    public double PrivateBytes { get; init; }

    /// <summary>Cumulative read + write bytes.</summary>
    public double IoBytes { get; init; }

    public double? GpuPct { get; init; }

    /// <summary>Read once per (PID, creation time) for watch-list matches only (B D-B8).</summary>
    public Sensitive<string>? CommandLine { get; init; }
}

/// <summary>A process exit, pushed by <c>IEventSource&lt;ProcessExit&gt;</c> (B D-B4): exit code read after the wait fired (null when unknown).</summary>
public sealed record ProcessExit(ProcKey Key, int? ExitCode, DateTimeOffset AtUtc);

