using System.Globalization;

namespace Tasktor.Hosts.Common.Theme;

/// <summary>An sRGB colour parsed from "#RRGGBB".</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb Parse(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        var h = hex.TrimStart('#');
        if (h.Length == 8)
        {
            h = h[2..]; // XAML #AARRGGBB; tokens are opaque
        }

        if (h.Length != 6)
        {
            throw new FormatException("expected #RRGGBB: " + hex);
        }

        var v = int.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"#{R:X2}{G:X2}{B:X2}");

    /// <summary>WCAG 2.x relative luminance.</summary>
    public double Luminance => 0.2126 * Channel(R) + 0.7152 * Channel(G) + 0.0722 * Channel(B);

    private static double Channel(byte c)
    {
        var s = c / 255.0;
        return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }
}

/// <summary>
/// One theme's colours (U4 amended D-G14): neutral surfaces, three inks for the deep window, dialogs and switcher, three
/// raised-contrast glance inks for the shelf, command line and tray, the approved accent, and the three marks.
/// <see cref="Accent"/> is the approved design accent; viewers pin it instead of following the Windows accent.
/// </summary>
public sealed record Palette(
    string Name,
    Rgb Mica,
    Rgb Shelf,
    Rgb Surface,
    Rgb SurfaceRaised,
    Rgb Rule,
    Rgb Ink,
    Rgb Ink2,
    Rgb Ink3,
    Rgb GlanceInk,
    Rgb GlanceInk2,
    Rgb GlanceInk3,
    Rgb Accent,
    Rgb OnAccent,
    Rgb Caution,
    Rgb Critical,
    Rgb Success,
    Rgb Recording)
{
    /// <summary>The token names and values in XAML key order (Tasktor{Name}Color / Tasktor{Name}Brush).</summary>
    public IReadOnlyList<(string Name, Rgb Color)> Entries =>
    [
        (nameof(Mica), Mica), (nameof(Shelf), Shelf), (nameof(Surface), Surface), (nameof(SurfaceRaised), SurfaceRaised),
        (nameof(Rule), Rule), (nameof(Ink), Ink), (nameof(Ink2), Ink2), (nameof(Ink3), Ink3),
        (nameof(GlanceInk), GlanceInk), (nameof(GlanceInk2), GlanceInk2), (nameof(GlanceInk3), GlanceInk3), (nameof(Accent), Accent),
        (nameof(OnAccent), OnAccent),
        (nameof(Caution), Caution), (nameof(Critical), Critical), (nameof(Success), Success), (nameof(Recording), Recording),
    ];

    /// <summary>Tokens drawn as text or as a mark that carries meaning; each must reach 4.5:1 on every background.</summary>
    public IReadOnlyList<(string Name, Rgb Color)> Foregrounds =>
    [
        (nameof(Ink), Ink), (nameof(Ink2), Ink2), (nameof(Ink3), Ink3),
        (nameof(GlanceInk), GlanceInk), (nameof(GlanceInk2), GlanceInk2), (nameof(GlanceInk3), GlanceInk3), (nameof(Accent), Accent),
        (nameof(Caution), Caution), (nameof(Critical), Critical), (nameof(Success), Success), (nameof(Recording), Recording),
    ];

    /// <summary>Surfaces text is drawn on.</summary>
    public IReadOnlyList<(string Name, Rgb Color)> Backgrounds =>
        [(nameof(Mica), Mica), (nameof(Shelf), Shelf), (nameof(Surface), Surface), (nameof(SurfaceRaised), SurfaceRaised)];
}

/// <summary>One step of the type ramp; sizes in effective pixels, weights as OpenType values.</summary>
public sealed record TypeStep(string Name, string Family, double Size, double LineHeight, int Weight);

/// <summary>
/// U4 amended D-G14: the shared tokens behind every Tasktor surface. The WinUI viewers load the same values from
/// <c>Themes/Tasktor.xaml</c>; a test keeps the two equal. Surfaces and inks come from the approved deep-window, dialog and switcher boards
/// (Windows Settings values); the glance inks come from the later glance boards, which raised contrast.
/// </summary>
public static class ThemeTokens
{
    public const double MinTextContrast = 4.5;

    public static Palette Dark { get; } = new(
        "Dark",
        Mica: Rgb.Parse("#202020"),
        Shelf: Rgb.Parse("#1C1C1C"),
        Surface: Rgb.Parse("#2B2B2B"),
        SurfaceRaised: Rgb.Parse("#323232"),
        Rule: Rgb.Parse("#3D3D3D"),
        Ink: Rgb.Parse("#FFFFFF"),
        Ink2: Rgb.Parse("#CFCFCF"),
        Ink3: Rgb.Parse("#9D9D9D"),
        GlanceInk: Rgb.Parse("#FFFFFF"),
        GlanceInk2: Rgb.Parse("#D6D6D6"),
        GlanceInk3: Rgb.Parse("#ABABAB"),
        Accent: Rgb.Parse("#60CDFF"),
        OnAccent: Rgb.Parse("#000000"),
        Caution: Rgb.Parse("#FCE100"),
        Critical: Rgb.Parse("#FF99A4"),
        Success: Rgb.Parse("#6CCB5F"),
        Recording: Rgb.Parse("#FF6A6A"));

    public static Palette Light { get; } = new(
        "Light",
        Mica: Rgb.Parse("#F3F3F3"),
        Shelf: Rgb.Parse("#F3F3F3"),
        Surface: Rgb.Parse("#FBFBFB"),
        SurfaceRaised: Rgb.Parse("#F6F6F6"),
        Rule: Rgb.Parse("#D6D6D6"),
        Ink: Rgb.Parse("#1A1A1A"),
        Ink2: Rgb.Parse("#5D5D5D"),
        Ink3: Rgb.Parse("#616161"),
        GlanceInk: Rgb.Parse("#111111"),
        GlanceInk2: Rgb.Parse("#3D3D3D"),
        GlanceInk3: Rgb.Parse("#5A5A5A"),
        Accent: Rgb.Parse("#005FB8"),
        OnAccent: Rgb.Parse("#FFFFFF"),
        Caution: Rgb.Parse("#9D5D00"),
        Critical: Rgb.Parse("#C42B1C"),
        Success: Rgb.Parse("#0F7B0F"),
        Recording: Rgb.Parse("#C42B1C"));

    public static IReadOnlyList<Palette> Palettes { get; } = [Dark, Light];

    public const string Display = "Segoe UI Variable Display";
    public const string Text = "Segoe UI Variable Text";

    /// <summary>The ramp: the shelf sentence, the command input, page titles, section heads, body, captions.</summary>
    public static IReadOnlyList<TypeStep> Ramp { get; } =
    [
        new("Command", Display, 34, 44, 350),
        new("Sentence", Display, 26, 38, 350),
        new("Title", Display, 28, 36, 600),
        new("Subtitle", Text, 20, 28, 600),
        new("BodyStrong", Text, 14, 20, 600),
        new("Body", Text, 14, 20, 400),
        new("Caption", Text, 12, 16, 400),
    ];

    /// <summary>The spacing scale in effective pixels (4 px grid); the shelf uses 24 vertical and 48 horizontal.</summary>
    public static IReadOnlyList<int> Spacing { get; } = [4, 8, 12, 16, 24, 32, 48];

    /// <summary>WCAG 2.x contrast ratio, 1 to 21.</summary>
    public static double Contrast(Rgb a, Rgb b)
    {
        var (l1, l2) = (a.Luminance, b.Luminance);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }
}
