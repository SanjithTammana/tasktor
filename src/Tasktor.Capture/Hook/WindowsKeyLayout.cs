using System.Runtime.InteropServices;
using Tasktor.Contracts.Platform;

namespace Tasktor.Capture.Hook;

/// <summary>Explicit native translation outcome; a dead key is never guessed as text.</summary>
public enum LayoutResultKind : byte
{
    Unknown = 0,
    Text = 1,
    NoText = 2,
    DeadKey = 3,
}

public readonly record struct LayoutResult(LayoutResultKind Kind, Sensitive<string>? Text);

/// <summary>
/// Windows layout translation for the capture logic thread only. Bit 2 prevents changes to the user's keyboard state.
/// Dead keys are reported explicitly; composing layout-specific sequences and uncertainty suppression remain the
/// caller's responsibility. Windows can still consult an existing dead-key state even with bit 2; therefore this
/// adapter must not be integrated as complete composition. A zero or unknown layout is not replaced with US.
/// </summary>
public static unsafe partial class WindowsKeyLayout
{
    private const uint PreserveKeyboardState = 4;

    public static LayoutResult Translate(ushort vk, ushort scan, bool shift, bool caps, bool altGr, nint layout)
    {
        if (!OperatingSystem.IsWindows() || layout == 0) return new(LayoutResultKind.Unknown, null);
        Span<byte> state = stackalloc byte[256];
        state.Clear();
        if (shift) state[0x10] = 0x80;
        if (caps) state[0x14] = 0x01;
        if (altGr)
        {
            state[0x11] = 0x80;
            state[0x12] = 0x80;
            state[0xA5] = 0x80;
        }

        Span<char> output = stackalloc char[16];
        fixed (byte* keys = state)
        fixed (char* chars = output)
        {
            var count = ToUnicodeEx(vk, scan, keys, chars, output.Length, PreserveKeyboardState, layout);
            return Classify(count, output);
        }
    }

    internal static LayoutResult Classify(int count, ReadOnlySpan<char> output)
    {
        if (count < 0) return new(LayoutResultKind.DeadKey, null);
        if (count == 0) return new(LayoutResultKind.NoText, null);
        if (count > output.Length) return new(LayoutResultKind.Unknown, null);
        return new(LayoutResultKind.Text, Sensitive.Of(new string(output[..count])));
    }

    [LibraryImport("user32.dll")]
    private static partial int ToUnicodeEx(uint vk, uint scan, byte* state, char* output, int length, uint flags, nint layout);
}

