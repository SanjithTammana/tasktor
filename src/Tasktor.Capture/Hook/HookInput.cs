namespace Tasktor.Capture.Hook;

/// <summary>Discriminates native low-level input; unknown events are never treated as a known kind.</summary>
public enum HookInputKind : byte
{
    Unknown = 0,
    Keyboard = 1,
    Mouse = 2,
}

/// <summary>One native hook sample. Mouse and keyboard flags retain their distinct Win32 meanings.</summary>
public readonly record struct HookInput(HookInputKind Kind, uint Message, uint VirtualKey, uint ScanCode, uint Flags, uint Time, int X, int Y, uint MouseData)
{
    public bool KeyUp => Kind == HookInputKind.Keyboard && Message is 0x0101 or 0x0105;

    public static bool TryKeyboard(uint message, uint vk, uint scan, uint flags, uint time, out HookInput input)
    {
        if (message is 0x0100 or 0x0101 or 0x0104 or 0x0105)
        {
            input = new(HookInputKind.Keyboard, message, vk, scan, flags, time, 0, 0, 0);
            return true;
        }
        input = default;
        return false;
    }

    public static bool TryMouse(uint message, int x, int y, uint data, uint flags, uint time, out HookInput input)
    {
        if (message is >= 0x0200 and <= 0x020e)
        {
            input = new(HookInputKind.Mouse, message, 0, 0, flags, time, x, y, data);
            return true;
        }
        input = default;
        return false;
    }
}

/// <summary>A fixed, allocation-free ring adapter. Called by exactly one hook thread.</summary>
public interface IHookInputSink
{
    /// <summary>Number of entries published; read by the hook thread watchdog.</summary>
    long Published { get; }

    /// <summary>Must only copy to the ring: no allocation, wait, logging, or exception.</summary>
    void Write(in HookInput input);
}
