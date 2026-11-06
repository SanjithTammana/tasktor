namespace Tasktor.Capture.Deep;

/// <summary>C §3: a tab, a document or a panel. Browser views are (window, tab id as the root ref).</summary>
public readonly record struct ViewKey(long Window, ulong RootRef);

/// <summary>C §3: one visible element of a walk. Strings live in the walk's arena (<see cref="UiaWalk.Strings"/>); -1 = none.</summary>
public readonly record struct SnapLine(ulong Ref, byte Depth, byte Role, int NameId, int ValueId);

/// <summary>What <c>UiaWalkSource</c> emits (C §11, A-C4): plain data, no COM. Values are never read for password fields.</summary>
public sealed record UiaWalk(ViewKey View, IReadOnlyList<SnapLine> Lines, IReadOnlyList<string> Strings)
{
    public string? Text(int id) => id >= 0 && id < Strings.Count ? Strings[id] : null;
}

public enum DiffSource : byte
{
    Uia,
    Extension,
    Ocr,
}

/// <summary>A line as stored: strings resolved from the arena.</summary>
public sealed record DiffLine(ulong Ref, byte Depth, byte Role, string? Name, string? Value);

/// <summary>
/// C §3: the unit stored in the vault. <see cref="Seq"/> numbers this view's records; <see cref="Base"/> is the record it
/// applies to. A <see cref="Baseline"/> carries every line (first sight, or the re-baseline rule) and removes nothing.
/// </summary>
public sealed record ViewDiff(ViewKey View, DateTimeOffset At, long Seq, long Base, IReadOnlyList<DiffLine> Added, IReadOnlyList<ulong> Removed, DiffSource Source)
{
    public bool Baseline { get; init; }
}

/// <summary>
/// C §4 <c>RefMinter</c>: a stable element id across walks, xxHash64 of (parent ref, identity, sibling index), where the
/// identity is the AutomationId when there is one, else role + name. The sibling index counts earlier siblings with the
/// same identity, so inserting an unrelated sibling does not renumber the rest. Collisions only cost a spurious ±line.
/// </summary>
public static class RefMinter
{
    public static ulong Ref(ulong parentRef, string? automationId, byte role, string? name, int siblingIndex)
    {
        Span<byte> head = stackalloc byte[13];
        BitConverter.TryWriteBytes(head, parentRef);
        BitConverter.TryWriteBytes(head[8..], siblingIndex);
        var hasId = !string.IsNullOrEmpty(automationId);
        head[12] = hasId ? (byte)0xFF : role;
        var h = XxHash64.Hash(head);
        return XxHash64.Hash(hasId ? automationId : name, h);
    }
}

/// <summary>Myers O((N+M)·D) diff on equality-comparable keys: which items of each side are kept (common).</summary>
public static class Myers
{
    /// <summary>Returns false when the edit distance exceeds <paramref name="maxD"/> (the caller re-baselines instead).</summary>
    public static bool Diff<T>(IReadOnlyList<T> a, IReadOnlyList<T> b, int maxD, out bool[] keptA, out bool[] keptB)
        where T : IEquatable<T>
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        int n = a.Count, m = b.Count, offset = n + m + 1;
        keptA = new bool[n];
        keptB = new bool[m];
        var v = new int[(2 * offset) + 1];
        var trace = new List<int[]>();
        var limit = Math.Min(maxD, n + m);
        for (var d = 0; d <= limit; d++)
        {
            trace.Add((int[])v.Clone());
            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]) ? v[offset + k + 1] : v[offset + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && a[x].Equals(b[y]))
                {
                    x++;
                    y++;
                }

                v[offset + k] = x;
                if (x >= n && y >= m)
                {
                    Backtrack(trace, offset, n, m, keptA, keptB);
                    return true;
                }
            }
        }

        return false;
    }

    private static void Backtrack(List<int[]> trace, int offset, int n, int m, bool[] keptA, bool[] keptB)
    {
        int x = n, y = m;
        for (var d = trace.Count - 1; d >= 0; d--)
        {
            var v = trace[d];
            var k = x - y;
            var prevK = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]) ? k + 1 : k - 1;
            var prevX = d == 0 ? 0 : v[offset + prevK];
            var prevY = prevX - prevK;
            while (x > prevX && y > prevY)
            {
                keptA[--x] = true;
                keptB[--y] = true;
            }

            if (d > 0)
            {
                (x, y) = (prevX, prevY);
            }
        }
    }
}

