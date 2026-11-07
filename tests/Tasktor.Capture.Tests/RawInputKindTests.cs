using Tasktor.Capture.Deep;

namespace Tasktor.Capture.Tests;

public sealed class RawInputKindTests
{
    [Fact]
    public void Ring_preserves_keyboard_and_mouse_kinds_and_native_flags()
    {
        var ring = new InputRing(8);
        var key = new RawInput(0x41, 0, RawInput.Injected, 10, false);
        var mouse = new RawInput(0, 0, 1, 11, false, RawInputKind.Mouse, 120, -8, 0x0201, 0);
        ring.Write(key);
        ring.Write(mouse);
        var result = new RawInput[2];
        Assert.Equal(2, ring.Drain(result));
        Assert.Equal(RawInputKind.Keyboard, result[0].Kind);
        Assert.True(result[0].IsInjected);
        Assert.Equal(RawInputKind.Mouse, result[1].Kind);
        Assert.True(result[1].IsInjected);
        Assert.Equal((120, -8, 0x0201u), (result[1].MouseX, result[1].MouseY, result[1].MouseMessage));
        Assert.Equal(2, ring.Written);
    }

    [Fact]
    public void Native_ticks_extend_across_the_32_bit_wrap_without_changing_synthetic_ticks()
    {
        var before = new RawInput(0x41, 0, 0, 0xfffffff0, false, NativeTick32: true);
        var after = new RawInput(0x41, 0, 0, 5, false, NativeTick32: true);
        var synthetic = new RawInput(0x41, 0, 0, 42, false);
        const long now = 0x100000010;
        Assert.Equal(0xfffffff0L, before.ExtendTick(now).TimeMs);
        Assert.Equal(0x100000005L, after.ExtendTick(now).TimeMs);
        Assert.Equal(42, synthetic.ExtendTick(now).TimeMs);
    }
    [Fact]
    public void Unknown_key_in_explicit_layout_does_not_fall_back_to_us_text()
    {
        var decoder = new KeyDecoder { Layout = (_, _, _, _, _) => null };
        Assert.Equal(KeyAction.None, decoder.Decode(new RawInput(0x41, 0, 0, 1, false)).Action);
    }
    [Fact]
    public void Unknown_and_mouse_events_are_never_decoded_as_keyboard_input()
    {
        var decoder = new KeyDecoder();
        Assert.Equal(KeyAction.None, decoder.Decode(default).Action);
        Assert.Equal(KeyAction.None, decoder.Decode(new RawInput(0x41, 0, 0, 1, false, RawInputKind.Mouse)).Action);
    }
}


