namespace Tasktor.Shell;

/// <summary>What the hook asks the switcher window to do.</summary>
internal enum AltTabCommand
{
    None,
    Open,
    OpenReverse,
    Next,
    Previous,
    Cancel,
}

/// <summary>Whether the hook swallows the key, and the command it posts (if any).</summary>
internal readonly record struct AltTabDecision(bool Swallow, AltTabCommand Command)
{
    public static readonly AltTabDecision Pass = new(false, AltTabCommand.None);
}

/// <summary>
/// D-W3 opt-in "Replace Alt+Tab", as pure logic: only Tab (with Alt) and Esc (while the switcher is open) are ever
/// swallowed. Alt is never swallowed, so it cannot stick; letters reach the open switcher as typing; with an
/// exclusive-fullscreen app in front the native Alt+Tab runs (D-W9). Nothing about a key is kept.
/// </summary>
internal static class AltTabDecider
{
    private const int VkTab = 0x09;
    private const int VkEscape = 0x1B;

    public static AltTabDecision Decide(int vk, bool down, bool altHeld, bool shiftHeld, bool switcherOpen, bool exclusiveFullscreen)
    {
        if (exclusiveFullscreen)
        {
            return AltTabDecision.Pass;
        }

        if (vk == VkTab && altHeld)
        {
            if (!down)
            {
                return new AltTabDecision(true, AltTabCommand.None); // its key-up goes with the swallowed key-down
            }

            return new AltTabDecision(true, switcherOpen
                ? (shiftHeld ? AltTabCommand.Previous : AltTabCommand.Next)
                : (shiftHeld ? AltTabCommand.OpenReverse : AltTabCommand.Open));
        }

        if (vk == VkEscape && switcherOpen && down)
        {
            return new AltTabDecision(true, AltTabCommand.Cancel);
        }

        return AltTabDecision.Pass;
    }
}
