using System.Text.Json;
using System.Text.Json.Nodes;
using Tasktor.Contracts;
using Tasktor.Contracts.Pipe;
using Tasktor.Contracts.Snapshot;

namespace Tasktor.Core.State;

/// <summary>
/// Holds the snapshot's <c>state</c> tree (U1 class table). Features replace their section; the tracker marks it dirty
/// and <see cref="TakeDelta"/> returns JSON Patch ops (paths under <c>/state</c>) for what actually changed since the
/// last take, diffing only the dirty sections. Loop-only.
/// </summary>
public sealed class StateTracker
{
    private JsonObject _published = [];
    private bool _dirty = true;

    public SnapshotState Current { get; private set; } = new();

    public bool IsDirty => _dirty;

    /// <summary>Replace the state through <paramref name="change"/> (e.g. <c>s =&gt; s with { Jobs = rows }</c>).</summary>
    public void Update(Func<SnapshotState, SnapshotState> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var next = change(Current);
        if (!ReferenceEquals(next, Current))
        {
            Current = next;
            _dirty = true;
        }
    }

    /// <summary>The current state as JSON (for bindings, GlanceText, pulls).</summary>
    public JsonObject ToJson() =>
        JsonSerializer.SerializeToNode(Current, ContractsJsonContext.Default.SnapshotState) as JsonObject ?? [];

    /// <summary>Ops since the last take; empty when nothing changed in value.</summary>
    public IReadOnlyList<PatchOp> TakeDelta()
    {
        if (!_dirty)
        {
            return [];
        }

        _dirty = false;
        var now = ToJson();
        var ops = JsonDiff.Diff(_published, now, "/state");
        _published = now;
        return ops;
    }

    /// <summary>The last published state JSON (what subscribers have).</summary>
    public JsonObject Published => _published;
}
