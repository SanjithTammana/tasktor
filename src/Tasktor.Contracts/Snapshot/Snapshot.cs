using System.Text.Json.Serialization;
using Tasktor.Contracts.Cards;

namespace Tasktor.Contracts.Snapshot;

/// <summary><c>tasktor.snapshot/1</c>: state (data) + cards bound to state (U1 D-H2).</summary>
public sealed record Snapshot
{
    public string Schema { get; init; } = ContractIds.Snapshot;

    public required DateTimeOffset GeneratedAt { get; init; }

    public required long Seq { get; init; }

    public required AgentInfo Agent { get; init; }

    public required SnapshotState State { get; init; }

    public IReadOnlyList<Card>? Cards { get; init; }
}

public sealed record AgentInfo
{
    public required string Version { get; init; }

    public bool Running { get; init; } = true;

    public PausedState? Paused { get; init; }

    public SelfStats? Self { get; init; }
}

public sealed record PausedState
{
    public bool Tracking { get; init; }

    public bool Capture { get; init; }

    public bool Refits { get; init; }
}

/// <summary>The agent's own cost (A D-A3 self-accountability).</summary>
public sealed record SelfStats
{
    public double PrivateMb { get; init; }

    public double CpuPct { get; init; }

    public double WakeupsPerMin { get; init; }
}

/// <summary>The <c>state</c> tree. Readers ignore unknown fields; every member is optional so features add theirs.</summary>
public sealed record SnapshotState
{
    public SystemState? System { get; init; }

    public IReadOnlyList<AppRow>? Apps { get; init; }

    public IReadOnlyList<JobState>? Jobs { get; init; }

    public WorkflowState? Workflow { get; init; }

    public IReadOnlyList<AlertState>? Alerts { get; init; }

    public IReadOnlyList<PluginStatus>? Plugins { get; init; }

    public PowerState? Power { get; init; }

    public Fullscreen? Fullscreen { get; init; }

    public IReadOnlyList<GuardState>? Guards { get; init; }

    public IReadOnlyList<ListenerRow>? Ports { get; init; }

    public IReadOnlyList<AgentSessionRow>? Agents { get; init; }

    public CaptureState? Capture { get; init; }

    public FocusState? Focus { get; init; }

    public ConnectivityState? Connectivity { get; init; }

    public IReadOnlyList<CalendarItem>? Calendar { get; init; }
}

public sealed record SystemState
{
    public double? CpuPct { get; init; }

    public double? RamUsedPct { get; init; }

    public double? RamAvailableGb { get; init; }

    public double? RamTotalGb { get; init; }

    public double? CommitPct { get; init; }

    public IReadOnlyList<GpuRow>? Gpu { get; init; }

    public IReadOnlyList<DiskRow>? Disks { get; init; }

    public IReadOnlyList<VolumeRow>? Volumes { get; init; }

    public NetState? Net { get; init; }

    public BatteryState? Battery { get; init; }

    /// <summary>The A2.7 "why is it slow" verdict line, when one exists.</summary>
    public string? Verdict { get; init; }

    public Severity? VerdictSeverity { get; init; }
}

public sealed record GpuRow
{
    public required string Id { get; init; }

    public string? Name { get; init; }

    public double Pct { get; init; }

    public double? DedicatedMb { get; init; }

    public double? SharedMb { get; init; }
}

public sealed record DiskRow
{
    public required string Id { get; init; }

    public double ActivePct { get; init; }

    public double ReadBps { get; init; }

    public double WriteBps { get; init; }
}

public sealed record VolumeRow
{
    public required string Id { get; init; }

    public string? Letter { get; init; }

    public double FreeGb { get; init; }

    public double TotalGb { get; init; }

    public double FreePct { get; init; }

    public bool? Low { get; init; }
}

public sealed record NetState
{
    public double RxBps { get; init; }

    public double TxBps { get; init; }
}

public sealed record BatteryState
{
    /// <summary>Percent, or null when Windows reports it as unknown (never 255).</summary>
    public double? Pct { get; init; }

