namespace Tasktor.Capture.Tests;

public sealed class ScaffoldTests
{
    [Fact]
    public void Assembly_loads() => Assert.NotNull(typeof(ScaffoldTests).Assembly);
}
