using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Presets;
using Tasktor.Core.Features.Windows;
using Tasktor.Core.Windows;
using Tasktor.Tests.Shared;

namespace Tasktor.Core.Tests;

public sealed class PresetRestorerTests
{
    [Fact]
    public void ArrangeMovesOpenWindowsAndUndoRestoresPlacement()
    {
        using var rig = new FeatureRig();
        var window = new WindowInfo { Hwnd = 1, Pid = 7, AppId = "code.exe", FramePx = new RectPx(0, 0, 200, 200), WindowPx = new RectPx(0, 0, 200, 200) };
        var monitor = new MonitorInfo("display", new RectPx(0, 0, 1000, 800), new RectPx(0, 0, 1000, 800), 96, true);
        var preset = new Preset { Name = "Work", Slots = [new PresetSlot { App = new PresetApp { AppId = "code.exe" }, Place = new PresetPlace { Monitor = "display", Rect = new DipRect { X = 100, Y = 100, W = 300, H = 300 } } }] };
        new PresetStore(rig.Context).Save(new PresetsFile { Presets = [preset] });
        var moves = new List<WindowMove>();
        var feature = rig.Add(new PresetRestorer(rig.Context) { Windows = () => [window], UserChangedSince = (_, _) => false, Monitors = () => [monitor], Move = move => { moves.Add(move); return true; } });
        rig.Pump();
        var result = rig.Act(new ActionRequest { Action = "preset.arrange", Origin = Origin.Flyout, Target = new Dictionary<string, string> { ["preset"] = "Work" } });
        Assert.Equal(ActionStatus.Ok, result.Status);
        Assert.Single(moves);
        Assert.Equal(new RectPx(100, 100, 300, 300), moves[0].Rect);
        Assert.Single(rig.Notifier.Shown);
        var undo = rig.Act(new ActionRequest { Action = "preset.undo", Origin = Origin.Flyout });
        Assert.Equal(ActionStatus.Ok, undo.Status);
        Assert.Equal(window.WindowPx, moves[1].Rect);
        Assert.Equal("presets", feature.Id);
    }
    [Fact]
    public void RuleCannotLaunchAndFrozenWindowsAreSkipped()
    {
        using var rig = new FeatureRig();
        var monitor = new MonitorInfo("display", new RectPx(0, 0, 1000, 800), new RectPx(0, 0, 1000, 800), 96, true);
        var frozen = new WindowInfo { Hwnd = 1, Pid = 7, AppId = "code.exe", FramePx = new RectPx(0, 0, 200, 200), WindowPx = new RectPx(0, 0, 200, 200) };
        PresetPlace Place() => new() { Monitor = "display", Rect = new DipRect { W = 300, H = 300 } };
        var preset = new Preset
        {
            Name = "Work",
            Slots = [
            new PresetSlot { App = new PresetApp { AppId = "code.exe" }, Place = Place() },
            new PresetSlot { App = new PresetApp { AppId = "chrome.exe" }, Place = Place(), Launch = new PresetLaunch { Exe = "chrome.exe" } },
        ]
        };
        new PresetStore(rig.Context).Save(new PresetsFile { Presets = [preset] });
        var launches = 0;
        var moves = 0;
        rig.Add(new PresetRestorer(rig.Context) { Windows = () => [frozen], UserChangedSince = (_, _) => false, Monitors = () => [monitor], IsFrozen = pid => pid == 7, Move = _ => { moves++; return true; }, Launch = _ => { launches++; return true; } });
        rig.Pump();
        var result = rig.Act(new ActionRequest { Action = "preset.arrange", Origin = Origin.Rule, Rule = "test", Target = new Dictionary<string, string> { ["preset"] = "Work" } });
        Assert.Equal(ActionStatus.Partial, result.Status);
        Assert.Equal(0, moves);
        Assert.Equal(0, launches);
        Assert.Contains(result.Items!, item => item.Reason == "frozen job");
        Assert.Contains(result.Items!, item => item.Reason == "not open");
        var apply = rig.Act(new ActionRequest { Action = "preset.apply", Origin = Origin.Rule, Rule = "test", Target = new Dictionary<string, string> { ["preset"] = "Work" } });
        Assert.Equal(ActionStatus.Refused, apply.Status);
        Assert.Equal(0, launches);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LaunchedWindowIsMovedUnlessUserIntervened(bool intervened)
    {
        using var rig = new FeatureRig();
        var monitor = new MonitorInfo("display", new RectPx(0, 0, 1000, 800), new RectPx(0, 0, 1000, 800), 96, true);
        var window = new WindowInfo { Hwnd = 12, Pid = 7, AppId = "code.exe", FramePx = new RectPx(0, 0, 200, 200), WindowPx = new RectPx(0, 0, 200, 200) };
        var preset = new Preset { Name = "Work", Slots = [new PresetSlot { App = new PresetApp { AppId = "code.exe" }, Place = new PresetPlace { Monitor = "display", Rect = new DipRect { W = 300, H = 300 } }, Launch = new PresetLaunch { Exe = "code.exe" } }] };
        new PresetStore(rig.Context).Save(new PresetsFile { Presets = [preset] });
        var opened = false;
        var moves = new List<WindowMove>();
        rig.Add(new PresetRestorer(rig.Context) { Windows = () => opened ? [window] : [], UserChangedSince = (_, _) => intervened, Monitors = () => [monitor], Move = move => { moves.Add(move); return true; }, Launch = _ => { opened = true; return true; } });
        rig.Pump();
        var result = rig.Act(new ActionRequest { Action = "preset.apply", Origin = Origin.Flyout, Target = new Dictionary<string, string> { ["preset"] = "Work" } });
        Assert.Equal(ActionStatus.Ok, result.Status);
        rig.Advance(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(500));
        Assert.Equal(intervened ? 0 : 1, moves.Count);
        if (intervened)
        {
            intervened = false;
            rig.Advance(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(500));
            Assert.Empty(moves);
            return;
        }
        if (!intervened)
        {
            var undo = rig.Act(new ActionRequest { Action = "preset.undo", Origin = Origin.Flyout });
            Assert.Equal(ActionStatus.Ok, undo.Status);
            Assert.Equal(window.WindowPx, moves[1].Rect);
        }
    }
    [Fact]
    public void UndoExpiresAfterThirtyMinutes()
    {
        using var rig = new FeatureRig();
        var monitor = new MonitorInfo("display", new RectPx(0, 0, 1000, 800), new RectPx(0, 0, 1000, 800), 96, true);
        var window = new WindowInfo { Hwnd = 1, Pid = 7, AppId = "code.exe", FramePx = new RectPx(0, 0, 200, 200), WindowPx = new RectPx(0, 0, 200, 200) };
        var preset = new Preset { Name = "Work", Slots = [new PresetSlot { App = new PresetApp { AppId = "code.exe" }, Place = new PresetPlace { Monitor = "display", Rect = new DipRect { W = 300, H = 300 } } }] };
        new PresetStore(rig.Context).Save(new PresetsFile { Presets = [preset] });
        rig.Add(new PresetRestorer(rig.Context) { Windows = () => [window], UserChangedSince = (_, _) => false, Monitors = () => [monitor], Move = _ => true });
        rig.Pump();
        var arranged = rig.Act(new ActionRequest { Action = "preset.arrange", Origin = Origin.Flyout, Target = new Dictionary<string, string> { ["preset"] = "Work" } });
        Assert.Equal(ActionStatus.Ok, arranged.Status);
        rig.Advance(TimeSpan.FromMinutes(31));
        var undo = rig.Act(new ActionRequest { Action = "preset.undo", Origin = Origin.Flyout });
        Assert.Equal(ActionStatus.Refused, undo.Status);
    }
    [Fact]
    public void LaunchWindowIsNotMovedAfterDeadline()
    {
        using var rig = new FeatureRig();
        var monitor = new MonitorInfo("display", new RectPx(0, 0, 1000, 800), new RectPx(0, 0, 1000, 800), 96, true);
        var window = new WindowInfo { Hwnd = 12, Pid = 7, AppId = "code.exe", FramePx = new RectPx(0, 0, 200, 200), WindowPx = new RectPx(0, 0, 200, 200) };
        var preset = new Preset { Name = "Work", Slots = [new PresetSlot { App = new PresetApp { AppId = "code.exe" }, Place = new PresetPlace { Monitor = "display", Rect = new DipRect { W = 300, H = 300 } }, Launch = new PresetLaunch { Exe = "code.exe" } }] };
        new PresetStore(rig.Context).Save(new PresetsFile { Presets = [preset] });
        var opened = false;
        var moves = 0;
        rig.Add(new PresetRestorer(rig.Context) { Windows = () => opened ? [window] : [], UserChangedSince = (_, _) => false, Monitors = () => [monitor], Move = _ => { moves++; return true; }, Launch = _ => true });
        rig.Pump();
        var result = rig.Act(new ActionRequest { Action = "preset.apply", Origin = Origin.Flyout, Target = new Dictionary<string, string> { ["preset"] = "Work" } });
        Assert.Equal(ActionStatus.Ok, result.Status);
        rig.Advance(TimeSpan.FromSeconds(21), TimeSpan.FromMilliseconds(500));
        opened = true;
        rig.Advance(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(500));
        Assert.Equal(0, moves);
    }
    [Fact]
    public void RuleArrangeDoesNotApplyNonUndoableFancyZonesLayout()
    {
        using var rig = new FeatureRig();
        var monitor = new MonitorInfo("display", new RectPx(0, 0, 1000, 800), new RectPx(0, 0, 1000, 800), 96, true);
        var preset = new Preset { Name = "Work", Slots = [], Fancyzones = [new FancyZonesLayout { Monitor = "display", Layout = "columns" }] };
        new PresetStore(rig.Context).Save(new PresetsFile { Presets = [preset] });
        var calls = 0;
        rig.Add(new PresetRestorer(rig.Context) { Monitors = () => [monitor], ApplyLayout = _ => { calls++; return null; } });
        rig.Pump();
        var result = rig.Act(new ActionRequest { Action = "preset.arrange", Origin = Origin.Rule, Rule = "test", Target = new Dictionary<string, string> { ["preset"] = "Work" } });
        Assert.Equal(ActionStatus.Ok, result.Status);
        Assert.Equal(0, calls);
        Assert.Contains(result.Items!, item => item.Reason == "layout unchanged by arrange");
        result = rig.Act(new ActionRequest { Action = "preset.apply", Origin = Origin.Flyout, Target = new Dictionary<string, string> { ["preset"] = "Work" } });
        Assert.Equal(ActionStatus.Ok, result.Status);
        Assert.Equal(1, calls);
    }
    [Fact]
    public void RuleUndoUsesItsOwnPresetSnapshot()
    {
        using var rig = new FeatureRig();
        var monitor = new MonitorInfo("display", new RectPx(0, 0, 1000, 800), new RectPx(0, 0, 1000, 800), 96, true);
        var windows = new[] {
            new WindowInfo { Hwnd = 1, Pid = 1, AppId = "code.exe", FramePx = new RectPx(0, 0, 200, 200), WindowPx = new RectPx(0, 0, 200, 200) },
            new WindowInfo { Hwnd = 2, Pid = 2, AppId = "chrome.exe", FramePx = new RectPx(400, 0, 200, 200), WindowPx = new RectPx(400, 0, 200, 200) },
        };
        Preset Make(string name, string app) => new() { Name = name, Slots = [new PresetSlot { App = new PresetApp { AppId = app }, Place = new PresetPlace { Monitor = "display", Rect = new DipRect { W = 300, H = 300 } } }] };
        new PresetStore(rig.Context).Save(new PresetsFile { Presets = [Make("A", "code.exe"), Make("B", "chrome.exe")] });
        var moves = new List<WindowMove>();
        rig.Add(new PresetRestorer(rig.Context) { Windows = () => windows, UserChangedSince = (_, _) => false, Monitors = () => [monitor], Move = move => { moves.Add(move); return true; } });
        rig.Pump();
        ActionRequest Request(string action, string rule, string preset) => new() { Action = action, Origin = Origin.Rule, Rule = rule, Target = new Dictionary<string, string> { ["preset"] = preset } };
        Assert.Equal(ActionStatus.Ok, rig.Act(Request("preset.arrange", "rule-a", "A")).Status);
        Assert.Equal(ActionStatus.Ok, rig.Act(Request("preset.arrange", "rule-b", "B")).Status);
        Assert.Equal(ActionStatus.Ok, rig.Act(Request("preset.undo", "rule-a", "A")).Status);
        Assert.Equal(1, moves[2].Hwnd);
        Assert.Equal(ActionStatus.Ok, rig.Act(Request("preset.undo", "rule-b", "B")).Status);
        Assert.Equal(2, moves[3].Hwnd);
    }
    [Fact]
    public void PresetsPullListsCountsAndOffersUndoOnlyAfterAnArrange()
    {
        using var rig = new FeatureRig();
        var window = new WindowInfo { Hwnd = 1, Pid = 7, AppId = "code.exe", FramePx = new RectPx(0, 0, 200, 200), WindowPx = new RectPx(0, 0, 200, 200) };
        var monitor = new MonitorInfo("display", new RectPx(0, 0, 1000, 800), new RectPx(0, 0, 1000, 800), 96, true);
        PresetPlace Place() => new() { Monitor = "display", Rect = new DipRect { W = 300, H = 300 } };
        var preset = new Preset
        {
            Name = "Work",
            Hotkey = "Ctrl+Alt+1",
            Slots = [new PresetSlot { App = new PresetApp { AppId = "code.exe" }, Place = Place() }, new PresetSlot { App = new PresetApp { AppId = "other.exe" }, Place = Place(), Launch = new PresetLaunch { Exe = "private-tool.exe", Args = ["--private"] } }],
        };
        new PresetStore(rig.Context).Save(new PresetsFile { Presets = [preset] });
        rig.Add(new PresetRestorer(rig.Context) { Windows = () => [window], UserChangedSince = (_, _) => false, Monitors = () => [monitor], Move = _ => true });
        rig.Pump();
        var pull = new Contracts.Pipe.PullMessage { Table = "presets" };
        var first = Assert.Single(rig.Pulls["presets"](pull)!["presets"]!.AsArray())!;
        Assert.Equal(("Work", 2, 1, "Ctrl+Alt+1", false), ((string)first["name"]!, (int)first["slots"]!, (int)first["launches"]!, (string)first["hotkey"]!, (bool)first["can_undo"]!));
        rig.Act(new ActionRequest { Action = "preset.arrange", Origin = Origin.Flyout, Target = new Dictionary<string, string> { ["preset"] = "Work" } });
        var after = rig.Pulls["presets"](pull)!;
        Assert.True((bool)after["presets"]![0]!["can_undo"]!);
        Assert.DoesNotContain("private", after.ToJsonString(), StringComparison.Ordinal);
    }
}
