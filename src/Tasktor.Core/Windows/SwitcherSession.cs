using Tasktor.Contracts.Windows;

namespace Tasktor.Core.Windows;

/// <summary>What choosing a tile asks the shell to do.</summary>
public abstract record SwitcherOutcome
{
    private SwitcherOutcome()
    {
    }

    /// <summary>Bring the window to the front (the shell received the key press, so it may set the foreground window).</summary>
    public sealed record Activate(long Hwnd) : SwitcherOutcome;

    /// <summary>The window belongs to a frozen job: run <c>job.resume</c> for it first, then bring the window forward.</summary>
    public sealed record ResumeThenActivate(string JobId, long Hwnd) : SwitcherOutcome;

    /// <summary>F4: move the window from another virtual desktop to this one, then bring it forward (public API only).</summary>
    public sealed record BringHere(long Hwnd) : SwitcherOutcome;
}

/// <summary>
/// F2 (U3 D-W3 / D-W4): one opening of the switcher, as pure logic for <c>tasktor-shell</c>. Opening enumerates the
/// windows (in the shell's own memory; titles never leave it), orders them by the agent's most-recently-used handles and
/// selects the one before the current window, like Alt+Tab. The hotkey again moves on, Shift moves back; releasing Alt
/// chooses, unless the user typed (then it stays open for Enter). Typing filters by title and app; the app view shows
/// the selected app's windows. A frozen job's window offers Resume first and is never activated silently.
/// </summary>
public sealed class SwitcherSession
{
    private readonly Func<IReadOnlyList<WindowInfo>> _enumerate;
    private List<WindowInfo> _ordered = [];
    private Dictionary<uint, SwitcherJob> _jobByPid = [];
    private IReadOnlyDictionary<long, string> _workflows = new Dictionary<long, string>();
    private string? _appId;
    private bool _stay;
    private long? _asked;
    private bool _bringHere;

    public SwitcherSession(Func<IReadOnlyList<WindowInfo>> enumerate) => _enumerate = enumerate;

    public bool IsOpen { get; private set; }

    public string Query { get; private set; } = string.Empty;

    public SwitcherView View { get; private set; } = SwitcherView.All;

    public IReadOnlyList<SwitcherTile> Tiles { get; private set; } = [];

    /// <summary>The selected tile's index, or -1 when there is none.</summary>
    public int Selected { get; private set; } = -1;

    /// <summary>A question the user must answer with Enter or Esc (Resume first), else null.</summary>
    public string? Prompt { get; private set; }

    public event Action? Changed;

    public void Open(SwitcherFeed feed, bool reverse = false)
    {
        ArgumentNullException.ThrowIfNull(feed);
        _workflows = feed.Workflows;
        _bringHere = feed.BringHere;
        _jobByPid = [];
        foreach (var job in feed.Jobs)
        {
            foreach (var pid in job.Pids)
            {
                _jobByPid.TryAdd(pid, job);
            }
        }

        var list = new WindowList();
        for (var i = feed.Mru.Count - 1; i >= 0; i--)
        {
            list.Touch(feed.Mru[i]);
        }

        var frozen = _jobByPid.Where(p => p.Value.Frozen).Select(p => p.Key).ToHashSet();
        _ordered = list.Order([.. _enumerate().Select(w => w.Frozen || !frozen.Contains(w.Pid) ? w : w with { Frozen = true })], feed.ThisDesktopOnly);
        (Query, View, _appId, _stay, _asked, Prompt) = (string.Empty, SwitcherView.All, null, false, null, null);
        IsOpen = true;
        Refilter();
        Selected = Tiles.Count switch
        {
            0 => -1,
            1 => 0,
            _ => reverse ? Tiles.Count - 1 : 1,
        };
        if (Tiles.Count == 0)
        {
            IsOpen = false;
        }

        Changed?.Invoke();
    }

    public void Next() => Move(+1);

    public void Previous() => Move(-1);

    public void Type(char c)
    {
        if (!IsOpen || char.IsControl(c))
        {
            return;
        }

        _stay = true;
        Query += c;
        Refilter(resetSelection: true);
    }

    public void Backspace()
    {
        if (!IsOpen || Query.Length == 0)
        {
            return;
        }

        Query = Query[..^1];
        Refilter(resetSelection: true);
    }

