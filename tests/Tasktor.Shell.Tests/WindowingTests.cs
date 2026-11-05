using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Presets;
using Tasktor.Shell.Windowing;

namespace Tasktor.Shell.Tests;

/// <summary>U3 logic on Linux: monitors and DIPs (D-W8), fullscreen (D-W9), the window list (D-W4), presets (D-W5, D-W6).</summary>
public sealed class WindowingTests
{
    // Primary 1920×1080 at 100% (taskbar 48 px); a second 2560×1440 at 150% to its right.
    private static readonly MonitorInfo Left = new(@"\\.\DISPLAY1", new RectPx(0, 0, 1920, 1080), new RectPx(0, 0, 1920, 1032), 96, true);
    private static readonly MonitorInfo Right = new(@"\\.\DISPLAY2", new RectPx(1920, 0, 2560, 1440), new RectPx(1920, 0, 2560, 1392), 144, false);
    private static readonly MonitorMap Two = new([Left, Right]);

    private static WindowInfo Win(long hwnd, string app, RectPx frame, string? cls = "Chrome_WidgetWin_1", string title = "", uint pid = 0) => new()
    {
        Hwnd = hwnd,
        Pid = pid == 0 ? (uint)hwnd : pid,
        AppId = app,
        Exe = app,
        Class = cls,
        Title = Sensitive.Of(title),
        FramePx = frame,
        WindowPx = new RectPx(frame.X - 7, frame.Y, frame.W + 14, frame.H + 7), // Windows 11's invisible resize border
    };

    [Fact]
    public void Dips_are_relative_to_the_work_area_and_scale_with_each_monitors_dpi()
    {
        var px = new RectPx(1920 + 150, 300, 1200, 900);
        var dip = MonitorMap.ToDip(px, Right);
        Assert.Equal((100.0, 200.0, 800.0, 600.0), (dip.X, dip.Y, dip.W, dip.H));
        Assert.Equal(px, MonitorMap.FromDip(dip, Right));
        Assert.Equal(new RectPx(100, 200, 800, 600), MonitorMap.FromDip(dip, Left)); // same DIPs on a 100% monitor
        Assert.Same(Right, Two.Of(new RectPx(1800, 100, 400, 400))); // mostly on the right one
        Assert.Same(Left, Two.Of(new RectPx(-5000, -5000, 10, 10))); // off every monitor → nearest
    }

    [Fact]
    public void A_missing_monitor_falls_back_to_the_primary_scaled_to_fit_and_never_off_screen()
    {
        var place = new PresetPlace { Monitor = @"\\.\DISPLAY9", Rect = new DipRect { X = 1500, Y = 900, W = 2400, H = 1300 } };
        var (m, r) = Two.Resolve(place);
        Assert.Same(Left, m);
        Assert.True(r.W <= Left.WorkPx.W && r.H <= Left.WorkPx.H);
        Assert.True(Left.WorkPx.Covers(r, 0));
        var (m2, r2) = Two.Resolve(new PresetPlace { Monitor = @"\\.\display2", Rect = new DipRect { X = 1700, Y = 0, W = 400, H = 300 } });
        Assert.Same(Right, m2);
        Assert.Equal(Right.WorkPx.Right, r2.Right); // clamped inside, not off the right edge
    }

    [Fact]
    public void The_monitor_fingerprint_is_order_and_case_independent_and_changes_with_dpi()
    {
        var a = new MonitorMap([Right, Left]).Fingerprint();
        Assert.Equal(Two.Fingerprint(), a);
        Assert.Equal(Two.Fingerprint(), new MonitorMap([Left with { DevicePath = @"\\.\display1" }, Right]).Fingerprint());
        Assert.NotEqual(Two.Fingerprint(), new MonitorMap([Left, Right with { Dpi = 192 }]).Fingerprint());
    }

