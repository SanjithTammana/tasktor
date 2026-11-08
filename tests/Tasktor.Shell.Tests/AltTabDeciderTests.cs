namespace Tasktor.Shell.Tests;

/// <summary>D-W3 opt-in "Replace Alt+Tab": which keys the hook swallows and what it asks the switcher to do.</summary>
public sealed class AltTabDeciderTests
{
    private const int Tab = 0x09, Esc = 0x1B, Alt = 0x12, LAlt = 0xA4, Shift = 0x10, A = 0x41;

    private static AltTabDecision D(int vk, bool down = true, bool alt = true, bool shift = false, bool open = false, bool exclusive = false) =>
        AltTabDecider.Decide(vk, down, alt, shift, open, exclusive);

    [Fact]
    public void Alt_tab_opens_moves_and_goes_back_and_the_native_one_never_sees_it()
    {
        Assert.Equal(new AltTabDecision(true, AltTabCommand.Open), D(Tab));
        Assert.Equal(new AltTabDecision(true, AltTabCommand.OpenReverse), D(Tab, shift: true));
        Assert.Equal(new AltTabDecision(true, AltTabCommand.Next), D(Tab, open: true));
        Assert.Equal(new AltTabDecision(true, AltTabCommand.Previous), D(Tab, shift: true, open: true));
        Assert.Equal(new AltTabDecision(true, AltTabCommand.None), D(Tab, down: false)); // its key-up goes with it
        Assert.Equal(new AltTabDecision(true, AltTabCommand.Cancel), D(Esc, open: true));
    }

    [Fact]
    public void Everything_else_passes_through_untouched()
    {
        Assert.Equal(AltTabDecision.Pass, D(Tab, alt: false)); // a plain Tab
        Assert.Equal(AltTabDecision.Pass, D(Esc)); // Esc while the switcher is closed
        Assert.Equal(AltTabDecision.Pass, D(A, open: true)); // letters reach the open switcher as typing
        Assert.Equal(AltTabDecision.Pass, D(Alt, down: false, open: true)); // Alt up is never swallowed (no stuck Alt)
        Assert.Equal(AltTabDecision.Pass, D(LAlt, down: false, open: true));
        Assert.Equal(AltTabDecision.Pass, D(Shift));
    }

    [Fact]
    public void An_exclusive_fullscreen_app_keeps_the_native_alt_tab()
    {
        Assert.Equal(AltTabDecision.Pass, D(Tab, exclusive: true));
        Assert.Equal(AltTabDecision.Pass, D(Tab, down: false, exclusive: true));
    }

    [Fact]
    public void The_hook_source_stores_and_forwards_nothing()
    {
        // G-redact family (D-W3): the hook module may only compare keys and post a message.
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Tasktor.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        var source = File.ReadAllText(Path.Combine(dir!, "src", "Tasktor.Shell", "KeyChordHook.cs"));
        foreach (var forbidden in new[] { "File.", "Stream", "Log", "Console", "WriteAllText", "Append", "Sensitive", "ToUnicode", "GetKeyNameText", "List<", "Queue<", "StringBuilder" })
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        }
    }
}
