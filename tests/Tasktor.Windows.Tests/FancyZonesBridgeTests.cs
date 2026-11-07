using Tasktor.Windows.Shell;

namespace Tasktor.Windows.Tests;

public sealed class FancyZonesBridgeTests
{
    [Fact]
    public void ArgumentsPreserveMonitorAndLayoutAsSeparateArguments() =>
        Assert.Equal(["set-layout", "layout one", "--monitor", "display two"], FancyZonesBridge.Arguments("display two", "layout one"));
}