    [Theory]
    [InlineData(NotificationState.D3dFullScreen, false, FullscreenClassifier.Exclusive)]
    [InlineData(NotificationState.PresentationMode, false, FullscreenClassifier.Presentation)]
    [InlineData(NotificationState.Busy, true, FullscreenClassifier.Borderless)]
    [InlineData(NotificationState.AcceptsNotifications, true, FullscreenClassifier.Borderless)]
    [InlineData(NotificationState.AcceptsNotifications, false, FullscreenClassifier.None)]
    public void Fullscreen_is_exclusive_presentation_borderless_or_none(NotificationState state, bool covers, string expected)
    {
        var fg = Win(1, "game.exe", covers ? Right.BoundsPx : new RectPx(2000, 100, 1200, 800), "GameWindow");
        Assert.Equal(expected, FullscreenClassifier.Classify(state, fg, Two));
    }

    [Fact]
    public void The_desktop_and_a_minimized_window_are_never_fullscreen()
    {
        Assert.Equal(FullscreenClassifier.None, FullscreenClassifier.Classify(NotificationState.AcceptsNotifications, Win(1, "explorer.exe", Left.BoundsPx, "Progman"), Two));
        Assert.Equal(FullscreenClassifier.None, FullscreenClassifier.Classify(NotificationState.AcceptsNotifications, Win(2, "x.exe", Left.BoundsPx) with { Show = ShowState.Minimized }, Two));
        Assert.Equal(FullscreenClassifier.None, FullscreenClassifier.Classify(NotificationState.AcceptsNotifications, null, Two));
    }

    [Fact]
    public void The_list_follows_alt_tab_rules_and_most_recent_use()
    {
        var main = Win(10, "code.exe", new RectPx(0, 0, 800, 600));
        var dialog = Win(11, "code.exe", new RectPx(0, 0, 300, 200)) with { Owner = 10 };
        var ownedApp = Win(12, "code.exe", new RectPx(0, 0, 300, 200)) with { Owner = 10, AppWindow = true };
        var tool = Win(13, "tool.exe", new RectPx(0, 0, 10, 10)) with { ToolWindow = true };
        var cloakedByApp = Win(14, "app.exe", new RectPx(0, 0, 10, 10)) with { Cloaked = Cloak.App };
        var otherDesktop = Win(15, "chrome.exe", new RectPx(0, 0, 10, 10)) with { Cloaked = Cloak.Shell, OnCurrentDesktop = false };
        var hidden = Win(16, "x.exe", new RectPx(0, 0, 10, 10)) with { Visible = false };
        var chrome = Win(17, "chrome.exe", new RectPx(0, 0, 10, 10));
        var all = new[] { main, dialog, ownedApp, tool, cloakedByApp, otherDesktop, hidden, chrome };

        var list = new WindowList();
        list.Touch(10);
        list.Touch(17);
        Assert.Equal([17L, 10, 12, 15], list.Order(all).Select(w => w.Hwnd));
        Assert.Equal([17L, 10, 12], list.Order(all, thisDesktopOnly: true).Select(w => w.Hwnd));
        list.Touch(10);
        Assert.Equal(10, list.Order(all)[0].Hwnd);
        Assert.Equal(int.MaxValue, new WindowList().Rank(10));
        list.Order([chrome]); // 10 closed: forgotten
        Assert.Equal(int.MaxValue, list.Rank(10));
    }

