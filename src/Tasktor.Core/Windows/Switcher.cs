namespace Tasktor.Core.Windows;

/// <summary>
/// D-W4's window list: the Alt+Tab-equivalent filter (visible, top-level, not cloaked by its app, not a tool window,
/// unowned unless it asks for a taskbar button, activatable) and most-recently-used order from foreground events.
/// Titles stay in memory; nothing here is stored.
/// </summary>
public sealed class WindowList
{
    private readonly LinkedList<long> _mru = new();
    private readonly Dictionary<long, LinkedListNode<long>> _nodes = [];

    /// <summary>Alt+Tab rule (after Raymond Chen's description): what the native switcher lists.</summary>
    public static bool InAltTab(WindowInfo w, IReadOnlyDictionary<long, WindowInfo> byHwnd, bool thisDesktopOnly)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(byHwnd);
        if (!w.Visible || w.NoActivate || w.Cloaked is Cloak.App or Cloak.Inherited)
        {
            return false;
        }

        if (w.Cloaked == Cloak.Shell && thisDesktopOnly)
        {
            return false; // another virtual desktop (listed with a badge unless "this desktop only")
        }

        if (w.ToolWindow && !w.AppWindow)
        {
            return false;
        }

        // An owned window is listed only when it asks for a taskbar button or its owner is not itself listed.
        if (w.Owner != 0 && !w.AppWindow && byHwnd.TryGetValue(w.Owner, out var owner) && owner.Visible)
        {
            return false;
        }

        return true;
    }

    /// <summary>A window came to the foreground.</summary>
    public void Touch(long hwnd)
    {
        if (_nodes.TryGetValue(hwnd, out var node))
        {
            _mru.Remove(node);
            _mru.AddFirst(node);
        }
        else
        {
            _nodes[hwnd] = _mru.AddFirst(hwnd);
        }
    }

    public void Remove(long hwnd)
    {
        if (_nodes.Remove(hwnd, out var node))
        {
            _mru.Remove(node);
        }
    }

    public int Count => _nodes.Count;

    /// <summary>The handles, most recent first, at most <paramref name="max"/>.</summary>
    public IReadOnlyList<long> Recent(int max = int.MaxValue) => [.. _mru.Take(max)];

    /// <summary>Forget the least recent windows beyond <paramref name="max"/> (a closed window's handle may never be seen again).</summary>
    public void Trim(int max)
    {
        while (_nodes.Count > max && _mru.Last is { } last)
        {
            Remove(last.Value);
        }
    }

    public int Rank(long hwnd)
    {
        var i = 0;
        for (var n = _mru.First; n is not null; n = n.Next, i++)
        {
            if (n.Value == hwnd)
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    /// <summary>The switcher's list: filtered, MRU first; windows never seen in the foreground follow in enumeration (z) order.</summary>
    public List<WindowInfo> Order(IReadOnlyList<WindowInfo> enumerated, bool thisDesktopOnly = false)
    {
        ArgumentNullException.ThrowIfNull(enumerated);
        var byHwnd = enumerated.ToDictionary(w => w.Hwnd);
        foreach (var gone in _nodes.Keys.Where(h => !byHwnd.ContainsKey(h)).ToList())
        {
            Remove(gone);
        }

        return [.. enumerated
            .Select((w, z) => (w, z))
            .Where(x => InAltTab(x.w, byHwnd, thisDesktopOnly))
            .OrderBy(x => Rank(x.w.Hwnd))
            .ThenBy(x => x.z)
            .Select(x => x.w)];
    }
}

public enum SwitcherView : byte
{
    All,
    App,
    Workflow,
}

/// <summary>Badges a tile shows (D-W4): a job row, frozen (Resume first), on another desktop, admin.</summary>
[Flags]
public enum TileBadges
{
    None = 0,
    Job = 1,
    Frozen = 2,
    OtherDesktop = 4,
    Admin = 8,
}

public sealed record SwitcherTile(WindowInfo Window, TileBadges Badges, string? Group);

/// <summary>
/// The switcher's search and views: typing filters by title and app name (every word must match the start of a word,
/// or appear anywhere for 3+ characters), app view shows one app's windows, workflow view groups by C's per-window
/// workflow (hidden without an active model). At most <see cref="MaxTiles"/> tiles are drawn; the rest scroll.
/// </summary>
public static class SwitcherFilter
{
    public const int MaxTiles = 24;

    public static List<SwitcherTile> Tiles(
        IReadOnlyList<WindowInfo> ordered,
        string? query,
        SwitcherView view,
        string? appId,
        Func<long, string?> workflowOf,
        Func<uint, bool> hasJob)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        ArgumentNullException.ThrowIfNull(workflowOf);
        ArgumentNullException.ThrowIfNull(hasJob);
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(w => w.ToLowerInvariant()).ToList();
        var tiles = new List<SwitcherTile>();
        foreach (var w in ordered)
        {
            if (view == SwitcherView.App && !string.Equals(w.AppId, appId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (words.Count > 0 && !words.All(q => Matches(q, w)))
            {
                continue;
            }

            var badges = (hasJob(w.Pid) ? TileBadges.Job : TileBadges.None)
                | (w.Frozen ? TileBadges.Frozen : TileBadges.None)
                | (!w.OnCurrentDesktop || w.Cloaked == Cloak.Shell ? TileBadges.OtherDesktop : TileBadges.None)
                | (w.Elevated ? TileBadges.Admin : TileBadges.None);
            tiles.Add(new SwitcherTile(w, badges, view == SwitcherView.Workflow ? workflowOf(w.Hwnd) ?? "Other" : null));
        }

        if (view == SwitcherView.Workflow)
        {
            // Groups in the order their first (most recent) window appears; MRU order kept inside each group.
            var firstSeen = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < tiles.Count; i++)
            {
                firstSeen.TryAdd(tiles[i].Group!, i);
            }

            tiles = [.. tiles.Select((t, i) => (t, i)).OrderBy(x => firstSeen[x.t.Group!]).ThenBy(x => x.i).Select(x => x.t)];
        }

        return tiles;
    }

    private static bool Matches(string q, WindowInfo w)
    {
        var app = (w.Exe ?? w.AppId).ToLowerInvariant();
        var title = w.Title.Reveal().ToLowerInvariant();
        foreach (var text in new[] { title, app })
        {
            if (q.Length >= 3 && text.Contains(q, StringComparison.Ordinal))
            {
                return true;
            }

            foreach (var word in text.Split([' ', '-', '_', '.', '|', '—', '/', '\\'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.StartsWith(q, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The switcher opens on the monitor with the cursor (D-W8).</summary>
    public static MonitorInfo MonitorFor(int cursorX, int cursorY, MonitorMap monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        return monitors.Of(new RectPx(cursorX, cursorY, 1, 1));
    }
}
