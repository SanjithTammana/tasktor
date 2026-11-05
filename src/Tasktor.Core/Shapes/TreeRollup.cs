namespace Tasktor.Core.Shapes;

/// <summary>
/// Rooted n-ary forests (A D-A6): struct-of-arrays <c>parent</c>, <c>firstChild</c>, <c>nextSibling</c> and a memoised
/// <c>anchor</c> (the node a row rolls up to). The anchor rule is a parameter: A2 folds processes into apps; B folds
/// workers into the topmost watch-list ancestor; A5 / I7 sum folder sizes to a root. O(n) to resolve and to sum.
/// </summary>
public sealed class TreeRollup
{
    private int[] _parent = [];
    private int[] _firstChild = [];
    private int[] _nextSibling = [];
    private int[] _anchor = [];

    public int Count { get; private set; }

    /// <summary>Start a forest of <paramref name="n"/> nodes, all roots. Buffers are reused.</summary>
    public void Reset(int n)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(n);
        if (_parent.Length < n)
        {
            var cap = Math.Max(n, _parent.Length * 2);
            _parent = new int[cap];
            _firstChild = new int[cap];
            _nextSibling = new int[cap];
            _anchor = new int[cap];
        }

        Count = n;
        for (var i = 0; i < n; i++)
        {
            _parent[i] = -1;
            _firstChild[i] = -1;
            _nextSibling[i] = -1;
            _anchor[i] = -1;
        }
    }

    /// <summary>Set <paramref name="child"/>'s parent. The caller guarantees acyclicity (PID-reuse guard: parent created first).</summary>
    public void SetParent(int child, int parent)
    {
        if (parent < 0 || parent == child)
        {
            return;
        }

        _parent[child] = parent;
        _nextSibling[child] = _firstChild[parent];
        _firstChild[parent] = child;
    }

    public int Parent(int i) => _parent[i];

    public int Anchor(int i) => _anchor[i];

    public IEnumerable<int> Children(int i)
    {
        for (var c = _firstChild[i]; c >= 0; c = _nextSibling[c])
        {
            yield return c;
        }
    }

    /// <summary>
    /// Resolve every node's anchor once (memoised). <paramref name="foldsIntoParent"/>(i) says whether node i rolls
    /// up into its parent's anchor; otherwise it is its own anchor. A defensive step limit breaks any cycle.
    /// </summary>
    public void ResolveAnchors(Func<int, bool> foldsIntoParent)
    {
        ArgumentNullException.ThrowIfNull(foldsIntoParent);
        Span<int> stack = Count <= 512 ? stackalloc int[Count] : new int[Count];
        for (var i = 0; i < Count; i++)
        {
            if (_anchor[i] >= 0)
            {
                continue;
            }

            var depth = 0;
            var n = i;
            while (true)
            {
                if (_anchor[n] >= 0)
                {
                    break;
                }

                var p = _parent[n];
                if (p < 0 || depth >= Count || !foldsIntoParent(n))
                {
                    _anchor[n] = n;
                    break;
                }

                stack[depth++] = n;
                n = p;
            }

            var a = _anchor[n];
            while (depth > 0)
            {
                _anchor[stack[--depth]] = a;
            }
        }
    }

    /// <summary>Sum <paramref name="values"/> into each node's anchor: <paramref name="sums"/>[anchor] += values[i].</summary>
    public void SumToAnchors(ReadOnlySpan<double> values, Span<double> sums)
    {
        sums[..Count].Clear();
        for (var i = 0; i < Count; i++)
        {
            sums[_anchor[i]] += values[i];
        }
    }

    /// <summary>Subtree totals (each node's own value plus all descendants'), for folder trees. One reverse pass over a post-order.</summary>
    public void SubtreeTotals(ReadOnlySpan<double> values, Span<double> totals)
    {
        values[..Count].CopyTo(totals);
        Span<int> order = Count <= 512 ? stackalloc int[Count] : new int[Count];
        var k = 0;
        Span<int> stack = Count <= 512 ? stackalloc int[Count] : new int[Count];
        for (var r = 0; r < Count; r++)
        {
            if (_parent[r] >= 0)
            {
                continue;
            }

            var sp = 0;
            stack[sp++] = r;
            while (sp > 0)
            {
                var n = stack[--sp];
                order[k++] = n;
                for (var c = _firstChild[n]; c >= 0; c = _nextSibling[c])
                {
                    stack[sp++] = c;
                }
            }
        }

        for (var j = k - 1; j >= 0; j--)
        {
            var n = order[j];
            var p = _parent[n];
            if (p >= 0)
            {
                totals[p] += totals[n];
            }
        }
    }
}