    public bool Charging { get; init; }

    public bool OnAc { get; init; }

    /// <summary>Seconds left, or null when unknown (never -1).</summary>
    public long? TimeLeftS { get; init; }

    public bool Saver { get; init; }
}

public sealed record AppRow
{
    public required string AppId { get; init; }

    public required string Name { get; init; }

    public double CpuPct { get; init; }

    public double PrivateMb { get; init; }

    public double? GpuPct { get; init; }

    public int Count { get; init; } = 1;

    public bool? Admin { get; init; }

    public bool? System { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<JobStatus>))]
public enum JobStatus
{
    [JsonStringEnumMemberName("starting")] Starting,
    [JsonStringEnumMemberName("working")] Working,
    [JsonStringEnumMemberName("working_gpu")] WorkingGpu,
    [JsonStringEnumMemberName("quiet")] Quiet,
    [JsonStringEnumMemberName("silent")] Silent,
    [JsonStringEnumMemberName("stuck_busy")] StuckBusy,
    [JsonStringEnumMemberName("paused")] Paused,
    [JsonStringEnumMemberName("done")] Done,
    [JsonStringEnumMemberName("failed")] Failed,
    [JsonStringEnumMemberName("ended")] Ended,
}

[JsonConverter(typeof(JsonStringEnumConverter<PauseMode>))]
public enum PauseMode
{
    [JsonStringEnumMemberName("none")] None,
    [JsonStringEnumMemberName("polite")] Polite,
    [JsonStringEnumMemberName("frozen")] Frozen,
    [JsonStringEnumMemberName("frozen_trimmed")] FrozenTrimmed,
}

[JsonConverter(typeof(JsonStringEnumConverter<ProcessPriority>))]
public enum ProcessPriority
{
    [JsonStringEnumMemberName("unknown")] Unknown,
    [JsonStringEnumMemberName("idle")] Idle,
    [JsonStringEnumMemberName("below_normal")] BelowNormal,
    [JsonStringEnumMemberName("normal")] Normal,
    [JsonStringEnumMemberName("above_normal")] AboveNormal,
    [JsonStringEnumMemberName("high")] High,
    [JsonStringEnumMemberName("realtime")] Realtime,
}

/// <summary>Where an adaptive value came from (B D-B9).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ValueSource>))]
public enum ValueSource
{
    [JsonStringEnumMemberName("default")] Default,
    [JsonStringEnumMemberName("blended")] Blended,
    [JsonStringEnumMemberName("learned")] Learned,
    [JsonStringEnumMemberName("fallback_guardrail")] FallbackGuardrail,
    [JsonStringEnumMemberName("fallback_out_of_range")] FallbackOutOfRange,
}

/// <summary>A job row in the snapshot (follows B's <c>JobRow</c>; D-B11, B-D asks).</summary>
public sealed record JobState
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    public required JobStatus Status { get; init; }

    public PauseMode Pause { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public double CoresNow { get; init; }

    public double CpuTimeS { get; init; }

    public double? IoBps { get; init; }

    public ProcessPriority Priority { get; init; } = ProcessPriority.Normal;

    public bool? Eco { get; init; }

    public bool? Restrained { get; init; }

    public bool? Hogging { get; init; }

    public long? QuietS { get; init; }

    public long? PausedS { get; init; }

    public int? ExitCode { get; init; }

    public int Workers { get; init; }

    public double? Pct { get; init; }

    public string? Stage { get; init; }

    public string? Msg { get; init; }

    public long? EtaS { get; init; }

    public ValueSource? EtaSource { get; init; }

    public int? EtaRuns { get; init; }

    public bool? LongerThanUsual { get; init; }

    public ValueSource? SilentSource { get; init; }

    /// <summary>One-line explanation of the current adaptive decision (e.g. "silent 14 min · learned").</summary>
    public string? Basis { get; init; }

    public bool? Internal { get; init; }

    public bool? HasLog { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<WorkflowSource>))]