/// <summary>
/// C §4 / §5.4 <c>SnapshotDiffer</c>: the last snapshot per view (≤ 64 live, least recently used evicted), Myers on
/// (ref, text hash). Null when nothing changed. Re-baselines when the change exceeds 60 % of the lines or every 50 diffs.
/// Churn filter: a ref whose text changed ≥ 10 times in 60 s is volatile; its changes are withheld and only its last
/// value is emitted when the activity closes (its first value went out before it turned volatile). Logic thread only.
/// </summary>
public sealed class SnapshotDiffer
{
    public const int MaxViews = 64;
    public const double RebaselineShare = 0.6;
    public const int RebaselineEvery = 50;
    public const int VolatileChanges = 10;
    public static readonly TimeSpan VolatileWindow = TimeSpan.FromSeconds(60);

    private readonly Dictionary<ViewKey, LinkedListNode<ViewState>> _views = [];
    private readonly LinkedList<ViewState> _lru = new();

    public int LiveViews => _views.Count;

    private readonly record struct LineKey(ulong Ref, ulong TextHash) : IEquatable<LineKey>;

    private sealed class ViewState(ViewKey view)
    {
        public ViewKey View { get; } = view;

        public LineKey[] Keys { get; set; } = [];

        public DiffLine[] Lines { get; set; } = [];

        public long Seq { get; set; }

        public int SinceBaseline { get; set; }

        public Dictionary<ulong, Queue<DateTimeOffset>> Changes { get; } = [];

        public HashSet<ulong> Volatile { get; } = [];

        public HashSet<ulong> Withheld { get; } = [];
    }

    public ViewDiff? Diff(UiaWalk walk, DateTimeOffset at, DiffSource source = DiffSource.Uia)
    {
        ArgumentNullException.ThrowIfNull(walk);
        var lines = walk.Lines.Select(l => new DiffLine(l.Ref, l.Depth, l.Role, walk.Text(l.NameId), walk.Text(l.ValueId))).ToArray();
        var keys = lines.Select(l => new LineKey(l.Ref, XxHash64.Hash(l.Name + "\u001f" + l.Value))).ToArray();
        var state = Touch(walk.View);
        var old = state.Keys;
        (state.Keys, state.Lines) = (keys, lines);

        if (state.Seq == 0)
        {
            return Baseline(state, at, source); // first sight (or seen again after eviction)
        }

        var size = Math.Max(old.Length, keys.Length);
        var maxD = (int)Math.Floor(RebaselineShare * size); // D = added + removed, so this is the 60 % rule
        if (!Myers.Diff(old, keys, maxD, out var keptOld, out var keptNew) || state.SinceBaseline >= RebaselineEvery)
        {
            return Baseline(state, at, source);
        }

        var removed = new List<ulong>();
        for (var i = 0; i < old.Length; i++)
        {
            if (!keptOld[i])
            {
                removed.Add(old[i].Ref);
            }
        }

        var added = new List<DiffLine>();
        for (var i = 0; i < keys.Length; i++)
        {
            if (!keptNew[i])
            {
                added.Add(lines[i]);
            }
        }

        // Churn: a ref removed and re-added in one diff changed its text.
        var addedRefs = added.Select(l => l.Ref).ToHashSet();
        foreach (var r in removed.Where(addedRefs.Contains).Distinct().ToArray())
        {
            if (Changed(state, r, at))
            {
                removed.RemoveAll(x => x == r);
                added.RemoveAll(l => l.Ref == r);
                state.Withheld.Add(r);
            }
        }

        if (added.Count == 0 && removed.Count == 0)
        {
            return null;
        }

        state.SinceBaseline++;
        var seq = ++state.Seq;
        return new ViewDiff(state.View, at, seq, seq - 1, added, removed, source);
    }