    [Fact]
    public void Search_views_and_badges()
    {
        var a = Win(1, "code.exe", default, title: "parser.rs - engine - Visual Studio Code") with { Elevated = true };
        var b = Win(2, "chrome.exe", default, title: "Pull request review - Google Chrome") with { Frozen = true };
        var c = Win(3, "chrome.exe", default, title: "Inbox mail") with { Cloaked = Cloak.Shell };
        var ordered = new[] { a, b, c };
        string? Wf(long h) => h == 1 ? "Engine work" : h == 2 ? "Engine work" : null;

        Assert.Equal([1L], SwitcherFilter.Tiles(ordered, "pars", SwitcherView.All, null, Wf, _ => false).Select(t => t.Window.Hwnd));
        Assert.Equal([2L], SwitcherFilter.Tiles(ordered, "chrome req", SwitcherView.All, null, Wf, _ => false).Select(t => t.Window.Hwnd));
        Assert.Equal([2L, 3], SwitcherFilter.Tiles(ordered, null, SwitcherView.App, "chrome.exe", Wf, _ => false).Select(t => t.Window.Hwnd));
        Assert.Empty(SwitcherFilter.Tiles(ordered, "zzz", SwitcherView.All, null, Wf, _ => false));

        var grouped = SwitcherFilter.Tiles([c, a, b], null, SwitcherView.Workflow, null, Wf, pid => pid == 1);
        Assert.Equal(["Other", "Engine work", "Engine work"], grouped.Select(t => t.Group));
        var badges = SwitcherFilter.Tiles(ordered, null, SwitcherView.All, null, Wf, pid => pid == 1).ToDictionary(t => t.Window.Hwnd, t => t.Badges);
        Assert.Equal(TileBadges.Job | TileBadges.Admin, badges[1]);
        Assert.Equal(TileBadges.Frozen, badges[2]);
        Assert.Equal(TileBadges.OtherDesktop, badges[3]);
        Assert.Same(Right, SwitcherFilter.MonitorFor(2500, 500, Two));
    }

