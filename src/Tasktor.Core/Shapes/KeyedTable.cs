namespace Tasktor.Core.Shapes;

/// <summary>
/// Rows that come and go (A D-A5 Table). <see cref="Apply"/> replaces the current rows and fills
/// <see cref="Added"/> / <see cref="Removed"/> / <see cref="Changed"/>; buffers are reused, O(n) via hashing.
/// </summary>
public sealed class KeyedTable<TKey, TRow>
    where TKey : notnull
{
    private Dictionary<TKey, TRow> _rows;
    private Dictionary<TKey, TRow> _next;
    private readonly IEqualityComparer<TRow> _rowComparer;
    private readonly List<TKey> _added = [];
    private readonly List<KeyValuePair<TKey, TRow>> _removed = [];
    private readonly List<TKey> _changed = [];

    public KeyedTable(IEqualityComparer<TKey>? keyComparer = null, IEqualityComparer<TRow>? rowComparer = null)
    {
        _rows = new Dictionary<TKey, TRow>(keyComparer);
        _next = new Dictionary<TKey, TRow>(keyComparer);
        _rowComparer = rowComparer ?? EqualityComparer<TRow>.Default;
    }

    public int Count => _rows.Count;

    public IReadOnlyDictionary<TKey, TRow> Rows => _rows;

    public IReadOnlyList<TKey> Added => _added;

    /// <summary>Removed rows with their last values (e.g. for "process exited").</summary>
    public IReadOnlyList<KeyValuePair<TKey, TRow>> Removed => _removed;

    public IReadOnlyList<TKey> Changed => _changed;

    public bool TryGet(TKey key, out TRow row) => _rows.TryGetValue(key, out row!);

    /// <summary>Replace all rows with <paramref name="rows"/> and compute the diff. Duplicate keys: last wins.</summary>
    public void Apply(IEnumerable<KeyValuePair<TKey, TRow>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _added.Clear();
        _removed.Clear();
        _changed.Clear();
        _next.Clear();
        foreach (var kv in rows)
        {
            _next[kv.Key] = kv.Value;
        }

        foreach (var kv in _next)
        {
            if (!_rows.TryGetValue(kv.Key, out var old))
            {
                _added.Add(kv.Key);
            }
            else if (!_rowComparer.Equals(old, kv.Value))
            {
                _changed.Add(kv.Key);
            }
        }

        foreach (var kv in _rows)
        {
            if (!_next.ContainsKey(kv.Key))
            {
                _removed.Add(kv);
            }
        }

        (_rows, _next) = (_next, _rows);
    }

    /// <summary>Insert or update one row (for push sources); returns true when it was new.</summary>
    public bool Upsert(TKey key, TRow row)
    {
        var isNew = !_rows.ContainsKey(key);
        _rows[key] = row;
        return isNew;
    }

    public bool Remove(TKey key) => _rows.Remove(key);

    /// <summary>Drop all rows and free the buffers' contents (D-A3: drop per-process buffers when no viewer).</summary>
    public void Clear()
    {
        _rows.Clear();
        _next.Clear();
        _added.Clear();
        _removed.Clear();
        _changed.Clear();
        _rows.TrimExcess();
        _next.TrimExcess();
    }
}
