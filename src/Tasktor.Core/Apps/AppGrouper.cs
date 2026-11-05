using Tasktor.Contracts.Platform;
using Tasktor.Core.Shapes;

namespace Tasktor.Core.Apps;

/// <summary>One app group (A2): summed columns and its members.</summary>
public sealed class AppGroup
{
    public required string AppId { get; init; }

    public required string Name { get; init; }

    public double CpuPct { get; set; }

    public double PrivateBytes { get; set; }

    public double? GpuPct { get; set; }

    public bool AnyElevated { get; set; }

    public bool AllElevated { get; set; } = true;

    public bool IsSystem { get; init; }

    public List<ProcKey> Members { get; } = [];

    /// <summary>The group's root processes (anchors), for "end tree" and Close.</summary>
    public List<ProcKey> Roots { get; } = [];

    public int Count => Members.Count;
}

/// <summary>
/// The A2 grouping engine on <see cref="TreeRollup"/>: (1) same-exe child → parent; (2) helpers → nearest non-helper
/// ancestor; (3) tool processes → the launching app; (4) stop at launchers; (5) a parent counts only if it was created
/// before the child (PID-reuse guard); (6) no readable path and not your user → "System / services". User rules
/// (<c>attach: parent | never</c>, <c>group_as</c>) override the defaults.
/// </summary>
public sealed class AppGrouper
{
    public const string SystemAppId = "system";
    public const string SystemName = "System / services";

    private readonly AppIdentity _identity;
    private readonly TreeRollup _tree = new();
    private readonly Dictionary<uint, int> _byPid = [];
    private AppInfo[] _infos = [];

    public AppGrouper(AppIdentity identity) => _identity = identity;

    /// <summary>Group <paramref name="rows"/>; <paramref name="cpuPct"/> is parallel to rows (rates computed upstream).</summary>
    public List<AppGroup> Group(IReadOnlyList<ProcessRow> rows, ReadOnlySpan<double> cpuPct)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var n = rows.Count;
        _tree.Reset(n);
        _byPid.Clear();
        if (_infos.Length < n)
        {
            _infos = new AppInfo[Math.Max(n, _infos.Length * 2)];
        }

        for (var i = 0; i < n; i++)
        {
            _byPid[rows[i].Key.Pid] = i;
            _infos[i] = _identity.Resolve(rows[i].Exe, rows[i].Path, rows[i].Package);
        }

        for (var i = 0; i < n; i++)
        {
            var r = rows[i];
            if (r.ParentPid == r.Key.Pid || !_byPid.TryGetValue(r.ParentPid, out var p))
            {
                continue;
            }

            // Step 5: the parent counts only if it was created before the child (0 = unknown creation time).
            var pc = rows[p].Key.CreatedTicks;
            if (pc != 0 && r.Key.CreatedTicks != 0 && pc > r.Key.CreatedTicks)
            {
                continue;
            }

            _tree.SetParent(i, p);
        }

        _tree.ResolveAnchors(i => FoldsIntoParent(rows, i));

        var groups = new Dictionary<string, AppGroup>(StringComparer.Ordinal);
        for (var i = 0; i < n; i++)
        {
            var r = rows[i];
            var a = _tree.Anchor(i);
            var isSystem = IsSystemRow(rows[a]);
            var info = _infos[a];
            var id = isSystem ? SystemAppId : info.AppId;
            if (!groups.TryGetValue(id, out var g))
            {
                g = new AppGroup { AppId = id, Name = isSystem ? SystemName : info.Name, IsSystem = isSystem };
                groups[id] = g;
            }

            g.CpuPct += i < cpuPct.Length ? cpuPct[i] : 0;
            g.PrivateBytes += r.PrivateBytes;
            if (r.GpuPct is { } gpu)
            {
                g.GpuPct = (g.GpuPct ?? 0) + gpu;
            }

            g.AnyElevated |= r.Elevated;
            g.AllElevated &= r.Elevated;
            g.Members.Add(r.Key);
            if (a == i)
            {
                g.Roots.Add(r.Key);
            }
        }

        return [.. groups.Values];
    }

    /// <summary>The anchor index of row <paramref name="i"/> after the last <see cref="Group"/>.</summary>
    public int AnchorOf(int i) => _tree.Anchor(i);

    private bool FoldsIntoParent(IReadOnlyList<ProcessRow> rows, int i)
    {
        var p = _tree.Parent(i);
        var me = _infos[i];
        var parent = _infos[p];
        if (IsSystemRow(rows[i]))
        {
            return false;
        }

        if (me.Rule?.GroupAs is not null)
        {
            // A named group stays itself, but its same-group children fold into it.
            return string.Equals(parent.AppId, me.AppId, StringComparison.Ordinal);
        }

        if (string.Equals(me.Rule?.Attach, "never", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (parent.Role == AppRole.Launcher || IsSystemRow(rows[p]))
        {
            return false; // step 4
        }

        if (string.Equals(me.Rule?.Attach, "parent", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(rows[i].Exe, rows[p].Exe, StringComparison.OrdinalIgnoreCase))
        {
            return true; // step 1
        }

        return me.Role is AppRole.Helper or AppRole.Tool; // steps 2 and 3
    }

    private static bool IsSystemRow(ProcessRow r) => (r.Path is null && !r.OwnUser) || r.Key.Pid is 0 or 4;
}
