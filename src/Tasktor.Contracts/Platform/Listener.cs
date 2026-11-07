namespace Tasktor.Contracts.Platform;

/// <summary>One listening TCP endpoint with its owner (A7, <c>GetExtendedTcpTable</c> OWNER_PID_LISTENER, both families).</summary>
public sealed record ListenerEntry
{
    public required int Port { get; init; }

    public required string Address { get; init; }

    public required uint Pid { get; init; }

    public string? Exe { get; init; }

    public bool OwnUser { get; init; }

    public long CreatedTicks { get; init; }
}
