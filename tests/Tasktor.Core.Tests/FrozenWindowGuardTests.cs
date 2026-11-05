using Tasktor.Contracts.Platform;
using Tasktor.Core.Features.Jobs;
using Tasktor.Core.Features.Windows;
using Tasktor.Tests.Shared;

namespace Tasktor.Core.Tests;

public sealed class FrozenWindowGuardTests
{
    [Fact]
    public void FrozenPidsAreBlockedAndMalformedFileFailsClosed()
    {
        using var rig = new FeatureRig();
        var path = rig.Store.Layout.Frozen;
        Assert.False(FrozenWindowGuard.IsFrozen(path, 42));
        var registry = new FrozenRegistry(path);
        registry.Begin("job", [new ProcKey(42, 1)], rig.Clock.UtcNow);
        Assert.True(FrozenWindowGuard.IsFrozen(path, 42));
        Assert.False(FrozenWindowGuard.IsFrozen(path, 43));
        File.WriteAllText(path, "{");
        Assert.True(FrozenWindowGuard.IsFrozen(path, 43));
    }
}