    /// <summary>The activity closed: each view's withheld volatile refs, at their current values (one diff per view).</summary>
    public IReadOnlyList<ViewDiff> CloseActivity(DateTimeOffset at, DiffSource source = DiffSource.Uia)
    {
        var diffs = new List<ViewDiff>();
        foreach (var state in _lru)
        {
            if (state.Withheld.Count > 0)
            {
                var last = state.Lines.Where(l => state.Withheld.Contains(l.Ref)).ToArray();
                var gone = state.Withheld.Where(r => !last.Any(l => l.Ref == r)).ToArray();
                state.Withheld.Clear();
                var seq = ++state.Seq;
                diffs.Add(new ViewDiff(state.View, at, seq, seq - 1, last, [.. last.Select(l => l.Ref), .. gone], source));
            }

            state.Changes.Clear();
            state.Volatile.Clear();
        }

        return diffs;
    }

    /// <summary>The view closed (tab closed, window destroyed).</summary>
    public void Forget(ViewKey view)
    {
        if (_views.Remove(view, out var node))
        {
            _lru.Remove(node);
        }
    }

    private ViewState Touch(ViewKey view)
    {
        if (_views.TryGetValue(view, out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
            return node.Value;
        }

        if (_views.Count >= MaxViews && _lru.Last is { } oldest)
        {
            _views.Remove(oldest.Value.View);
            _lru.RemoveLast();
        }

        var created = _lru.AddFirst(new ViewState(view));
        _views[view] = created;
        return created.Value;
    }

    private static ViewDiff Baseline(ViewState state, DateTimeOffset at, DiffSource source)
    {
        state.SinceBaseline = 0;
        state.Withheld.Clear(); // a baseline carries every current value
        var seq = ++state.Seq;
        return new ViewDiff(state.View, at, seq, seq, state.Lines, [], source) { Baseline = true };
    }

    /// <summary>Record one text change of <paramref name="r"/>; true when the ref is (now) volatile.</summary>
    private static bool Changed(ViewState state, ulong r, DateTimeOffset at)
    {
        if (state.Volatile.Contains(r))
        {
            return true;
        }

        if (!state.Changes.TryGetValue(r, out var q))
        {
            state.Changes[r] = q = new Queue<DateTimeOffset>();
        }

        q.Enqueue(at);
        while (q.Count > 0 && at - q.Peek() > VolatileWindow)
        {
            q.Dequeue();
        }

        if (q.Count >= VolatileChanges)
        {
            state.Volatile.Add(r);
            state.Changes.Remove(r);
            return true;
        }

        return false;
    }
}

/// <summary>An edit to a field: the inserted text and how many characters were deleted.</summary>
public readonly record struct FieldEdit(string Inserted, int Deleted);

/// <summary>
/// C §5.3 <c>FieldTruth</c>: for edits that expose their value (≤ 8 KB), the stored typed text is the value diff, not the
/// key decode (IME, autocomplete, paste, dictation). Common prefix / suffix diff; logic thread only.
/// </summary>
public sealed class FieldTruth
{
    public const int MaxChars = 8 * 1024;

    private string? _held;

    /// <summary>True while a value is held (the field exposes one, within the limit).</summary>
    public bool Holding => _held is not null;

    /// <summary>Focus landed on an edit: hold its value. False (keys are the fallback) when there is none or it is too long.</summary>
    public bool Hold(string? value)
    {
        _held = value is { Length: <= MaxChars } ? value : null;
        return _held is not null;
    }

    public void Release() => _held = null;

    /// <summary>The value changed (run close, or TextChanged debounced): what was inserted since the held value.</summary>
    public FieldEdit? Diff(string? after)
    {
        if (_held is not { } before || after is not { Length: <= MaxChars })
        {
            _held = null;
            return null;
        }

        var p = 0;
        var max = Math.Min(before.Length, after.Length);
        while (p < max && before[p] == after[p])
        {
            p++;
        }

        var s = 0;
        while (s < max - p && before[^(s + 1)] == after[^(s + 1)])
        {
            s++;
        }

        _held = after;
        var inserted = after[p..(after.Length - s)];
        var deleted = before.Length - p - s;
        return inserted.Length == 0 && deleted == 0 ? null : new FieldEdit(inserted, deleted);
    }
}
