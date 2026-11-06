using Tasktor.Capture.Deep;
using Tasktor.Contracts.Platform;

namespace Tasktor.Capture.Tests;

public sealed class DeadKeyRunTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Uncertain_composition_suppresses_the_entire_run_including_a_field_diff()
    {
        var decoder = new KeyDecoder
        {
            DetailedLayout = (vk, _, _, _, _) => vk switch
        {
            0x41 => new(LayoutOutcome.Text, Sensitive.Of("a")),
            0x42 => new(LayoutOutcome.Text, Sensitive.Of("b")),
            _ => new(LayoutOutcome.Uncertain, null),
        }
        };
        var run = Builder(decoder);
        run.Focus(new FocusTarget("view", "field", "editor.exe") { IsPassword = false }, 0);
        run.Key(new RawInput(0x41, 0, 0, 1, false));
        run.Key(new RawInput(0x55, 0, 0, 2, false));
        run.Key(new RawInput(0x42, 0, 0, 3, false));
        var result = Assert.IsType<KeyRun>(run.Close(RunEnd.Flush, 4, "before", "before partial"));
        Assert.Equal(Suppression.UncertainText, result.Suppressed);
        Assert.Equal(3, result.Keys);
        Assert.Null(result.Text);
        Assert.Empty(result.Chords);
    }

    [Fact]
    public void Uncertain_first_key_creates_a_suppressed_run_and_retains_its_count()
    {
        var decoder = new KeyDecoder { DetailedLayout = (_, _, _, _, _) => new(LayoutOutcome.Uncertain, null) };
        var run = Builder(decoder);
        run.Focus(new FocusTarget("view", "field", "editor.exe") { IsPassword = false }, 0);
        run.Key(new RawInput(0x41, 0, 0, 1, false));
        var result = Assert.IsType<KeyRun>(run.Close(RunEnd.Flush, 2));
        Assert.Equal(Suppression.UncertainText, result.Suppressed);
        Assert.Equal(1, result.Keys);
        Assert.Null(result.Text);
    }

    [Fact]
    public void No_text_is_not_uncertainty_and_unknown_outcomes_fail_closed()
    {
        var decoder = new KeyDecoder
        {
            DetailedLayout = (vk, _, _, _, _) => vk switch
        {
            0x41 => new(LayoutOutcome.NoText, null),
            0x42 => new(LayoutOutcome.Text, Sensitive.Of("b")),
            _ => default,
        }
        };
        var run = Builder(decoder);
        run.Focus(new FocusTarget("view", "field", "editor.exe") { IsPassword = false }, 0);
        run.Key(new RawInput(0x41, 0, 0, 1, false));
        run.Key(new RawInput(0x42, 0, 0, 2, false));
        var first = Assert.IsType<KeyRun>(run.Close(RunEnd.Flush, 3));
        Assert.Equal("b", first.Text);
        Assert.Equal(1, first.Keys);
        run.Key(new RawInput(0x43, 0, 0, 4, false));
        var second = Assert.IsType<KeyRun>(run.Close(RunEnd.Flush, 5));
        Assert.Equal(Suppression.UncertainText, second.Suppressed);
        Assert.Null(second.Text);
        Assert.Equal(1, second.Keys);
    }

    [Fact]
    public void Uncertainty_never_replaces_an_existing_password_suppression()
    {
        var decoder = new KeyDecoder { DetailedLayout = (_, _, _, _, _) => new(LayoutOutcome.Uncertain, null) };
        var run = Builder(decoder);
        run.Focus(new FocusTarget("view", "field", "editor.exe") { IsPassword = true }, 0);
        run.Key(new RawInput(0x41, 0, 0, 1, false));
        var result = Assert.IsType<KeyRun>(run.Close(RunEnd.Flush, 2));
        Assert.Equal(Suppression.Password, result.Suppressed);
        Assert.Null(result.Text);
        Assert.Equal(1, result.Keys);
    }

    private static KeyRunBuilder Builder(KeyDecoder decoder) => new(decoder, new ActorAttributor(() => []), new SecretRedactor(),
        new Suppressor(new CaptureBlocks(local: TimeZoneInfo.Utc)).Check, _ => Now);
}
