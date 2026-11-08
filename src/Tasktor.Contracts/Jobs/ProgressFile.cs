using System.Text.Json.Serialization;

namespace Tasktor.Contracts.Jobs;

/// <summary>OSC 9;4 state names (B4).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProgressState>))]
public enum ProgressState
{
    [JsonStringEnumMemberName("running")] Running,
    [JsonStringEnumMemberName("indeterminate")] Indeterminate,
    [JsonStringEnumMemberName("warning")] Warning,
    [JsonStringEnumMemberName("error")] Error,
    [JsonStringEnumMemberName("done")] Done,
}

/// <summary>
/// <c>tasktor.job/1</c>: the progress file <c>jobs\&lt;id&gt;.json</c> (B4, D-B6). Untrusted input: ≤ 4 KB,
/// unknown fields ignored, strings length-capped at parse.
/// </summary>
public sealed record ProgressFile
{
    /// <summary>Always <c>1</c>.</summary>
    public required int Schema { get; init; }

    /// <summary>Equals the file stem; <c>[A-Za-z0-9._-]{1,64}</c>.</summary>
    public required string Id { get; init; }

    public uint? Pid { get; init; }

    public string? Label { get; init; }

    public double? Pct { get; init; }

    public long? Done { get; init; }

    public long? Total { get; init; }

    public string? Unit { get; init; }

    public string? Stage { get; init; }

    public string? Msg { get; init; }

    public long? EtaS { get; init; }

    /// <summary>A local log path; URLs are rejected.</summary>
    public string? Log { get; init; }

    public ProgressState? State { get; init; }

    /// <summary>5–3600: the writer's promise to update at least this often.</summary>
    public int? HeartbeatS { get; init; }

    /// <summary>ISO-8601 UTC; the heartbeat.</summary>
    public required DateTimeOffset Updated { get; init; }

    /// <summary>Additive field written by the helper while politely paused (B3.7).</summary>
    public bool? Paused { get; init; }
}

/// <summary>The polite-pause control file <c>jobs\&lt;id&gt;.control.json</c> written by Tasktor (B3.7; internal).</summary>
public sealed record JobControlFile
{
    public bool Pause { get; init; }

    public string? Message { get; init; }
}
