using System.Text.Json.Serialization;

namespace Tasktor.Contracts;

/// <summary>Closed set of worker kinds (U1 D-H9 condition 1).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkerKind>))]
public enum WorkerKind
{
    [JsonStringEnumMemberName("learning")] Learning,
    [JsonStringEnumMemberName("content")] Content,
    [JsonStringEnumMemberName("capture")] Capture,
    [JsonStringEnumMemberName("plugin")] Plugin,
}

/// <summary>Closed set of control messages for Tasktor's children and polite-pause jobs (D-H9 condition 3, B-H5).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ControlMessage>))]
public enum ControlMessage
{
    [JsonStringEnumMemberName("pause")] Pause,
    [JsonStringEnumMemberName("resume")] Resume,
    [JsonStringEnumMemberName("cancel")] Cancel,
    [JsonStringEnumMemberName("checkpoint")] Checkpoint,
    [JsonStringEnumMemberName("flush")] Flush,
}

/// <summary>What <c>IProcessControl.StartWorker</c> starts (D-H9). Non-plugin exes must resolve inside the install dir.</summary>
public sealed record WorkerSpec
{
    public required WorkerKind Kind { get; init; }

    public required string Exe { get; init; }

    public IReadOnlyList<string>? Args { get; init; }

    public required string WorkingDir { get; init; }

    /// <summary>Job Object memory cap in MB (plugins 256, workers 1536).</summary>
    public int MemoryMb { get; init; } = 256;

    /// <summary>Kill after this many milliseconds (0 = no timeout).</summary>
    public int TimeoutMs { get; init; }

    /// <summary>Text written to stdin, then stdin is closed.</summary>
    public string? Stdin { get; init; }

    /// <summary>Environment variables to remove (e.g. PYTHONPATH, NODE_PATH).</summary>
    public IReadOnlyList<string>? ClearEnv { get; init; }

    public bool Eco { get; init; }
}
