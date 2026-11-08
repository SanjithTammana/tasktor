using Tasktor.Core.Windows;

namespace Tasktor.Shell;

/// <summary>The switcher window's geometry for one opening: its rect on the work area, the header and the tile cells.</summary>
internal sealed record SwitcherLayout(RectPx Window, RectPx Header, IReadOnlyList<RectPx> Tiles)
{
    public const int Columns = 6;
    public const int TileW = 232;
    public const int TileH = 176;
    public const int Gap = 12;
    public const int Pad = 20;
    public const int HeaderH = 40;
    public const int CaptionH = 36;

    /// <summary>
    /// Up to <see cref="SwitcherFilter.MaxTiles"/> tiles in rows of six, centred on <paramref name="work"/> (the monitor
    /// with the cursor, D-W8), scaled for its DPI and shrunk to fit a small screen. Tile rects are window-relative.
    /// </summary>
    public static SwitcherLayout For(int count, RectPx work, double scale)
    {
        var n = Math.Clamp(count, 1, SwitcherFilter.MaxTiles);
        var cols = Math.Min(Columns, n);
        var rows = (n + Columns - 1) / Columns;
        int S(int v) => (int)Math.Round(v * scale);
        int tileW = S(TileW), tileH = S(TileH), gap = S(Gap), pad = S(Pad), headerH = S(HeaderH);
        var width = (pad * 2) + (cols * tileW) + ((cols - 1) * gap);
        var height = (pad * 2) + headerH + (rows * tileH) + ((rows - 1) * gap);

        // A small screen: shrink the tiles (never the count) until the window fits the work area.
        var fit = Math.Min(1.0, Math.Min((double)work.W / width, (double)work.H / height));
        if (fit < 1.0)
        {
            (tileW, tileH, gap, pad) = ((int)(tileW * fit), (int)(tileH * fit), (int)(gap * fit), (int)(pad * fit));
            width = (pad * 2) + (cols * tileW) + ((cols - 1) * gap);
            height = (pad * 2) + headerH + (rows * tileH) + ((rows - 1) * gap);
        }

        width = Math.Min(width, work.W);
        height = Math.Min(height, work.H);
        var window = new RectPx(work.X + ((work.W - width) / 2), work.Y + ((work.H - height) / 2), width, height);
        var tiles = new List<RectPx>(n);
        for (var i = 0; i < n; i++)
        {
            var (r, c) = (i / Columns, i % Columns);
            tiles.Add(new RectPx(pad + (c * (tileW + gap)), pad + headerH + (r * (tileH + gap)), tileW, tileH));
        }

        return new SwitcherLayout(window, new RectPx(pad, pad, width - (pad * 2), headerH), tiles);
    }

    /// <summary>
    /// The page of at most <see cref="SwitcherFilter.MaxTiles"/> tiles that holds the selection (D-W4: "more scrolls"):
    /// its first tile's index and how many tiles it has.
    /// </summary>
    public static (int First, int Count) Page(int selected, int count)
    {
        if (count <= 0)
        {
            return (0, 0);
        }

        var first = Math.Max(0, selected) / SwitcherFilter.MaxTiles * SwitcherFilter.MaxTiles;
        return (first, Math.Min(SwitcherFilter.MaxTiles, count - first));
    }

    /// <summary>The thumbnail area of a tile: everything above the caption, inset a little.</summary>
    public static RectPx Thumbnail(RectPx tile, double scale)
    {
        var inset = (int)Math.Round(6 * scale);
        var caption = (int)Math.Round(CaptionH * scale);
        return new RectPx(tile.X + inset, tile.Y + inset, tile.W - (inset * 2), Math.Max(0, tile.H - caption - (inset * 2)));
    }

    /// <summary>The caption line under the thumbnail (title · app · badges).</summary>
    public static RectPx Caption(RectPx tile, double scale)
    {
        var inset = (int)Math.Round(6 * scale);
        var caption = (int)Math.Round(CaptionH * scale);
        return new RectPx(tile.X + inset, tile.Bottom - caption, tile.W - (inset * 2), caption - inset);
    }

    /// <summary>The source's aspect inside <paramref name="area"/>, centred; the whole area when the size is unknown.</summary>
    public static RectPx Fit(RectPx area, int sourceW, int sourceH)
    {
        if (sourceW <= 0 || sourceH <= 0 || area.W <= 0 || area.H <= 0)
        {
            return area;
        }

        var s = Math.Min((double)area.W / sourceW, (double)area.H / sourceH);
        var (w, h) = ((int)Math.Round(sourceW * s), (int)Math.Round(sourceH * s));
        return new RectPx(area.X + ((area.W - w) / 2), area.Y + ((area.H - h) / 2), w, h);
    }
}