    /// <summary>
    /// The next view: all windows → the selected tile's app (F1's useful part) → by workflow (only when some window has
    /// one, D-W4) → all windows.
    /// </summary>
    public void CycleView()
    {
        if (!IsOpen)
        {
            return;
        }

        if (View == SwitcherView.All && Current is { } tile)
        {
            (View, _appId) = (SwitcherView.App, tile.Window.AppId);
        }
        else if (View == SwitcherView.App && _workflows.Count > 0)
        {
            (View, _appId) = (SwitcherView.Workflow, null);
        }
        else
        {
            (View, _appId) = (SwitcherView.All, null);
        }

        _stay = true;
        Refilter(resetSelection: true);
    }

    /// <summary>Alt was released: choose, unless the user typed or switched views (then Enter chooses).</summary>
    public SwitcherOutcome? AltReleased() => !IsOpen || _stay ? null : Commit();

    /// <summary>Enter (or Alt released): the outcome, or null while a Resume-first question waits.</summary>
    public SwitcherOutcome? Commit()
    {
        if (!IsOpen || Current is not { } tile)
        {
            IsOpen = false;
            return null;
        }

        var hwnd = tile.Window.Hwnd;
        if (tile.Badges.HasFlag(TileBadges.Frozen) && _jobByPid.TryGetValue(tile.Window.Pid, out var job))
        {
            if (_asked != hwnd)
            {
                _asked = hwnd;
                _stay = true;
                Prompt = job.Label + " is frozen: Enter resumes it and switches, Esc cancels";
                Changed?.Invoke();
                return null;
            }

            Close();
            return new SwitcherOutcome.ResumeThenActivate(job.Id, hwnd);
        }

        Close();
        return new SwitcherOutcome.Activate(hwnd);
    }

    /// <summary>
    /// F4 (D-W7), Ctrl+Enter: the selected window is on another desktop and "bring here" is on (Settings, only after spike
    /// W2 passed on this PC): close and ask the shell to move it here. Null otherwise (nothing happens). A frozen window
    /// still goes through Enter's Resume first.
    /// </summary>
    public SwitcherOutcome? BringHere()
    {
        if (!IsOpen || !_bringHere || Current is not { } tile || !tile.Badges.HasFlag(TileBadges.OtherDesktop) || tile.Badges.HasFlag(TileBadges.Frozen))
        {
            return null;
        }

        Close();
        return new SwitcherOutcome.BringHere(tile.Window.Hwnd);
    }

    public void Cancel() => Close();

    /// <summary>A tile's line under its thumbnail: the title, the app, and its badges in words.</summary>
    public string Describe(SwitcherTile tile)
    {
        ArgumentNullException.ThrowIfNull(tile);
        var w = tile.Window;
        var parts = new List<string>();
        if (tile.Group is { } group)
        {
            parts.Add(group); // the workflow view: each tile says which group it is in
        }

        parts.Add(w.Title.Reveal() is { Length: > 0 } t ? t : w.Exe ?? w.AppId);
        if (_jobByPid.TryGetValue(w.Pid, out var job) && tile.Badges.HasFlag(TileBadges.Job))
        {
            parts.Add(job.Label);
        }

        if (tile.Badges.HasFlag(TileBadges.Frozen))
        {
            parts.Add("frozen");
        }

        if (tile.Badges.HasFlag(TileBadges.OtherDesktop))
        {
            parts.Add(_bringHere ? "on another desktop (Ctrl+Enter brings it here)" : "on another desktop");
        }

        if (tile.Badges.HasFlag(TileBadges.Admin))
        {
            parts.Add("admin");
        }

        return string.Join(" · ", parts);
    }

    private SwitcherTile? Current => Selected >= 0 && Selected < Tiles.Count ? Tiles[Selected] : null;

    private void Move(int by)
    {
        if (!IsOpen || Tiles.Count == 0)
        {
            return;
        }

        Selected = ((Selected + by) % Tiles.Count + Tiles.Count) % Tiles.Count;
        (_asked, Prompt) = (null, null);
        Changed?.Invoke();
    }

    private void Refilter(bool resetSelection = false)
    {
        Tiles = SwitcherFilter.Tiles(_ordered, Query, View, _appId, hwnd => _workflows.TryGetValue(hwnd, out var w) ? w : null, pid => _jobByPid.ContainsKey(pid));
        if (resetSelection)
        {
            Selected = Tiles.Count > 0 ? 0 : -1;
            (_asked, Prompt) = (null, null);
            Changed?.Invoke();
        }
    }

    private void Close()
    {
        IsOpen = false;
        (_asked, Prompt) = (null, null);
        Changed?.Invoke();
    }
}
