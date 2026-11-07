using System.Globalization;

namespace Tasktor.Core.Windows;

/// <summary>
/// U4 OQ-G1 / D-G12: a global hotkey (the command line's: Win+Alt+T by default, configurable). Parsed from text such as
/// <c>Ctrl+Shift+Space</c> into <c>RegisterHotKey</c>'s modifier flags and virtual-key code; written back in one canonical
/// order (Win, Ctrl, Alt, Shift). It needs Ctrl, Alt or Win (Shift alone would steal typing) and exactly one key.
/// </summary>
public sealed record Hotkey(uint Modifiers, uint VirtualKey, string Key)
{
    public const uint Alt = 0x1;
    public const uint Ctrl = 0x2;
    public const uint Shift = 0x4;
    public const uint Win = 0x8;

    public static Hotkey Default { get; } = new(Win | Alt, 0x54, "T");

    private static readonly (string Name, uint Flag)[] Order = [("Win", Win), ("Ctrl", Ctrl), ("Alt", Alt), ("Shift", Shift)];

    private static readonly Dictionary<string, uint> ModifierNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["win"] = Win,
        ["windows"] = Win,
        ["ctrl"] = Ctrl,
        ["control"] = Ctrl,
        ["alt"] = Alt,
        ["shift"] = Shift,
    };

    private static readonly Dictionary<string, (string Name, uint Vk)> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["space"] = ("Space", 0x20),
        ["enter"] = ("Enter", 0x0D),
        ["tab"] = ("Tab", 0x09),
        ["esc"] = ("Esc", 0x1B),
        ["escape"] = ("Esc", 0x1B),
        ["backspace"] = ("Backspace", 0x08),
        ["insert"] = ("Insert", 0x2D),
        ["delete"] = ("Delete", 0x2E),
        ["del"] = ("Delete", 0x2E),
        ["home"] = ("Home", 0x24),
        ["end"] = ("End", 0x23),
        ["pageup"] = ("PageUp", 0x21),
        ["pagedown"] = ("PageDown", 0x22),
        ["up"] = ("Up", 0x26),
        ["down"] = ("Down", 0x28),
        ["left"] = ("Left", 0x25),
        ["right"] = ("Right", 0x27),
        ["pause"] = ("Pause", 0x13),
        ["`"] = ("`", 0xC0),
        ["-"] = ("-", 0xBD),
        ["="] = ("=", 0xBB),
        [","] = (",", 0xBC),
        ["."] = (".", 0xBE),
        ["/"] = ("/", 0xBF),
        [";"] = (";", 0xBA),
        ["'"] = ("'", 0xDE),
        ["["] = ("[", 0xDB),
        ["]"] = ("]", 0xDD),
    };

    /// <summary>Combinations Windows keeps for itself (never delivered to <c>RegisterHotKey</c>, or unsafe to take).</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal) { "Ctrl+Alt+Delete", "Win+L" };

    /// <summary>Parse <paramref name="text"/>; null with <paramref name="error"/> in plain words when it cannot be used.</summary>
    public static Hotkey? Parse(string? text, out string? error)
    {
        error = null;
        var parts = (text ?? string.Empty).Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            error = "Type a shortcut, e.g. Win+Alt+T";
            return null;
        }

        uint mods = 0;
        var keys = new List<string>();
        foreach (var p in parts)
        {
            if (ModifierNames.TryGetValue(p, out var flag))
            {
                if ((mods & flag) != 0)
                {
                    error = "Each modifier once";
                    return null;
                }

                mods |= flag;
            }
            else
            {
                keys.Add(p);
            }
        }

        if (keys.Count == 0)
        {
            error = "Add a key";
            return null;
        }

        if (keys.Count > 1)
        {
            error = "Use one key";
            return null;
        }

        if (KeyOf(keys[0]) is not { } key)
        {
            error = "Unknown key: " + keys[0];
            return null;
        }

        if ((mods & (Ctrl | Alt | Win)) == 0)
        {
            error = "Add Ctrl, Alt or Win";
            return null;
        }

        var hotkey = new Hotkey(mods, key.Vk, key.Name);
        if (Reserved.Contains(hotkey.ToString()))
        {
            error = "Windows keeps " + hotkey;
            return null;
        }

        return hotkey;
    }

    /// <summary>The setting's hotkey, or the default when it is missing or unusable.</summary>
    public static Hotkey FromSetting(string? text) => Parse(text, out _) ?? Default;

    public override string ToString() => string.Join('+', Order.Where(o => (Modifiers & o.Flag) != 0).Select(o => o.Name).Append(Key));

    private static (string Name, uint Vk)? KeyOf(string text)
    {
        if (text.Length == 1 && char.IsAsciiLetterOrDigit(text[0]))
        {
            var c = char.ToUpperInvariant(text[0]);
            return (c.ToString(), c); // 'A'–'Z' and '0'–'9' are their own virtual-key codes
        }

        if (text.Length is 2 or 3 && (text[0] is 'F' or 'f') && int.TryParse(text.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 24)
        {
            return ("F" + n.ToString(CultureInfo.InvariantCulture), (uint)(0x70 + n - 1));
        }

        return Named.TryGetValue(text, out var named) ? named : null;
    }
}
