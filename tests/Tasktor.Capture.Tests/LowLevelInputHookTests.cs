using Tasktor.Capture.Deep;
using Tasktor.Capture.Hook;

namespace Tasktor.Capture.Tests;

public sealed class LowLevelInputHookTests
{
    [Fact]
    public void Watchdog_reinstalls_only_when_input_advanced_and_hooks_are_silent_for_30_seconds()
    {
        var watchdog = new HookWatchdog(1000, 1000, 0);
        Assert.False(watchdog.ShouldReinstall(30999, 2000, 0));
        Assert.True(watchdog.ShouldReinstall(31000, 2000, 0));
        Assert.False(watchdog.ShouldReinstall(61000, 2000, 0));
        Assert.True(watchdog.ShouldReinstall(61001, 3000, 0));
    }

    [Fact]
    public void Watchdog_resets_silence_when_the_ring_advances()
    {
        var watchdog = new HookWatchdog(1000, 1000, 0);
        Assert.False(watchdog.ShouldReinstall(30000, 2000, 1));
        Assert.False(watchdog.ShouldReinstall(59999, 3000, 1));
        Assert.True(watchdog.ShouldReinstall(60000, 3000, 1));
    }

    [Fact]
    public void Keyboard_and_mouse_keep_their_original_flags_and_unknown_messages_are_ignored()
    {
        Assert.True(HookInput.TryKeyboard(0x0100, 65, 30, 0x12, 123, out var key));
        Assert.Equal(HookInputKind.Keyboard, key.Kind);
        Assert.Equal(0x12u, key.Flags);
        Assert.False(key.KeyUp);
        Assert.True(HookInput.TryKeyboard(0x0105, 65, 30, 0x10, 124, out var up));
        Assert.True(up.KeyUp);
        Assert.False(HookInput.TryKeyboard(0xffff, 65, 30, 0, 125, out _));
        Assert.True(HookInput.TryMouse(0x020a, -12, 34, 0xff880000, 0x03, 126, out var mouse));
        Assert.Equal(HookInputKind.Mouse, mouse.Kind);
        Assert.Equal((0x020au, -12, 34, 0xff880000u, 0x03u), (mouse.Message, mouse.X, mouse.Y, mouse.MouseData, mouse.Flags));
        Assert.False(HookInput.TryMouse(0xffff, 0, 0, 0, 0, 127, out _));
    }
    [Fact]
    public void Ring_sink_keeps_mouse_flags_and_payload_and_ignores_unknown_input()
    {
        var ring = new InputRing(8);
        var sink = new RingHookSink(ring);
        var unknown = default(HookInput);
        sink.Write(in unknown);
        var invalidKey = new HookInput(HookInputKind.Keyboard, 0x0100, 70000, 1, 0, 1, 0, 0, 0);
        sink.Write(in invalidKey);
        var invalidMessage = new HookInput(HookInputKind.Mouse, 0xffff, 0, 0, 0, 1, 0, 0, 0);
        sink.Write(in invalidMessage);
        Assert.Equal(0, sink.Published);
        Assert.True(HookInput.TryMouse(0x020a, -10, 20, 0xff880000, 0x03, 0xfffffffe, out var mouse));
        sink.Write(in mouse);
        Assert.Equal(1, sink.Published);
        var buffer = new RawInput[2];
        Assert.Equal(1, ring.Drain(buffer));
        var written = buffer[0];
        Assert.Equal(RawInputKind.Mouse, written.Kind);
        Assert.Equal((0x03u, -10, 20, 0x020au, 0xff880000u, 0xfffffffeL), (written.Flags, written.MouseX, written.MouseY, written.MouseMessage, written.MouseData, written.TimeMs));
        Assert.True(written.NativeTick32);
        Assert.True(written.IsInjected);
        Assert.True(written.IsLowerIlInjected);
    }
    [Fact]
    public void Layout_result_distinguishes_dead_keys_no_text_and_invalid_native_lengths()
    {
        var buffer = new[] { 'a', 'b' };
        Assert.Equal(LayoutResultKind.DeadKey, WindowsKeyLayout.Classify(-1, buffer).Kind);
        Assert.Equal(LayoutResultKind.NoText, WindowsKeyLayout.Classify(0, buffer).Kind);
        Assert.Equal(LayoutResultKind.Unknown, WindowsKeyLayout.Classify(3, buffer).Kind);
        var text = WindowsKeyLayout.Classify(1, buffer);
        Assert.Equal(LayoutResultKind.Text, text.Kind);
        Assert.Equal("a", text.Text!.Value.Reveal());
    }
    [Fact]
    public void Watchdog_uses_unsigned_tick_arithmetic_across_wraparound()
    {
        var start = uint.MaxValue - 9999;
        var watchdog = new HookWatchdog(start, start, 0);
        Assert.False(watchdog.ShouldReinstall(unchecked(start + 29999), 200, 0));
        Assert.True(watchdog.ShouldReinstall(unchecked(start + 30000), 200, 0));
    }
}




