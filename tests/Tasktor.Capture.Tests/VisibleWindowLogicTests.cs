using Tasktor.Capture.Visibility;
using Tasktor.Contracts.Presets;
using Tasktor.Core.Windows;

namespace Tasktor.Capture.Tests;

public sealed class VisibleWindowLogicTests
{
    private static readonly DateTimeOffset At = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static WindowInfo Window(long id, RectPx bounds) => new()
    {
        Hwnd = id,
        Pid = (uint)id,
        AppId = $"app{id}.exe",
        FramePx = bounds,
        Show = ShowState.Normal,
    };

    [Fact]
    public void Z_order_subtracts_the_union_of_all_higher_windows_without_double_counting()
    {
        var screen = new RectPx(0, 0, 100, 100);
        var windows = new[] { Window(1, new RectPx(0, 0, 50, 100)), Window(2, new RectPx(25, 0, 50, 100)), Window(3, screen) };
        var result = VisibleWindowLogic.Compute(At, [screen], windows, foreground: 1);
        Assert.Equal(10_000, result.ScreenPx);
        Assert.Equal(new long[] { 5_000, 2_500, 2_500 }, result.Windows.Select(w => w.VisiblePx));
        Assert.Equal(new[] { true, false, false }, result.Windows.Select(w => w.IsForeground));
    }

    [Fact]
    public void Clips_to_all_monitors_with_negative_virtual_coordinates()
    {
        var monitors = new[] { new RectPx(-100, 0, 100, 100), new RectPx(0, 0, 200, 100) };
        var windows = new[] { Window(1, new RectPx(-50, 0, 100, 100)), Window(2, new RectPx(-100, 0, 300, 100)) };
        var result = VisibleWindowLogic.Compute(At, monitors, windows, foreground: 2);
        Assert.Equal(30_000, result.ScreenPx);
        Assert.Equal(new long[] { 10_000, 20_000 }, result.Windows.Select(w => w.VisiblePx));
        Assert.True(result.Windows[1].IsForeground);
    }

    [Fact]
    public void Mirrored_or_overlapping_displays_do_not_double_count_pixels()
    {
        var monitors = new[] { new RectPx(0, 0, 100, 100), new RectPx(50, 0, 100, 100) };
        var result = VisibleWindowLogic.Compute(At, monitors, [Window(1, new RectPx(0, 0, 150, 100))], foreground: 1);
        Assert.Equal(15_000, result.ScreenPx);
        Assert.Equal(15_000, Assert.Single(result.Windows).VisiblePx);
    }

    [Fact]
    public void Filters_hidden_minimized_cloaked_tool_invalid_and_unknown_states_before_occlusion()
    {
        var full = new RectPx(0, 0, 100, 100);
        var windows = new[]
        {
            Window(1, full) with { Visible = false },
            Window(2, full) with { Show = ShowState.Minimized },
            Window(3, full) with { Cloaked = Cloak.Shell },
            Window(4, full) with { ToolWindow = true },
            Window(5, new RectPx(0, 0, 0, 100)),
            Window(6, full) with { Show = (ShowState)255 },
            Window(7, full) with { OnCurrentDesktop = false },
            Window(8, full),
        };
        var result = VisibleWindowLogic.Compute(At, [full], windows, foreground: 8);
        var visible = Assert.Single(result.Windows);
        Assert.Equal(8, visible.Window);
        Assert.Equal(10_000, visible.VisiblePx);
    }

    [Fact]
    public void Fully_occluded_or_offscreen_windows_are_not_reported()
    {
        var screen = new RectPx(0, 0, 100, 100);
        var result = VisibleWindowLogic.Compute(At, [screen], [Window(1, screen), Window(2, screen), Window(3, new RectPx(200, 0, 10, 10))], foreground: 1);
        Assert.Equal(1, Assert.Single(result.Windows).Window);
    }

    [Fact]
    public void Evaluates_only_the_first_thirty_eligible_windows()
    {
        var screen = new RectPx(0, 0, 31, 1);
        var windows = Enumerable.Range(0, 31).Select(i => Window(i + 1, new RectPx(i, 0, 1, 1))).ToArray();
        var result = VisibleWindowLogic.Compute(At, [screen], windows, foreground: 1);
        Assert.Equal(30, result.Windows.Count);
        Assert.DoesNotContain(result.Windows, w => w.Window == 31);
    }

    [Fact]
    public void Unknown_app_identity_is_not_invented_but_its_window_still_occludes()
    {
        var screen = new RectPx(0, 0, 100, 100);
        var windows = new[] { Window(1, new RectPx(0, 0, 50, 100)) with { AppId = "" }, Window(2, screen) };
        var result = VisibleWindowLogic.Compute(At, [screen], windows, foreground: 2);
        var visible = Assert.Single(result.Windows);
        Assert.Equal(2, visible.Window);
        Assert.Equal(5_000, visible.VisiblePx);
    }

    [Fact]
    public void A_window_crossing_the_gap_between_monitors_does_not_count_the_gap()
    {
        var screens = new[] { new RectPx(-200, 0, 100, 100), new RectPx(100, 0, 100, 100) };
        var result = VisibleWindowLogic.Compute(At, screens, [Window(1, new RectPx(-150, 0, 300, 100))], foreground: 1);
        Assert.Equal(20_000, result.ScreenPx);
        Assert.Equal(10_000, Assert.Single(result.Windows).VisiblePx);
    }
    [Fact]
    public void Empty_or_invalid_monitors_produce_no_visible_pixels()
    {
        var result = VisibleWindowLogic.Compute(At, [new RectPx(0, 0, -1, 10)], [Window(1, new RectPx(0, 0, 10, 10))], foreground: 1);
        Assert.Equal(0, result.ScreenPx);
        Assert.Empty(result.Windows);
    }
}

