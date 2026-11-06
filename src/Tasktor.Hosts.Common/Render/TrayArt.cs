using Tasktor.Hosts.Common.Theme;

namespace Tasktor.Hosts.Common.Render;

/// <summary>The tray icon's states (U4 D-G4, amended D-G15).</summary>
public enum TrayIconState
{
    Normal,
    Caution,
    Critical,
    Paused,
    Recording,
}

/// <summary>
/// Paints the tray icon (the edge in miniature: three waves) and the edge strip into pixel buffers, so the Win32 side
/// only hands bytes to <c>CreateIconIndirect</c> / <c>UpdateLayeredWindow</c>. Pixels are premultiplied BGRA packed as
/// <c>0xAARRGGBB</c>, rows top first. Geometry follows the signed boards' 16-unit drawing. Severity is a small shape on
/// the icon; red is used only while capture records (D-G14).
/// </summary>
public static class TrayArt
{
    /// <summary>Alpha 1 of 255 behind the edge's ticks: invisible, but it makes the whole strip receive the mouse.</summary>
    public const uint HitTestPixel = 0x01000000;

    public static uint[] Icon(TrayIconState state, bool open, int size, Palette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 8);
        var c = new Canvas(size);
        var lines = open ? palette.Accent : palette.Ink;
        switch (state)
        {
            case TrayIconState.Paused:
                foreach (var y in (ReadOnlySpan<double>)[5, 9, 13])
                {
                    c.DashedLine(y, 1.5, 14.5, 1.3, 2, lines);
                }

                break;
            case TrayIconState.Recording:
                c.Ring(8, 8, 7.1, 1.4, palette.Recording);
                c.Wave(6.5, 4, 12, 4, 0.33, 1.2, lines);
                c.Wave(9.5, 4, 12, 4, 0.33, 1.2, lines);
                break;
            default:
                foreach (var y in (ReadOnlySpan<double>)[5, 9, 13])
                {
                    c.Wave(y, 1.5, 14.5, 6.5, 0.58, 1.3, lines);
                }

                break;
        }

        if (state == TrayIconState.Caution)
        {
            (double X, double Y)[] tri = [(12.75, 9.75), (15.75, 15.5), (9.75, 15.5)];
            c.Clear((x, y) => Triangle(tri, x, y, grow: 0.9));
            c.Fill((x, y) => Triangle(tri, x, y, grow: 0), palette.Caution);
        }
        else if (state == TrayIconState.Critical)
        {
            c.Clear((x, y) => 3.5 - Math.Sqrt(((x - 12.75) * (x - 12.75)) + ((y - 12.75) * (y - 12.75))));
            c.Fill((x, y) => 2.7 - Math.Sqrt(((x - 12.75) * (x - 12.75)) + ((y - 12.75) * (y - 12.75))), palette.Critical);
        }

