using Tasktor.Core.Windows;

namespace Tasktor.Core.Tests;

/// <summary>U4 OQ-G1: the command line's hotkey, Win+Alt+T by default and configurable.</summary>
public sealed class HotkeyTests
{
    [Theory]
    [InlineData("Win+Alt+T", "Win+Alt+T", 0x8u | 0x1u, 0x54u)]
    [InlineData("alt + win + t", "Win+Alt+T", 0x8u | 0x1u, 0x54u)] // any order, case, spaces; one canonical form
    [InlineData("Ctrl+Shift+Space", "Ctrl+Shift+Space", 0x2u | 0x4u, 0x20u)]
    [InlineData("Control+Alt+F12", "Ctrl+Alt+F12", 0x2u | 0x1u, 0x7Bu)]
    [InlineData("Win+Ctrl+7", "Win+Ctrl+7", 0x8u | 0x2u, 0x37u)]
    [InlineData("Alt+PageDown", "Alt+PageDown", 0x1u, 0x22u)]
    public void Parses_to_one_canonical_form_and_the_win32_codes(string text, string canonical, uint modifiers, uint key)
    {
        var h = Hotkey.Parse(text, out var error);
        Assert.Null(error);
        Assert.Equal((canonical, modifiers, key), (h!.ToString(), h.Modifiers, h.VirtualKey));
    }

    [Theory]
    [InlineData("T", "Add Ctrl, Alt or Win")]
    [InlineData("Shift+T", "Add Ctrl, Alt or Win")] // Shift alone would steal a capital letter
    [InlineData("Ctrl+Alt", "Add a key")]
    [InlineData("Ctrl+A+B", "Use one key")]
    [InlineData("Ctrl+Ctrl+A", "Each modifier once")]
    [InlineData("Ctrl+Banana", "Unknown key: Banana")]
    [InlineData("Ctrl+Alt+Delete", "Windows keeps Ctrl+Alt+Delete")]
    [InlineData("Win+L", "Windows keeps Win+L")]
    [InlineData("", "Type a shortcut, e.g. Win+Alt+T")]
    public void Refuses_with_plain_words(string text, string error)
    {
        Assert.Null(Hotkey.Parse(text, out var why));
        Assert.Equal(error, why);
    }

    [Fact]
    public void The_default_is_win_alt_t_and_a_bad_setting_falls_back_to_it()
    {
        Assert.Equal("Win+Alt+T", Hotkey.Default.ToString());
        Assert.Equal(Hotkey.Default, Hotkey.FromSetting(null));
        Assert.Equal(Hotkey.Default, Hotkey.FromSetting("Shift+T"));
        Assert.Equal("Ctrl+Shift+Space", Hotkey.FromSetting("ctrl+shift+space").ToString());
    }
}
