using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Windows;
using Tasktor.Core.Windows;

namespace Tasktor.Core.Tests;

/// <summary>F2 (U3 D-W3 / D-W4): the switcher's keys, search, app view and commit, with Resume first on a frozen window.</summary>
public sealed class SwitcherSessionTests
{
    private static WindowInfo W(long hwnd, string app, string title, uint pid = 1, bool frozen = false) =>
        new() { Hwnd = hwnd, Pid = pid, AppId = app, Exe = app, Title = Sensitive.Of(title), Frozen = frozen };

    // Enumeration (z) order differs from use order on purpose.
    private static readonly List<WindowInfo> Open =
    [
        W(1, "code.exe", "main.cs - project"),
        W(2, "chrome.exe", "Release notes - Browser", pid: 2),
        W(3, "chrome.exe", "Mail - Browser", pid: 2),
        W(4, "python.exe", "training run", pid: 40, frozen: true),
    ];

    private static readonly SwitcherFeed Feed = new()
    {
        Mru = [2, 1, 3],
        Jobs = [new SwitcherJob { Id = "j1", Label = "training", Frozen = true, Pids = [40] }],
    };

    private static SwitcherSession Session() => new(() => Open);

    [Fact]
    public void Opening_lists_windows_most_recent_first_and_selects_the_previous_one()
    {
        var s = Session();
        s.Open(Feed);
        Assert.True(s.IsOpen);
        Assert.Equal([2L, 1L, 3L, 4L], s.Tiles.Select(t => t.Window.Hwnd)); // never-focused windows follow in z order
        Assert.Equal(1, s.Selected); // like Alt+Tab: the window before the current one
        s.Next();
        s.Next();
        s.Next();
        Assert.Equal(0, s.Selected); // wraps
        s.Previous();
        Assert.Equal(3, s.Selected);

        var reverse = Session();
        reverse.Open(Feed, reverse: true);
        Assert.Equal(3, reverse.Selected); // Shift: start from the end
    }

    [Fact]
    public void Bring_here_moves_only_a_window_on_another_desktop_and_only_when_turned_on()
    {
        List<WindowInfo> windows = [W(1, "code.exe", "here"), W(2, "notes.exe", "there") with { Cloaked = Cloak.Shell }];
        var s = new SwitcherSession(() => windows);
        s.Open(new SwitcherFeed { Mru = [1, 2] });
        Assert.Equal(2, s.Tiles[s.Selected].Window.Hwnd);
        Assert.Null(s.BringHere()); // off by default (until spike W2 passes on this PC)
        Assert.True(s.IsOpen);
        Assert.DoesNotContain("brings it here", s.Describe(s.Tiles[s.Selected]), StringComparison.Ordinal);

        s.Open(new SwitcherFeed { Mru = [1, 2], BringHere = true });
        Assert.Contains("Ctrl+Enter brings it here", s.Describe(s.Tiles[s.Selected]), StringComparison.Ordinal);
        s.Previous(); // the window on this desktop
        Assert.Null(s.BringHere());
        s.Next();
        Assert.Equal(new SwitcherOutcome.BringHere(2), s.BringHere());
        Assert.False(s.IsOpen);
    }

    [Fact]
    public void Releasing_alt_commits_unless_typing_made_the_switcher_stay()
    {
        var s = Session();
        s.Open(Feed);
        Assert.Equal(new SwitcherOutcome.Activate(1), s.AltReleased());
        Assert.False(s.IsOpen);

        var typed = Session();
        typed.Open(Feed);
        typed.Type('m');
        typed.Type('a');
        typed.Type('i');
        Assert.Equal([1L, 3L], typed.Tiles.Select(t => t.Window.Hwnd)); // "mai": main.cs and Mail
        Assert.Equal(0, typed.Selected);
        Assert.Null(typed.AltReleased()); // typing makes it stay open
        typed.Backspace();
        typed.Backspace();
        typed.Backspace();
        Assert.Equal(4, typed.Tiles.Count);
        typed.Next();
        Assert.Equal(new SwitcherOutcome.Activate(1), typed.Commit());
    }

    [Fact]
    public void App_view_shows_one_apps_windows_and_toggles_back()
    {
        var s = Session();
        s.Open(Feed);
        s.Previous(); // onto chrome's window 2
        s.CycleView();
        Assert.Equal(SwitcherView.App, s.View);
        Assert.Equal([2L, 3L], s.Tiles.Select(t => t.Window.Hwnd));
        s.CycleView();
        Assert.Equal(SwitcherView.All, s.View);
        Assert.Equal(4, s.Tiles.Count);
    }

    [Fact]
    public void A_frozen_window_offers_resume_first_and_never_activates_silently()
    {
        var s = Session();
        s.Open(Feed);
        s.Previous();
        s.Previous(); // wraps to the last tile: the frozen training window
        Assert.True(s.Tiles[s.Selected].Badges.HasFlag(TileBadges.Frozen));
        Assert.Contains("training", s.Describe(s.Tiles[s.Selected]), StringComparison.Ordinal);

        Assert.Null(s.Commit()); // first Enter only asks
        Assert.Equal("training is frozen: Enter resumes it and switches, Esc cancels", s.Prompt);
        Assert.Equal(new SwitcherOutcome.ResumeThenActivate("j1", 4), s.Commit());
        Assert.False(s.IsOpen);

        var moved = Session();
        moved.Open(Feed);
        moved.Previous();
        moved.Previous();
        moved.Commit();
        moved.Next(); // moving away drops the question
        Assert.Null(moved.Prompt);
        Assert.Equal(new SwitcherOutcome.Activate(2), moved.Commit());
    }

    [Fact]
    public void Escape_and_an_empty_list_close_without_switching()
    {
        var s = Session();
        s.Open(Feed);
        s.Cancel();
        Assert.False(s.IsOpen);
        Assert.Null(s.Commit());

        var none = new SwitcherSession(() => []);
        none.Open(new SwitcherFeed());
        Assert.Equal(-1, none.Selected);
        Assert.Null(none.AltReleased());
        Assert.False(none.IsOpen);
    }

    [Fact]
    public void With_workflows_the_views_cycle_all_app_workflow_and_the_workflow_view_groups_windows()
    {
        var feed = Feed with { Workflows = new Dictionary<long, string> { [1] = "Thesis", [3] = "Email", [2] = "Thesis" } };
        var s = Session();
        s.Open(feed);
        s.CycleView();
        Assert.Equal(SwitcherView.App, s.View);
        s.CycleView();
        Assert.Equal(SwitcherView.Workflow, s.View);
        // MRU order 2, 1, 3, 4; grouped by each group's most recent window: Thesis (2, 1), Email (3), then Other (4).
        Assert.Equal([2L, 1L, 3L, 4L], s.Tiles.Select(t => t.Window.Hwnd));
        Assert.Equal(["Thesis", "Thesis", "Email", "Other"], s.Tiles.Select(t => t.Group));
        s.CycleView();
        Assert.Equal(SwitcherView.All, s.View);

        var none = Session();
        none.Open(Feed); // no workflows known: the view is skipped
        none.CycleView();
        none.CycleView();
        Assert.Equal(SwitcherView.All, none.View);
    }
}
