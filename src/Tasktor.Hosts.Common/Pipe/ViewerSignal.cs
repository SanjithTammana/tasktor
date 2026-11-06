namespace Tasktor.Hosts.Common.Pipe;

/// <summary>
/// Named objects in the user's logon session (<c>Local\</c>) that keep one flyout process and let the agent's tray and
/// edge, or a second launch, ask it to show a surface without starting another WinUI process (U1 D-H10 budget).
/// </summary>
public static class ViewerSignal
{
    /// <summary>Held by the running flyout process.</summary>
    public const string Instance = @"Local\Tasktor.Flyout.Instance";

    /// <summary>Set to open the shelf (hover or click on the edge or the tray icon).</summary>
    public const string ShowShelf = @"Local\Tasktor.Flyout.ShowShelf";

    /// <summary>Set to open the deep window ("Everything Tasktor knows", the tray menu, <c>tasktor open</c>).</summary>
    public const string OpenDeep = @"Local\Tasktor.Flyout.OpenDeep";

    /// <summary>The surface a launch asks for: <c>--open</c> is the deep window, anything else the shelf.</summary>
    public static string ForArgs(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Contains("--open", StringComparer.Ordinal) ? OpenDeep : ShowShelf;
    }
}
