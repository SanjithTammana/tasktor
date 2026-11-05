namespace Tasktor.Core.Shapes;

/// <summary>Exact top N by a comparer without a full sort: a bounded min-heap, O(n log N).</summary>
public sealed class TopN<T>
{
    private readonly PriorityQueue<T, T> _heap;
    private readonly IComparer<T> _comparer;

    public TopN(int n, IComparer<T> comparer)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 1);
        N = n;
        _comparer = comparer;
        _heap = new PriorityQueue<T, T>(n + 1, comparer);
    }

    public int N { get; }

    public void Add(T item)
    {
        if (_heap.Count < N)
        {
            _heap.Enqueue(item, item);
        }
        else if (_heap.TryPeek(out var min, out _) && _comparer.Compare(item, min) > 0)
        {
            _heap.DequeueEnqueue(item, item);
        }
    }

    /// <summary>The kept items, largest first. Ties keep insertion order where the comparer allows.</summary>
    public List<T> Result()
    {
        var list = new List<T>(_heap.Count);
        foreach (var (e, _) in _heap.UnorderedItems)
        {
            list.Add(e);
        }

        list.Sort((a, b) => _comparer.Compare(b, a));
        return list;
    }

    public void Clear() => _heap.Clear();

    public static List<T> Of(IEnumerable<T> items, int n, IComparer<T> comparer)
    {
        ArgumentNullException.ThrowIfNull(items);
        var top = new TopN<T>(n, comparer);
        foreach (var i in items)
        {
            top.Add(i);
        }

        return top.Result();
    }
}
