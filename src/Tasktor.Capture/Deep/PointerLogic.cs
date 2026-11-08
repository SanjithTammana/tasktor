namespace Tasktor.Capture.Deep;

/// <summary>A shaped pointer event; times use the hook's monotonic millisecond clock.</summary>
public enum PointerEventKind : byte { Move, Click, Wheel }

public sealed record PointerEvent(PointerEventKind Kind, long TimeMs, int X, int Y, byte Button = 0, int WheelDelta = 0, string? Target = null);

/// <summary>Shapes pointer movement and decodes clicks and wheel input on the capture logic thread.</summary>
public sealed class PointerLogic(Func<int, int, string?> target)
{
    private readonly Func<int, int, string?> _target = target ?? throw new ArgumentNullException(nameof(target));
    private long? _lastMove;

    public PointerEvent? Process(RawInput input)
    {
        if (input.Kind != RawInputKind.Mouse) return null;
        if (input.MouseMessage == 0x0200)
        {
            if (_lastMove is { } last && input.TimeMs >= last && input.TimeMs - last < 100) return null;
            _lastMove = input.TimeMs;
            return new(PointerEventKind.Move, input.TimeMs, input.MouseX, input.MouseY);
        }

        var button = input.MouseMessage switch { 0x0201 => (byte)1, 0x0204 => (byte)2, 0x0207 => (byte)3, 0x020b => (byte)4, _ => (byte)0 };
        if (button != 0)
            return new(PointerEventKind.Click, input.TimeMs, input.MouseX, input.MouseY, button, Target: _target(input.MouseX, input.MouseY));
        if (input.MouseMessage is 0x020a or 0x020e)
            return new(PointerEventKind.Wheel, input.TimeMs, input.MouseX, input.MouseY, WheelDelta: unchecked((short)(input.MouseData >> 16)));
        return null;
    }
}