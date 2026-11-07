using System.Text.Json;
using Tasktor.Contracts;
using Tasktor.Contracts.Pipe;
using Tasktor.Contracts.Snapshot;
using Tasktor.Core.Loop;
using SnapshotRecord = Tasktor.Contracts.Snapshot.Snapshot;

namespace Tasktor.Core.State;

/// <summary>What a publish produced: the snapshot, its bytes, the state ops since the previous one, and whether cards changed.</summary>
public sealed record SnapshotPublished(SnapshotRecord Snapshot, byte[] Bytes, IReadOnlyList<PatchOp> StateOps, bool CardsChanged);

/// <summary>
/// Writes <c>snapshot.json</c> at most once per second while values change and never while nothing changes (U1 D-H3),
/// and feeds the pipe server. Change-driven: <see cref="MarkChanged"/> schedules one publish at the earliest allowed
/// time, so an idle agent does not wake every second (D-A3). Loop-only.
/// </summary>
public sealed class SnapshotPublisher
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

    private readonly LogicLoop _loop;
    private readonly StateTracker _state;
    private readonly CardTemplates _cards;
    private readonly Func<AgentInfo> _agent;
    private readonly Action<byte[]>? _write;
    private DateTimeOffset? _lastWrite;
    private AgentInfo? _lastAgent;
    private IDisposable? _scheduled;

    /// <param name="write">Receives the snapshot bytes (the agent hands them to the file writer); null = no file.</param>
    public SnapshotPublisher(LogicLoop loop, StateTracker state, CardTemplates cards, Func<AgentInfo> agent, Action<byte[]>? write, long seqStart = 0)
    {
        Seq = seqStart;
        _loop = loop;
        _state = state;
        _cards = cards;
        _agent = agent;
        _write = write;
    }

    public long Seq { get; private set; }

    public long Writes { get; private set; }

    public SnapshotRecord? Latest { get; private set; }

    public byte[]? LatestBytes { get; private set; }

    /// <summary>Raised on the loop after each publish.</summary>
    public event Action<SnapshotPublished>? Published;

    /// <summary>Something changed (state, cards or agent info): publish as soon as the 1 s limit allows.</summary>
    public void MarkChanged()
    {
        if (_scheduled is not null)
        {
            return;
        }

        var now = _loop.Clock.UtcNow;
        var due = _lastWrite is { } last && now - last < MinInterval ? last + MinInterval : now;
        _scheduled = _loop.At(due, () =>
        {
            _scheduled = null;
            Publish();
        });
    }

    /// <summary>Publish now if allowed and something changed. Returns true when a snapshot was written.</summary>
    public bool Publish(bool force = false)
    {
        var now = _loop.Clock.UtcNow;
        if (!force && _lastWrite is { } last && now - last < MinInterval)
        {
            MarkChanged();
            return false;
        }

        var ops = _state.TakeDelta();
        var cardsChanged = _cards.Refresh();
        var agent = _agent();
        var agentChanged = !AgentEquals(agent, _lastAgent);
        if (!force && ops.Count == 0 && !cardsChanged && !agentChanged)
        {
            return false;
        }

        Seq++;
        _lastAgent = agent;
        var snap = new SnapshotRecord
        {
            GeneratedAt = now,
            Seq = Seq,
            Agent = agent,
            State = _state.Current,
            Cards = _cards.Cards,
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snap, ContractsJsonContext.Default.Snapshot);
        Latest = snap;
        LatestBytes = bytes;
        _lastWrite = now;
        Writes++;
        _write?.Invoke(bytes);
        Published?.Invoke(new SnapshotPublished(snap, bytes, ops, cardsChanged));
        return true;
    }

    private static bool AgentEquals(AgentInfo a, AgentInfo? b) =>
        b is not null && a.Version == b.Version && a.Running == b.Running && Equals(a.Paused, b.Paused) && Equals(a.Self, b.Self);
}
