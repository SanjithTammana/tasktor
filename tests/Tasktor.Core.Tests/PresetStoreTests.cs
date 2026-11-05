using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Presets;
using Tasktor.Core.Features.Windows;
using Tasktor.Core.Windows;
using Tasktor.Tests.Shared;

namespace Tasktor.Core.Tests;

public sealed class PresetStoreTests
{
    [Fact]
    public void SaveOpenNeverCopiesTitleAndRoundTrips()
    {
        using var rig = new FeatureRig();
        var store = new PresetStore(rig.Context);
        var monitor = new MonitorInfo("display", new RectPx(0, 0, 1000, 800), new RectPx(0, 0, 1000, 800), 96, true);
        var window = new WindowInfo { Hwnd = 1, Pid = 10, AppId = "code.exe", Title = Sensitive.Of("private title"), FramePx = new RectPx(0, 0, 500, 500) };
        var saved = store.SaveOpen("Work", [window], new HashSet<long> { 1 }, new MonitorMap([monitor]));
        Assert.Null(saved.Slots[0].Match?.TitlePattern);
        Assert.DoesNotContain("private title", File.ReadAllText(rig.Store.Layout.Presets));
        Assert.Single(new PresetStore(rig.Context).Load().Presets);
    }

    [Fact]
    public void InvalidJsonReportsLineAndDoesNotOverwrite()
    {
        using var rig = new FeatureRig();
        var text = "{\n invalid\n}";
        File.WriteAllText(rig.Store.Layout.Presets, text);
        var store = new PresetStore(rig.Context);
        Assert.Empty(store.Load().Presets);
        Assert.Contains("line 2", store.Error);
        Assert.Equal(text, File.ReadAllText(rig.Store.Layout.Presets));
    }
}