public enum WorkflowSource
{
    [JsonStringEnumMemberName("learned")] Learned,
    [JsonStringEnumMemberName("default")] Default,
}

[JsonConverter(typeof(JsonStringEnumConverter<WorkflowLevel>))]
public enum WorkflowLevel
{
    [JsonStringEnumMemberName("workflow")] Workflow,
    [JsonStringEnumMemberName("activity")] Activity,
    [JsonStringEnumMemberName("app")] App,
}

/// <summary>C's <c>CurrentWorkflow</c> (C-H6).</summary>
public sealed record WorkflowState
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public double Confidence { get; init; }

    public WorkflowSource Source { get; init; }

    public WorkflowLevel Level { get; init; }

    public long? SwitchesToday { get; init; }
}

public sealed record AlertState
{
    public required string Rule { get; init; }

    public Severity Severity { get; init; }

    public string? Card { get; init; }

    public required DateTimeOffset Since { get; init; }

    public string? Text { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<PluginState>))]
public enum PluginState
{
    [JsonStringEnumMemberName("ok")] Ok,
    [JsonStringEnumMemberName("error")] Error,
    [JsonStringEnumMemberName("off")] Off,
    [JsonStringEnumMemberName("found")] Found,
}

public sealed record PluginStatus
{
    public required string Id { get; init; }

    public PluginState State { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    public string? Error { get; init; }
}

public sealed record PowerState
{
    public bool OnBattery { get; init; }

    public bool HasBattery { get; init; }
}

/// <summary><c>state.fullscreen</c> values (U3 D-W9, H-W5).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Fullscreen>))]
public enum Fullscreen
{
    [JsonStringEnumMemberName("none")] None,
    [JsonStringEnumMemberName("borderless")] Borderless,
    [JsonStringEnumMemberName("exclusive")] Exclusive,
    [JsonStringEnumMemberName("presentation")] Presentation,
}

/// <summary>An active guard (rule effect that can be undone), for <c>core.guards</c>.</summary>
public sealed record GuardState
{
    public required string Rule { get; init; }

    public required string Text { get; init; }

    public DateTimeOffset? Since { get; init; }
}

public sealed record ListenerRow
{
    public required int Port { get; init; }

    public required IReadOnlyList<string> Addresses { get; init; }

    public string? App { get; init; }

    public uint? Pid { get; init; }

    public bool Exposed { get; init; }

    /// <summary>Owned by one of the user's own processes (the default filter shows only these).</summary>
    public bool Own { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<AgentSessionState>))]
public enum AgentSessionState
{
    [JsonStringEnumMemberName("working")] Working,
    [JsonStringEnumMemberName("needs-permission")] NeedsPermission,
    [JsonStringEnumMemberName("waiting")] Waiting,
    [JsonStringEnumMemberName("ended")] Ended,
    [JsonStringEnumMemberName("busy")] Busy,
    [JsonStringEnumMemberName("idle")] Idle,
}

public sealed record AgentSessionRow
{
    public required string Agent { get; init; }

    public string? Folder { get; init; }

    public required AgentSessionState State { get; init; }

    public bool Reported { get; init; }

    public DateTimeOffset? Since { get; init; }

    public double? CoresNow { get; init; }
}

public sealed record CaptureState
{
    public bool On { get; init; }

    public DateTimeOffset? PausedUntil { get; init; }

    public bool PausedIndefinitely { get; init; }

    public IReadOnlyList<string>? Blocks { get; init; }

    public long? RecordsToday { get; init; }
}

public sealed record FocusState
{
    public required string Preset { get; init; }

    public required string Block { get; init; }

    public required DateTimeOffset Until { get; init; }

    public bool Paused { get; init; }
}

public sealed record ConnectivityState
{
    public required string Status { get; init; }

    public int DropsToday { get; init; }
}

public sealed record CalendarItem
{
    public required string Title { get; init; }

    public required DateTimeOffset Start { get; init; }

    public DateTimeOffset? End { get; init; }

    public bool Meeting { get; init; }

    public bool Deadline { get; init; }
}
