using Tasktor.Capture.Deep;

namespace Tasktor.Capture.Tests;

public sealed class KeyRunClockTests
{
    [Fact]
    public void Suppression_uses_wall_clock_not_monotonic_hook_ticks()
    {
        var wall = new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero);
        var observed = new List<DateTimeOffset>();
        var builder = new KeyRunBuilder(new KeyDecoder(), new ActorAttributor(() => []), new SecretRedactor(),
            (_, at) => { observed.Add(at); return at == wall ? Suppression.Blocked : Suppression.None; }, _ => wall);
        builder.Focus(new FocusTarget("view", "field", "example.exe"), 10_000);
        builder.Key(new RawInput(0x41, 0, 0, 10_100, false));
        var run = builder.Close(RunEnd.Flush, 10_200);
        Assert.NotNull(run);
        Assert.Null(run.Text);
        Assert.Equal(Suppression.Blocked, run.Suppressed);
        Assert.Equal(wall, Assert.Single(observed));
    }
    [Fact]
    public void Unknown_password_state_suppresses_text_instead_of_assuming_a_safe_field()
    {
        var blocks = new CaptureBlocks(local: TimeZoneInfo.Utc);
        var builder = new KeyRunBuilder(new KeyDecoder(), new ActorAttributor(() => []), new SecretRedactor(),
            (new Suppressor(blocks) { SecureDesktop = () => false }).Check, _ => DateTimeOffset.UnixEpoch);
        builder.Focus(new FocusTarget("view", "field", "example.exe"), 0);
        builder.Key(new RawInput(0x41, 0, 0, 1, false));
        var run = builder.Close(RunEnd.Flush, 2);
        Assert.NotNull(run);
        Assert.Equal(Suppression.UnknownField, run.Suppressed);
        Assert.Null(run.Text);
        Assert.Equal(1, run.Keys);
    }
}



