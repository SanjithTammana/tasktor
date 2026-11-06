using System.Runtime.CompilerServices;
using Tasktor.Core.Logging;

namespace Tasktor.Core.Tests;

/// <summary>G-redact: error lines say where (Type.Method), never what (messages, paths, arguments).</summary>
public sealed class ErrorWhereTests
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Explode(string secret) => throw new InvalidOperationException("contains " + secret + " C:\\Users\\someone");

    [Fact]
    public void Names_the_throwing_method_and_nothing_from_the_message()
    {
        try
        {
            Explode("hunter2");
        }
        catch (InvalidOperationException ex)
        {
            var at = ErrorWhere.Of(ex);
            Assert.StartsWith("ErrorWhereTests.Explode", at, StringComparison.Ordinal);
            Assert.DoesNotContain("hunter2", at, StringComparison.Ordinal);
            Assert.DoesNotContain("Users", at, StringComparison.Ordinal);
            Assert.True(at.Split('<').Length <= ErrorWhere.MaxFrames);
        }
    }

    [Fact]
    public void An_exception_never_thrown_has_no_frames() => Assert.Equal("-", ErrorWhere.Of(new InvalidOperationException()));
}
