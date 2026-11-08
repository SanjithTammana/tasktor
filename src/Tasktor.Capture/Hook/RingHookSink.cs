using Tasktor.Capture.Deep;

namespace Tasktor.Capture.Hook;

/// <summary>Copies a valid native hook value into the existing single-producer InputRing.</summary>
public sealed class RingHookSink(InputRing ring) : IHookInputSink
{
    public long Published => ring.Written;

    public void Write(in HookInput input)
    {
        RawInput raw;
        switch (input.Kind)
        {
            case HookInputKind.Keyboard when input.VirtualKey <= ushort.MaxValue && input.ScanCode <= ushort.MaxValue
                && input.Message is 0x0100 or 0x0101 or 0x0104 or 0x0105:
                raw = new RawInput((ushort)input.VirtualKey, (ushort)input.ScanCode, input.Flags, input.Time, input.KeyUp, RawInputKind.Keyboard, NativeTick32: true);
                break;
            case HookInputKind.Mouse when input.Message is >= 0x0200 and <= 0x020e:
                raw = new RawInput(0, 0, input.Flags, input.Time, false, RawInputKind.Mouse, input.X, input.Y, input.Message, input.MouseData, NativeTick32: true);
                break;
            default:
                return;
        }

        // The native event timestamp is a 32-bit tick. The logic thread must extend wraparound before comparing times.
        ring.Write(in raw);
    }
}

