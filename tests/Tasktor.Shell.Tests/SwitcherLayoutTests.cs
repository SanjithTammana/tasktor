using Tasktor.Core.Windows;

namespace Tasktor.Shell.Tests;

/// <summary>F2 (U3 D-W4 / D-W8): tile grid, window size and placement on the cursor's monitor, at any DPI.</summary>
public sealed class SwitcherLayoutTests
{
    private static readonly RectPx Work = new(0, 0, 1920, 1040);

    [Fact]
    public void Tiles_fill_rows_of_six_at_most_four_rows_and_the_window_is_centred_on_the_work_area()
    {
        var one = SwitcherLayout.For(1, Work, scale: 1.0);
        Assert.Single(one.Tiles);
        Assert.Equal(Work.X + ((Work.W - one.Window.W) / 2), one.Window.X);
        Assert.Equal(Work.Y + ((Work.H - one.Window.H) / 2), one.Window.Y);

        var many = SwitcherLayout.For(40, Work, scale: 1.0);
        Assert.Equal(SwitcherFilter.MaxTiles, many.Tiles.Count); // at most 24 drawn; the rest scroll
        Assert.Equal(4, many.Tiles.Select(t => t.Y).Distinct().Count());
        Assert.Equal(6, many.Tiles.Count(t => t.Y == many.Tiles[0].Y));
        Assert.All(many.Tiles, t => Assert.True(t.Right <= many.Window.W && t.Bottom <= many.Window.H));
        Assert.Equal(many.Tiles.Count, many.Tiles.Distinct().Count()); // no overlap at the same spot
    }

    [Fact]
    public void Everything_scales_with_dpi_and_never_exceeds_the_work_area()
    {
        var normal = SwitcherLayout.For(3, Work, scale: 1.0);
        var large = SwitcherLayout.For(3, Work, scale: 1.5); // three tiles fit 1920 px at 150 %
        Assert.Equal((int)Math.Round(normal.Tiles[0].W * 1.5), large.Tiles[0].W);

        var small = new RectPx(1920, 0, 1024, 600); // a second, small monitor
        var tight = SwitcherLayout.For(24, small, scale: 2.0);
        Assert.True(tight.Window.W <= small.W && tight.Window.H <= small.H);
        Assert.True(tight.Window.X >= small.X && tight.Window.Y >= small.Y);
    }

    [Fact]
    public void A_tile_has_a_thumbnail_area_above_its_caption_and_the_header_holds_the_query()
    {
        var l = SwitcherLayout.For(3, Work, scale: 1.0);
        var t = l.Tiles[0];
        var thumb = SwitcherLayout.Thumbnail(t, scale: 1.0);
        var caption = SwitcherLayout.Caption(t, scale: 1.0);
        Assert.True(thumb.Y >= t.Y && thumb.Bottom <= caption.Y && caption.Bottom <= t.Bottom);
        Assert.True(l.Header.Bottom <= t.Y);
    }

    [Fact]
    public void A_thumbnail_keeps_the_source_aspect_inside_its_area()
    {
        var area = new RectPx(10, 10, 200, 100);
        var wide = SwitcherLayout.Fit(area, 1600, 400); // 4:1 → full width, centred vertically
        Assert.Equal((10, 200, 50), (wide.X, wide.W, wide.H));
        Assert.Equal(35, wide.Y);
        var tall = SwitcherLayout.Fit(area, 400, 800); // 1:2 → full height, centred
        Assert.Equal((100, 50), (tall.H, tall.W));
        Assert.Equal(area, SwitcherLayout.Fit(area, 0, 0)); // unknown size: the whole area
    }

    [Fact]
    public void More_than_24_windows_page_so_the_selection_is_always_drawn()
    {
        Assert.Equal((0, 24), SwitcherLayout.Page(selected: 5, count: 30));
        Assert.Equal((24, 6), SwitcherLayout.Page(selected: 24, count: 30)); // the 25th window: second page, six tiles
        Assert.Equal((24, 6), SwitcherLayout.Page(selected: 29, count: 30));
        Assert.Equal((0, 3), SwitcherLayout.Page(selected: -1, count: 3)); // no selection: the first page
        Assert.Equal((0, 0), SwitcherLayout.Page(selected: -1, count: 0));
    }
}
