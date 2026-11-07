using Tasktor.Capture.Hook;
using Tasktor.Contracts.Platform;

namespace Tasktor.Capture.Tests;

public sealed class DeadKeyComposerTests
{
    [Theory]
    [InlineData("^", "e", "ê")]
    [InlineData("¨", "u", "ü")]
    [InlineData("´", "e", "é")]
    [InlineData("`", "a", "à")]
    public void Common_accent_pairs_have_a_sensitive_unicode_composition(string accent, string value, string expected)
    {
        var result = DeadKeyComposer.Compose(Sensitive.Of(accent), Sensitive.Of(value), sourceStateKnownClear: true);
        Assert.Equal(DeadKeyCompositionKind.Composed, result.Kind);
        Assert.Equal(expected, result.Text!.Value.Reveal());
        Assert.Equal("<redacted>", result.Text.Value.ToString());
    }

    [Theory]
    [InlineData("^", " ")]
    [InlineData("^", "^")]
    [InlineData("^", "q")]
    [InlineData("?", "e")]
    [InlineData("^", "ee")]
    public void Unhandled_sequences_return_uncertainty_without_partial_text(string accent, string value)
    {
        var result = DeadKeyComposer.Compose(Sensitive.Of(accent), Sensitive.Of(value), sourceStateKnownClear: true);
        Assert.Equal(DeadKeyCompositionKind.Uncertain, result.Kind);
        Assert.Null(result.Text);
    }

    [Fact]
    public void Unknown_native_dead_key_state_prevents_guessing_even_for_a_known_pair()
    {
        var result = DeadKeyComposer.Compose(Sensitive.Of("^"), Sensitive.Of("e"), sourceStateKnownClear: false);
        Assert.Equal(DeadKeyCompositionKind.Uncertain, result.Kind);
        Assert.Null(result.Text);
    }
}
