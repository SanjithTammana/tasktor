namespace Tasktor.Widget;

/// <summary>Tracks pinned instances and invalidates in-flight work when membership changes.</summary>
public sealed class WidgetActivity
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _active = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _changed = new(0, 1);
    private CancellationTokenSource _generation = new();

    public sealed record Snapshot(KeyValuePair<string, string>[] Instances, CancellationToken Token);

    public bool HasActive { get { lock (_gate) return _active.Count != 0; } }
    public bool IsActive(string id) { lock (_gate) return _active.ContainsKey(id); }
    public string[] ActiveIds() { lock (_gate) return [.. _active.Keys]; }
    public string? Definition(string id) { lock (_gate) return _active.GetValueOrDefault(id); }

    public void Activate(string id, string definition)
    {
        CancellationTokenSource previous;
        lock (_gate)
        {
            _active[id] = definition;
            previous = AdvanceGeneration();
        }
        NotifyChange(previous);
    }

    public void Deactivate(string id)
    {
        CancellationTokenSource previous;
        lock (_gate)
        {
            if (!_active.Remove(id)) return;
            previous = AdvanceGeneration();
        }
        NotifyChange(previous);
    }

    public Snapshot Capture()
    {
        lock (_gate) return new Snapshot([.. _active], _generation.Token);
    }

    public bool IsCurrent(Snapshot snapshot)
    {
        lock (_gate) return snapshot.Token == _generation.Token;
    }

    public Task<bool> WaitForChangeAsync(TimeSpan timeout) => _changed.WaitAsync(timeout);

    // Called under _gate; cancellation happens outside the lock to avoid invoking callbacks under it.
    private CancellationTokenSource AdvanceGeneration()
    {
        var previous = _generation;
        _generation = new CancellationTokenSource();
        return previous;
    }

    private void NotifyChange(CancellationTokenSource previous)
    {
        previous.Cancel();
        try { _changed.Release(); }
        catch (SemaphoreFullException) { } // Coalesce changes until the loop wakes.
    }
}
