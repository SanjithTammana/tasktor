using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Tasktor.Contracts.Actions;

namespace Tasktor.Contracts.Pipe;

/// <summary>
/// Pipe frames (U1 D-H3; internal contract: JSON lines, one message per line). Requests carry an <c>id</c>
/// that the reply echoes.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(WelcomeMessage), "welcome")]
[JsonDerivedType(typeof(SubscribeMessage), "subscribe")]
[JsonDerivedType(typeof(UnsubscribeMessage), "unsubscribe")]
[JsonDerivedType(typeof(DeltaMessage), "delta")]
[JsonDerivedType(typeof(PullMessage), "pull")]
[JsonDerivedType(typeof(PullResultMessage), "pull_result")]
[JsonDerivedType(typeof(ActMessage), "act")]
[JsonDerivedType(typeof(ResultMessage), "result")]
[JsonDerivedType(typeof(LeaseMessage), "lease")]
[JsonDerivedType(typeof(ErrorMessage), "error")]
public abstract record PipeMessage
{
    public string? Id { get; init; }
}

/// <summary>Version handshake: a client asking for an unknown major gets an error, not a guess.</summary>
public sealed record HelloMessage : PipeMessage
{
    public required string Client { get; init; }

    public required IReadOnlyList<string> Contracts { get; init; }
}

public sealed record WelcomeMessage : PipeMessage
{
    public required string Agent { get; init; }

    public required IReadOnlyList<string> Contracts { get; init; }
}

/// <summary>Subscribe to state paths (JSON Pointers; empty = all) and optionally cards.</summary>
public sealed record SubscribeMessage : PipeMessage
{
    public IReadOnlyList<string>? Paths { get; init; }

    public bool Cards { get; init; }
}

public sealed record UnsubscribeMessage : PipeMessage;

/// <summary>A JSON Patch subset (replace / add / remove), coalesced to ≤ 1 per 500 ms per client.</summary>
public sealed record DeltaMessage : PipeMessage
{
    public required long Seq { get; init; }

    public required IReadOnlyList<PatchOp> Ops { get; init; }
}

public sealed record PatchOp
{
    /// <summary><c>replace</c>, <c>add</c> or <c>remove</c>.</summary>
    public required string Op { get; init; }

    public required string Path { get; init; }

    public JsonNode? Value { get; init; }
}

/// <summary>Pull a full table, series or view: <c>processes</c>, <c>listeners</c>, <c>folders</c>, <c>series:&lt;id&gt;</c>, <c>learned</c>, <c>snapshot</c>, <c>catalog</c>.</summary>
public sealed record PullMessage : PipeMessage
{
    public required string Table { get; init; }

    public IReadOnlyList<string>? Columns { get; init; }

    public int? Top { get; init; }

    public IReadOnlyDictionary<string, string>? Args { get; init; }
}

public sealed record PullResultMessage : PipeMessage
{
    public required string Table { get; init; }

    public JsonNode? Data { get; init; }
}

public sealed record ActMessage : PipeMessage
{
    public required ActionRequest Request { get; init; }
}

public sealed record ResultMessage : PipeMessage
{
    public required ActionResult Result { get; init; }
}

/// <summary>A viewer's demand lease: the agent samples a source at the interval only while a lease is held; it dies with the connection.</summary>
public sealed record LeaseMessage : PipeMessage
{
    public required string Source { get; init; }

    public IReadOnlyList<string>? Columns { get; init; }

    /// <summary>Interval in milliseconds; 0 releases the lease.</summary>
    public required int IntervalMs { get; init; }
}

public sealed record ErrorMessage : PipeMessage
{
    public required string Code { get; init; }

    public string? Message { get; init; }
}
