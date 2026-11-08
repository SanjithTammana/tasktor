using Tasktor.Capture.Deep;

namespace Tasktor.Capture.Tests;

public sealed class PointerLogicTests
{
    [Fact]
    public void Moves_are_spaced_by_at_least_one_hundred_milliseconds()
    {
        var logic = new PointerLogic((_, _) => null);
        var samples = new List<PointerEvent>();
        for (var ms = 0; ms < 1000; ms++)
            if (logic.Process(Mouse(0x0200, ms)) is { } sample) samples.Add(sample);
        Assert.Equal(10, samples.Count);
        Assert.All(samples.Zip(samples.Skip(1)), pair => Assert.True(pair.Second.TimeMs - pair.First.TimeMs >= 100));
    }

    [Fact]
    public void Clicks_get_the_injected_target_and_wheel_preserves_signed_delta()
    {
        var logic = new PointerLogic((x, y) => x == 4 && y == 5 ? "button: synthetic" : null);
        var click = logic.Process(Mouse(0x0201, 10, 4, 5));
        Assert.NotNull(click);
        Assert.Equal(PointerEventKind.Click, click.Kind);
        Assert.Equal("button: synthetic", click.Target);
        var wheel = logic.Process(Mouse(0x020a, 11, data: unchecked((uint)(ushort)(short)-120) << 16));
        Assert.NotNull(wheel);
        Assert.Equal(PointerEventKind.Wheel, wheel.Kind);
        Assert.Equal(-120, wheel.WheelDelta);
    }

    [Fact]
    public void Unknown_mouse_messages_and_keyboard_do_not_produce_pointer_events()
    {
        var logic = new PointerLogic((_, _) => throw new InvalidOperationException());
        Assert.Null(logic.Process(Mouse(0x0202, 1)));
        Assert.Null(logic.Process(new RawInput(1, 0, 0, 1, false)));
    }

    private static RawInput Mouse(uint message, long ms, int x = 0, int y = 0, uint data = 0) =>
        new(0, 0, 0, ms, false, RawInputKind.Mouse, x, y, message, data);
}