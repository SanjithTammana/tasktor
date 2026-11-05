using System.Globalization;

namespace Tasktor.Capture.Deep;

/// <summary>
/// Turns raw key events into text, edits and chords (D-C19), on the logic thread with its own key state (never on the hook
/// thread). Shift / Caps / Ctrl / Alt / Win are tracked from the stream itself. Ctrl, Alt or Win with a key is a chord
/// (<c>Ctrl+S</c>), never text; AltGr (Ctrl+Alt) yields text through the layout. The portable table is US QWERTY; on
/// Windows the composition passes <see cref="Layout"/> = a <c>ToUnicodeEx</c> call (wFlags bit 2, no state change).
/// </summary>
public sealed class KeyDecoder
{
    public const ushort VkBack = 0x08, VkTab = 0x09, VkReturn = 0x0D, VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12, VkCapital = 0x14;
    public const ushort VkLShift = 0xA0, VkRShift = 0xA1, VkLControl = 0xA2, VkRControl = 0xA3, VkLMenu = 0xA4, VkRMenu = 0xA5, VkLWin = 0x5B, VkRWin = 0x5C;
    public const ushort VkDelete = 0x2E, VkEscape = 0x1B;

    private const string ShiftedDigits = ")!@#$%^&*(";

    private bool _shiftL, _shiftR, _ctrlL, _ctrlR, _altL, _altR, _winL, _winR, _caps;

    /// <summary>Layout hook: (vk, scan, shift, caps, altGr) → text, or null to use the built-in US table.</summary>
    public Func<ushort, ushort, bool, bool, bool, string?>? Layout { get; init; }

    public bool Shift => _shiftL || _shiftR;

    public bool Ctrl => _ctrlL || _ctrlR;

    public bool Alt => _altL || _altR;

    public bool Win => _winL || _winR;

    public DecodedKey Decode(RawInput k)
    {
        if (Modifier(k) || k.KeyUp)
        {
            return new DecodedKey(KeyAction.None, null, k.TimeMs, k.IsInjected, k.IsLowerIlInjected);
        }

        var altGr = Ctrl && Alt && !Win;
        if ((Ctrl || Alt || Win) && !altGr)
        {
            if (KeyName(k.Vk) is not { } name)
            {
                return new DecodedKey(KeyAction.None, null, k.TimeMs, k.IsInjected, k.IsLowerIlInjected);
            }

            var parts = new List<string>(5);
            if (Ctrl)
            {
                parts.Add("Ctrl");
            }

            if (Alt)
            {
                parts.Add("Alt");
            }

            if (Shift)
            {
                parts.Add("Shift");
            }

            if (Win)
            {
                parts.Add("Win");
            }

            parts.Add(name);
            return new DecodedKey(KeyAction.Chord, string.Join('+', parts), k.TimeMs, k.IsInjected, k.IsLowerIlInjected);
        }

        var action = k.Vk switch
        {
            VkBack => KeyAction.Backspace,
            VkDelete => KeyAction.Delete,
            VkReturn => KeyAction.Enter,
            VkTab => KeyAction.Tab,
            >= 0x21 and <= 0x28 => KeyAction.Navigate, // PgUp, PgDn, End, Home, arrows
            _ => KeyAction.Text,
        };
        if (action != KeyAction.Text)
        {
            return new DecodedKey(action, null, k.TimeMs, k.IsInjected, k.IsLowerIlInjected);
        }

        var text = Layout?.Invoke(k.Vk, k.Scan, Shift, _caps, altGr) ?? Us(k.Vk, Shift, _caps);
        return text is null
            ? new DecodedKey(KeyAction.None, null, k.TimeMs, k.IsInjected, k.IsLowerIlInjected)
            : new DecodedKey(KeyAction.Text, text, k.TimeMs, k.IsInjected, k.IsLowerIlInjected);
    }

    private bool Modifier(RawInput k)
    {
        var down = !k.KeyUp;
        switch (k.Vk)
        {
            case VkShift or VkLShift:
                _shiftL = down;
                return true;
            case VkRShift:
                _shiftR = down;
                return true;
            case VkControl or VkLControl:
                _ctrlL = down;
                return true;
            case VkRControl:
                _ctrlR = down;
                return true;
            case VkMenu or VkLMenu:
                _altL = down;
                return true;
            case VkRMenu:
                _altR = down;
                return true;
            case VkLWin:
                _winL = down;
                return true;
            case VkRWin:
                _winR = down;
                return true;
            case VkCapital:
                if (down)
                {
                    _caps = !_caps;
                }

                return true;
            default:
                return false;
        }
    }

    /// <summary>US QWERTY: letters (Shift xor Caps), digits, space, punctuation, numpad.</summary>
    public static string? Us(ushort vk, bool shift, bool caps)
    {
        if (vk is >= 0x41 and <= 0x5A)
        {
            var c = (char)('a' + (vk - 0x41));
            return (shift ^ caps ? char.ToUpperInvariant(c) : c).ToString();
        }

        if (vk is >= 0x30 and <= 0x39)
        {
            return shift ? ShiftedDigits[vk - 0x30].ToString() : ((char)vk).ToString();
        }

        if (vk is >= 0x60 and <= 0x69)
        {
            return ((char)('0' + (vk - 0x60))).ToString();
        }

        return vk switch
        {
            0x20 => " ",
            0xBA => shift ? ":" : ";",
            0xBB => shift ? "+" : "=",
            0xBC => shift ? "<" : ",",
            0xBD => shift ? "_" : "-",
            0xBE => shift ? ">" : ".",
            0xBF => shift ? "?" : "/",
            0xC0 => shift ? "~" : "`",
            0xDB => shift ? "{" : "[",
            0xDC => shift ? "|" : "\\",
            0xDD => shift ? "}" : "]",
            0xDE => shift ? "\"" : "'",
            0x6A => "*",
            0x6B => "+",
            0x6D => "-",
            0x6E => ".",
            0x6F => "/",
            _ => null,
        };
    }

    /// <summary>The key name used in chords (<c>S</c>, <c>F5</c>, <c>Enter</c>), or null for keys that make no chord.</summary>
    public static string? KeyName(ushort vk) => vk switch
    {
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F).ToString(CultureInfo.InvariantCulture),
        VkReturn => "Enter",
        VkTab => "Tab",
        VkBack => "Backspace",
        VkDelete => "Delete",
        VkEscape => "Esc",
        0x20 => "Space",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "PageUp",
        0x22 => "PageDown",
        _ => Us(vk, false, false)?.ToUpperInvariant(),
    };
}
