using Tasktor.Hosts.Common.Command;

namespace Tasktor.Hosts.Common.Tests;

public sealed class DimSpecTests
{
    [Fact]
    public void The_dim_covers_the_monitor_and_is_skipped_in_high_contrast()
    {
        Assert.Equal((-1920, 0, 1920, 1080, DimSpec.Alpha), DimSpec.For(-1920, 0, 1920, 1080, highContrast: false));
        Assert.Null(DimSpec.For(0, 0, 1920, 1080, highContrast: true));
        Assert.Null(DimSpec.For(0, 0, 0, 1080, highContrast: false));
        Assert.InRange(DimSpec.Alpha, 40, 140); // a dim, not a blackout
    }
}