    private static PresetSlot Slot(string app, int nth = 0, string? cls = "Chrome_WidgetWin_1", string? title = null, bool launch = false, bool focus = false) => new()
    {
        App = new PresetApp { AppId = app, Exe = @"C:\Apps\" + app },
        Match = new PresetMatch { Class = cls, Nth = nth, TitlePattern = title },
        Place = new PresetPlace { Monitor = Right.DevicePath, Rect = new DipRect { X = 0, Y = 0, W = 800, H = 600 } },
        Launch = launch ? new PresetLaunch { Exe = @"C:\Apps\" + app } : null,
        Focus = focus,
    };

    [Fact]
    public void Slots_claim_windows_greedily_in_mru_order_once_each_and_respect_nth_and_the_users_title_pattern()
    {
        var c1 = Win(1, "chrome.exe", new RectPx(0, 0, 500, 500), title: "Docs");
        var c2 = Win(2, "chrome.exe", new RectPx(0, 0, 500, 500), title: "Mail");
        var code = Win(3, "code.exe", new RectPx(0, 0, 500, 500), "Code");
        var preset = new Preset
        {
            Name = "Engine",
            Slots = [Slot("chrome.exe", nth: 1), Slot("chrome.exe"), Slot("code.exe", cls: "Code"), Slot("code.exe", cls: "Code", launch: true), Slot("obsidian.exe", launch: true), Slot("chrome.exe", title: "*mail*")],
        };
        var plans = PresetPlanner.Plan(preset, [c1, c2, code], Two, userInitiated: true);
        Assert.Equal(2, plans[0].Window!.Hwnd); // nth = 1 → the second chrome window
        Assert.Equal(1, plans[1].Window!.Hwnd);
        Assert.Equal(3, plans[2].Window!.Hwnd);
        Assert.Equal(SlotOutcome.Launch, plans[3].Outcome); // its window was already claimed
        Assert.Equal(SlotOutcome.Launch, plans[4].Outcome);
        Assert.Equal(SlotOutcome.NotOpen, plans[5].Outcome); // "Mail" was claimed by slot 0

        var byRule = PresetPlanner.Plan(preset, [c1, c2, code], Two, userInitiated: false);
        Assert.DoesNotContain(byRule, p => p.Outcome == SlotOutcome.Launch); // rules only arrange
    }

    [Fact]
    public void Frozen_hung_and_admin_windows_are_skipped_with_a_reason_and_targets_add_the_invisible_border()
    {
        var frozen = Win(1, "train.exe", new RectPx(0, 0, 100, 100), null) with { Frozen = true };
        var hung = Win(2, "hung.exe", new RectPx(0, 0, 100, 100), null) with { Hung = true };
        var admin = Win(3, "admin.exe", new RectPx(0, 0, 100, 100), null) with { Elevated = true };
        var ok = Win(4, "code.exe", new RectPx(0, 0, 100, 100), null);
        var preset = new Preset { Name = "p", Slots = [Slot("train.exe", cls: null), Slot("hung.exe", cls: null), Slot("admin.exe", cls: null), Slot("code.exe", cls: null)] };
        var plans = PresetPlanner.Plan(preset, [frozen, hung, admin, ok], Two, userInitiated: true);
        Assert.Equal([SlotOutcome.SkippedFrozen, SlotOutcome.SkippedHung, SlotOutcome.SkippedAdmin, SlotOutcome.Move], plans.Select(p => p.Outcome));
        Assert.Equal(["frozen job", "not responding", "admin window"], plans.Take(3).Select(p => p.Reason));

        // Visible frame 1200×900 px at the right monitor's work-area origin; the window rect adds 7 px left / right / bottom.
        Assert.Equal(new RectPx(1920 - 7, 0, 1200 + 14, 900 + 7), plans[3].TargetPx);
    }

    [Fact]
    public void Undo_restores_what_still_exists_for_30_minutes()
    {
        var a = Win(1, "code.exe", new RectPx(10, 10, 500, 500), null);
        var b = Win(2, "chrome.exe", new RectPx(20, 20, 500, 500));
        var preset = new Preset { Name = "p", Slots = [Slot("code.exe", cls: null), Slot("chrome.exe")] };
        var t0 = DateTimeOffset.UnixEpoch;
        var snap = PresetPlanner.Snapshot(PresetPlanner.Plan(preset, [a, b], Two, true), t0);
        Assert.Equal(2, snap.Before.Count);
        var back = PresetPlanner.Undo(snap, [1L], t0.AddMinutes(10));
        Assert.Equal((1L, a.WindowPx, ShowState.Normal), Assert.Single(back));
        Assert.Empty(PresetPlanner.Undo(snap, [1L, 2L], t0.AddMinutes(31)));
    }

    [Fact]
    public void Saving_what_is_open_keeps_app_class_nth_and_placement_and_never_a_title()
    {
        var c1 = Win(1, "chrome.exe", new RectPx(1920 + 150, 300, 1200, 900), title: "Secret page title");
        var c2 = Win(2, "chrome.exe", new RectPx(100, 100, 800, 600), title: "Other");
        var preset = PresetBuilder.FromOpen("  Morning ", [c1, c2], new HashSet<long> { 1, 2 }, Two);
        Assert.Equal("Morning", preset.Name);
        Assert.Equal([0, 1], preset.Slots.Select(s => s.Match!.Nth));
        Assert.Equal(Right.DevicePath, preset.Slots[0].Place.Monitor);
        Assert.Equal(800, preset.Slots[0].Place.Rect.W);
        Assert.All(preset.Slots, s => Assert.Null(s.Match!.TitlePattern));
        Assert.All(preset.Slots, s => Assert.Null(s.Launch));
        var json = System.Text.Json.JsonSerializer.Serialize(new PresetsFile { Presets = [preset] }, Tasktor.Contracts.ContractsJsonContext.Default.PresetsFile);
        Assert.DoesNotContain("Secret", json, StringComparison.Ordinal);

        // Round trip: the saved preset puts the same windows back where they were.
        var plans = PresetPlanner.Plan(preset, [c1, c2], Two, userInitiated: false);
        Assert.Equal(PresetPlanner.WindowRectFor(c1.FramePx, c1), plans[0].TargetPx);
        Assert.Equal(PresetPlanner.WindowRectFor(c2.FramePx, c2), plans[1].TargetPx);
    }
}
