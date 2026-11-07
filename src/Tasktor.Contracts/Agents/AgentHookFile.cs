using Tasktor.Contracts.Snapshot;

namespace Tasktor.Contracts.Agents;

/// <summary>
/// <c>tasktor.agent/1</c>: <c>agents\&lt;session&gt;.json</c>, written by <c>tasktor agent-hook</c> (U1 I1).
/// Keeps only the session id, the last segment of the working directory and the state; never prompts or transcripts.
/// </summary>
public sealed record AgentHookFile
{
    public string Schema { get; init; } = ContractIds.Agent;

    public required string Agent { get; init; }

    public required string SessionId { get; init; }

    public string? Folder { get; init; }

    public required AgentSessionState State { get; init; }

    public required DateTimeOffset Since { get; init; }

    public uint? Pid { get; init; }
}
