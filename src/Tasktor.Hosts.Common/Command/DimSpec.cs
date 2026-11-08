namespace Tasktor.Hosts.Common.Command;

/// <summary>
/// G2 (D-G12): the dimmed desktop under the command line: the whole monitor the command line opens on, black at
/// <see cref="Alpha"/>. None in high-contrast mode (a dim layer would fight the user's contrast colours) or when the
/// monitor has no area.
/// </summary>
public static class DimSpec
{
    /// <summary>About 35% black: the desktop stays readable behind the input.</summary>
    public const byte Alpha = 90;

    public static (int X, int Y, int W, int H, byte Alpha)? For(int x, int y, int width, int height, bool highContrast) =>
        highContrast || width <= 0 || height <= 0 ? null : (x, y, width, height, Alpha);
}
