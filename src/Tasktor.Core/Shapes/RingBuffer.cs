namespace Tasktor.Core.Shapes;

/// <summary>A fixed-capacity ring (A D-A1 hour ring, B's 60-slot sparkline, KindStats' 20-run windows). Never grows.</summary>
public sealed class RingBuffer<T>
{
    private readonly T[] _items;
    private int _start;

    public RingBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _items = new T[capacity];
    }

    public int Capacity => _items.Length;

    public int Count { get; private set; }

    public bool IsFull => Count == _items.Length;

    /// <summary>Item <paramref name="index"/>, 0 = oldest.</summary>
    public T this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            return _items[(_start + index) % _items.Length];
        }
    }

    public T Latest => Count == 0 ? throw new InvalidOperationException("empty") : this[Count - 1];

    /// <summary>Append; when full, the oldest item is overwritten.</summary>
    public void Add(T item)
    {
        if (Count < _items.Length)
        {
            _items[(_start + Count) % _items.Length] = item;
            Count++;
        }
        else
        {
            _items[_start] = item;
            _start = (_start + 1) % _items.Length;
        }
    }

    public void Clear()
    {
        Array.Clear(_items);
        _start = 0;
        Count = 0;
    }

    /// <summary>Copy oldest → newest into <paramref name="destination"/>; returns the number copied.</summary>
    public int CopyTo(Span<T> destination)
    {
        var n = Math.Min(Count, destination.Length);
        for (var i = 0; i < n; i++)
        {
            destination[i] = this[i];
        }

        return n;
    }

    public T[] ToArray()
    {
        var a = new T[Count];
        CopyTo(a);
        return a;
    }
}

/// <summary>Exact statistics over small windows (≤ a few hundred values): quantiles by a sort of a stack copy.</summary>
public static class WindowStats
{
    public static float Max(RingBuffer<float> ring)
    {
        ArgumentNullException.ThrowIfNull(ring);
        var max = float.NegativeInfinity;
        for (var i = 0; i < ring.Count; i++)
        {
            max = Math.Max(max, ring[i]);
        }

        return ring.Count == 0 ? float.NaN : max;
    }

    /// <summary>Exact quantile with linear interpolation (q in [0, 1]); NaN when empty.</summary>
    public static float Quantile(RingBuffer<float> ring, double q)
    {
        ArgumentNullException.ThrowIfNull(ring);
        if (ring.Count == 0)
        {
            return float.NaN;
        }

        Span<float> buf = ring.Count <= 256 ? stackalloc float[ring.Count] : new float[ring.Count];
        ring.CopyTo(buf);
        buf.Sort();
        return Quantile(buf, q);
    }

    public static float Quantile(ReadOnlySpan<float> sorted, double q)
    {
        if (sorted.IsEmpty)
        {
            return float.NaN;
        }

        q = Math.Clamp(q, 0, 1);
        var pos = q * (sorted.Length - 1);
        var lo = (int)Math.Floor(pos);
        var hi = (int)Math.Ceiling(pos);
        var frac = pos - lo;
        return (float)(sorted[lo] + ((sorted[hi] - sorted[lo]) * frac));
    }

    public static float Median(RingBuffer<float> ring) => Quantile(ring, 0.5);
}