        return c.Pixels;
    }

    /// <summary>
    /// The edge strip: <paramref name="ticks"/> from <see cref="EdgeGrain"/> in effective pixels, drawn at
    /// <paramref name="scale"/> into a <paramref name="width"/> × <paramref name="height"/> buffer; plain ticks in Ink3,
    /// bright ones in Ink, the rest <see cref="HitTestPixel"/>.
    /// </summary>
    public static uint[] Edge(IReadOnlyList<GrainTick> ticks, double scale, int width, int height, Palette palette)
    {
        ArgumentNullException.ThrowIfNull(ticks);
        ArgumentNullException.ThrowIfNull(palette);
        var px = new uint[Math.Max(0, width) * Math.Max(0, height)];
        Array.Fill(px, HitTestPixel);
        var w = Math.Max(1, (int)Math.Round(scale));
        foreach (var t in ticks)
        {
            var x0 = (int)Math.Round((t.X + 1) * scale);
            var h = Math.Clamp((int)Math.Round(t.Height * height), 1, height);
            var color = Opaque(t.Hot ? palette.Ink : palette.Ink3);
            for (var x = x0; x < Math.Min(width, x0 + w); x++)
            {
                for (var y = height - h; y < height; y++)
                {
                    px[(y * width) + x] = color;
                }
            }
        }

        return px;
    }

    public static uint Opaque(Rgb c) => 0xFF000000u | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

    // Signed distance inside a triangle (positive inside), approximated by the smallest edge distance.
    private static double Triangle((double X, double Y)[] p, double x, double y, double grow)
    {
        var min = double.MaxValue;
        for (var i = 0; i < 3; i++)
        {
            var (ax, ay) = p[i];
            var (bx, by) = p[(i + 1) % 3];
            var (cx, cy) = p[(i + 2) % 3];
            var nx = by - ay;
            var ny = ax - bx;
            var len = Math.Sqrt((nx * nx) + (ny * ny));
            var side = ((cx - ax) * nx) + ((cy - ay) * ny) > 0 ? 1 : -1;
            min = Math.Min(min, side * (((x - ax) * nx) + ((y - ay) * ny)) / len);
        }

        return min + grow;
    }

    /// <summary>A tiny coverage rasterizer over the 16-unit drawing, 4 × 4 supersampled.</summary>
    private sealed class Canvas
    {
        private const int Ss = 4;
        private readonly int _size;
        private readonly double _unit;
        private readonly double[] _a;
        private readonly double[] _r;
        private readonly double[] _g;
        private readonly double[] _b;

        public Canvas(int size)
        {
            _size = size;
            _unit = 16.0 / size;
            _a = new double[size * size];
            _r = new double[size * size];
            _g = new double[size * size];
            _b = new double[size * size];
        }

        public uint[] Pixels
        {
            get
            {
                var px = new uint[_size * _size];
                for (var i = 0; i < px.Length; i++)
                {
                    static uint B(double v) => (uint)Math.Clamp(Math.Round(v * 255), 0, 255);
                    px[i] = (B(_a[i]) << 24) | (B(_r[i]) << 16) | (B(_g[i]) << 8) | B(_b[i]);
                }

                return px;
            }
        }

        // Strokes never go below 1.5 device pixels, so the 16 px icon still has solid lines.
        private double Stroke(double width) => Math.Max(width, 1.5 * _unit) / 2;

        public void Wave(double cy, double x0, double x1, double period, double amp, double width, Rgb color) =>
            Fill((x, y) => Stroke(width) - WaveDistance(x, y, cy, x0, x1, period, amp), color);

        public void DashedLine(double cy, double x0, double x1, double width, double dash, Rgb color)
        {
            var snapped = (Math.Floor(cy / _unit) + 0.5) * _unit; // a straight line on a pixel row reads solid, not as two half rows
            Fill((x, y) => x < x0 || x > x1 || ((int)Math.Floor((x - x0) / dash) % 2) == 1 ? -1 : Stroke(width) - Math.Abs(y - snapped), color);
        }

        public void Ring(double cx, double cy, double radius, double width, Rgb color) =>
            Fill((x, y) => Stroke(width) - Math.Abs(Math.Sqrt(((x - cx) * (x - cx)) + ((y - cy) * (y - cy))) - radius), color);

        /// <summary>Source-over a shape whose signed distance (units, positive inside) is <paramref name="inside"/>.</summary>
        public void Fill(Func<double, double, double> inside, Rgb color)
        {
            var (cr, cg, cb) = (color.R / 255.0, color.G / 255.0, color.B / 255.0);
            Each(inside, (i, cov) =>
            {
                var keep = 1 - cov;
                _a[i] = cov + (_a[i] * keep);
                _r[i] = (cr * cov) + (_r[i] * keep);
                _g[i] = (cg * cov) + (_g[i] * keep);
                _b[i] = (cb * cov) + (_b[i] * keep);
            });
        }

        /// <summary>Erase under a shape (the gap that lets a severity mark read against the waves).</summary>
        public void Clear(Func<double, double, double> inside) =>
            Each(inside, (i, cov) =>
            {
                var keep = 1 - cov;
                _a[i] *= keep;
                _r[i] *= keep;
                _g[i] *= keep;
                _b[i] *= keep;
            });

        private void Each(Func<double, double, double> inside, Action<int, double> apply)
        {
            for (var py = 0; py < _size; py++)
            {
                for (var px = 0; px < _size; px++)
                {
                    var hits = 0;
                    for (var sy = 0; sy < Ss; sy++)
                    {
                        for (var sx = 0; sx < Ss; sx++)
                        {
                            var x = (px + ((sx + 0.5) / Ss)) * _unit;
                            var y = (py + ((sy + 0.5) / Ss)) * _unit;
                            if (inside(x, y) >= 0)
                            {
                                hits++;
                            }
                        }
                    }

                    if (hits > 0)
                    {
                        apply((py * _size) + px, hits / (double)(Ss * Ss));
                    }
                }
            }
        }

        private static double WaveDistance(double x, double y, double cy, double x0, double x1, double period, double amp)
        {
            var cx = Math.Clamp(x, x0, x1);
            var k = 2 * Math.PI / period;
            var fy = cy - (amp * Math.Sin(k * (cx - x0)));
            if (cx != x)
            {
                return Math.Sqrt(((x - cx) * (x - cx)) + ((y - fy) * (y - fy))); // round cap past the ends
            }

            var slope = -amp * k * Math.Cos(k * (x - x0));
            return Math.Abs(y - fy) / Math.Sqrt(1 + (slope * slope));
        }
    }
}
