using Tasktor.Hosts.Common.Render;
using Tasktor.Hosts.Common.Theme;

namespace Tasktor.Hosts.Common.Tests;

/// <summary>U4 D-G4 / amended D-G10, D-G15: the tray icon states and the edge strip as pixels.</summary>
public sealed class TrayArtTests
{
    private static readonly Palette Dark = ThemeTokens.Dark;

    private static int Count(uint[] px, Rgb color, int minAlpha = 200) =>
        px.Count(p => (p >> 24) >= minAlpha && (p & 0xFFFFFF) == (TrayArt.Opaque(color) & 0xFFFFFF));

    private static bool Premultiplied(uint p)
    {
        var a = p >> 24;
        return ((p >> 16) & 0xFF) <= a && ((p >> 8) & 0xFF) <= a && (p & 0xFF) <= a;
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(32)]
    public void Every_state_paints_a_valid_premultiplied_icon(int size)
    {
        foreach (var state in Enum.GetValues<TrayIconState>())
        {
            foreach (var open in new[] { false, true })
            {
                var px = TrayArt.Icon(state, open, size, Dark);
                Assert.Equal(size * size, px.Length);
                Assert.All(px, p => Assert.True(Premultiplied(p)));
                Assert.Contains(px, p => (p >> 24) >= 230); // solid strokes even at 16 px
                Assert.Contains(px, p => p == 0); // transparent background
            }
        }
    }

    [Fact]
    public void Red_appears_only_while_recording()
    {
        Assert.True(Count(TrayArt.Icon(TrayIconState.Recording, false, 32, Dark), Dark.Recording) > 20);
        foreach (var state in new[] { TrayIconState.Normal, TrayIconState.Caution, TrayIconState.Critical, TrayIconState.Paused })
        {
            Assert.Equal(0, Count(TrayArt.Icon(state, false, 32, Dark), Dark.Recording));
        }
    }

    [Fact]
    public void Severity_is_a_small_mark_in_the_corner_with_a_gap()
    {
        var caution = TrayArt.Icon(TrayIconState.Caution, false, 32, Dark);
        var critical = TrayArt.Icon(TrayIconState.Critical, false, 32, Dark);
        Assert.True(Count(caution, Dark.Caution) > 10);
        Assert.True(Count(critical, Dark.Critical) > 10);
        Assert.Equal(0, Count(TrayArt.Icon(TrayIconState.Normal, false, 32, Dark), Dark.Caution));

        // The mark sits in the bottom-right quarter only.
        for (var i = 0; i < caution.Length; i++)
        {
            if ((caution[i] & 0xFFFFFF) == (TrayArt.Opaque(Dark.Caution) & 0xFFFFFF) && caution[i] >> 24 > 200)
            {
                Assert.True(i % 32 >= 16 && i / 32 >= 16, "caution pixel outside the corner at " + i);
            }
        }

        // The gap: fewer ink pixels than the plain icon.
        var normal = TrayArt.Icon(TrayIconState.Normal, false, 32, Dark);
        Assert.True(Count(caution, Dark.Ink) < Count(normal, Dark.Ink));
    }

    [Fact]
    public void Open_draws_the_waves_in_the_accent_and_paused_differs()
    {
        var open = TrayArt.Icon(TrayIconState.Normal, true, 32, Dark);
        Assert.True(Count(open, Dark.Accent) > 20);
        Assert.Equal(0, Count(open, Dark.Ink));
        Assert.NotEqual(TrayArt.Icon(TrayIconState.Normal, false, 32, Dark), TrayArt.Icon(TrayIconState.Paused, false, 32, Dark));
    }

    [Fact]
    public void Edge_is_hit_testable_everywhere_and_draws_ticks_bottom_up()
    {
        GrainTick[] ticks = [new(0, 0.6, false), new(4, 1.0, true)];
        var px = TrayArt.Edge(ticks, 1.5, 12, 9, Dark); // 6 DIP at 150 %
        Assert.Equal(12 * 9, px.Length);
        Assert.All(px, p => Assert.True(p >> 24 >= 1));

        uint At(int x, int y) => px[(y * 12) + x];
        var plain = TrayArt.Opaque(Dark.Ink3);
        var bright = TrayArt.Opaque(Dark.Ink);
        Assert.Equal(plain, At(2, 8)); // first tick at x = round(1 × 1.5) = 2, two px wide, round(0.6 × 9) = 5 of 9 rows
        Assert.Equal(plain, At(3, 4));
        Assert.Equal(TrayArt.HitTestPixel, At(2, 3));
        Assert.Equal(bright, At(8, 0)); // second tick at round(5 × 1.5) = 8, full height
        Assert.Equal(TrayArt.HitTestPixel, At(0, 8));
    }

    [Fact]
    public void Edge_ticks_past_the_strip_are_dropped()
    {
        var px = TrayArt.Edge([new GrainTick(100, 1, false)], 1, 10, 6, Dark);
        Assert.All(px, p => Assert.Equal(TrayArt.HitTestPixel, p));
    }
}
