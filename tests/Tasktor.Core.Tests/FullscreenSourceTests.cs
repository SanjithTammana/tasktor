using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Snapshot;
using Tasktor.Contracts.Workflow;
using Tasktor.Core.Features.Windows;
using Tasktor.Core.Rules;
using Tasktor.Core.Windows;
using Tasktor.Tests.Shared;

namespace Tasktor.Core.Tests;

public sealed class FullscreenSourceTests
{
    [Fact]
    public void ForegroundChangesUpdateSnapshotAndRules()
    {
        using var rig = new FeatureRig();
        var monitor = new MonitorInfo("display", new RectPx(0, 0, 100, 100), new RectPx(0, 0, 100, 100), 96, true);
        var feature = rig.Add(new FullscreenSource(rig.Context, null)
        {
            Monitors = () => [monitor],
            Window = hwnd => new WindowInfo { Hwnd = hwnd, Pid = 1, AppId = "code.exe", FramePx = monitor.BoundsPx },
        });
        feature.OnEvent(new WindowEvent(WindowEventKind.Foreground, 1, new ProcKey(1, 0), 0, rig.Clock.UtcNow));
        rig.Pump();
        Assert.Equal(Fullscreen.Borderless, rig.State.Current.Fullscreen);
        var inputs = new RuleInputs();
        feature.Contribute(inputs, rig.Clock.UtcNow);
        Assert.Equal("borderless", inputs.States["fullscreen"]);
        Assert.Equal(1, rig.RulesDirtyCount);
    }
}
