using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Presets;
using Tasktor.Core.Features.Windows;
using Tasktor.Core.Windows;
using Tasktor.Tests.Shared;

namespace Tasktor.Core.Tests;

/// <summary>U3 I5 / U4: save the open windows as a preset, with the launch shown before saving.</summary>
public sealed class PresetSaveTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\App\\app.exe\" --open \"My File.txt\" -x", new[] { "--open", "My File.txt", "-x" })]
    [InlineData("app.exe", new string[0])]
    [InlineData("tool.exe \"a\\\"b\" c\\\\d \"e f\\\\\"", new[] { "a\"b", "c\\\\d", "e f\\" })]
    public void Command_lines_split_like_windows_does(string line, string[] args) =>
        Assert.Equal(args, CommandLineArgs.Split(line).Skip(1));

    [Fact]
    public void Open_windows_are_listed_and_a_preset_is_saved_from_the_ticked_ones_with_their_launch()
    {
        using var rig = new FeatureRig();
        var monitor = new MonitorInfo("display", new RectPx(0, 0, 1000, 800), new RectPx(0, 0, 1000, 800), 96, true);
        WindowInfo Win(long h, uint pid, string app) => new() { Hwnd = h, Pid = pid, AppId = app, Exe = "C:\\Apps\\" + app, Title = Sensitive.Of(app + " title"), FramePx = new RectPx(0, 0, 400, 300), WindowPx = new RectPx(0, 0, 400, 300) };
        var feature = rig.Add(new PresetRestorer(rig.Context)
        {
            Windows = () => [Win(1, 10, "code.exe"), Win(2, 20, "chrome.exe")],
            Monitors = () => [monitor],
            CommandLine = pid => pid == 10 ? "\"C:\\Apps\\code.exe\" --reuse-window \"D:\\work\\thesis\"" : null,
        });
        rig.Pump();

        var report = feature.OpenWindows();
        var code = report.Windows.Single(w => w.App == "code.exe");
        Assert.Equal(("1", "code.exe title", 1, 400, 300), (code.Id, code.Title, code.Monitor, code.Width, code.Height));
        Assert.Equal("C:\\Apps\\code.exe", code.Launch!.Exe);
        Assert.Equal(["--reuse-window", "D:\\work\\thesis"], code.Launch.Args!);
        Assert.Null(report.Windows.Single(w => w.App == "chrome.exe").Launch); // no command line: nothing to launch

        ActionResult Save(Dictionary<string, string> args, Origin origin = Origin.Flyout) => rig.Act(new ActionRequest { Action = "preset.save", Origin = origin, Args = args });
        var ok = Save(new() { ["name"] = " Writing ", ["windows"] = "1,2", ["launch"] = "1" });
        Assert.Equal((ActionStatus.Ok, "Saved Writing: 2 windows, 1 opens if missing"), (ok.Status, ok.Message));
        var saved = feature.Store.Current.Presets.Single(p => p.Name == "Writing");
        Assert.Equal(["code.exe", "chrome.exe"], saved.Slots.Select(s => s.App.AppId));
        Assert.Equal(["--reuse-window", "D:\\work\\thesis"], saved.Slots[0].Launch!.Args!); // what the process ran, re-read at save
        Assert.Null(saved.Slots[1].Launch);

        Assert.Equal(ActionStatus.Refused, Save(new() { ["name"] = "x", ["windows"] = "" }).Status); // nothing ticked
        Assert.Equal(ActionStatus.Refused, Save(new() { ["name"] = "", ["windows"] = "1" }).Status);
        Assert.Equal(ActionStatus.Refused, Save(new() { ["name"] = "x", ["windows"] = "1" }, Origin.Rule).Status); // people only
    }

    [Fact]
    public void A_saved_preset_can_be_renamed_trimmed_and_deleted_by_a_person()
    {
        using var rig = new FeatureRig();
        PresetSlot Slot(string app, string? exe = null) => new() { App = new PresetApp { AppId = app }, Place = new PresetPlace { Monitor = "display", Rect = new DipRect { W = 300, H = 200 } }, Launch = exe is null ? null : new PresetLaunch { Exe = exe, Args = ["--x"] } };
        new PresetStore(rig.Context).Save(new PresetsFile { Presets = [new Preset { Name = "Work", Slots = [Slot("code.exe", "C:\\Apps\\code.exe"), Slot("chrome.exe")] }, new Preset { Name = "Home", Slots = [Slot("spotify.exe")] }] });
        var feature = rig.Add(new PresetRestorer(rig.Context));
        rig.Pump();
        ActionResult Act(string action, string preset, Dictionary<string, string>? args = null, string? confirm = null, Origin origin = Origin.Flyout) =>
            rig.Act(new ActionRequest { Action = action, Origin = origin, Target = new Dictionary<string, string> { ["preset"] = preset }, Args = args, Confirm = confirm });

        var detail = feature.PresetDetail("work")!;
        Assert.Equal(["code.exe", "chrome.exe"], detail.Slots.Select(x => x.App.AppId));
        Assert.Equal("C:\\Apps\\code.exe", detail.Slots[0].Launch!.Exe);

        Assert.Equal(ActionStatus.Ok, Act("preset.rename", "Work", new() { ["name"] = " Writing " }).Status);
        Assert.Equal(["Writing", "Home"], feature.Store.Current.Presets.Select(x => x.Name));
        Assert.Equal(ActionStatus.Refused, Act("preset.rename", "Writing", new() { ["name"] = "home" }).Status); // names stay unique
        Assert.Equal(ActionStatus.Ok, Act("preset.remove_slot", "Writing", new() { ["slot"] = "0" }).Status);
        Assert.Equal(["chrome.exe"], feature.Store.Current.Presets[0].Slots.Select(x => x.App.AppId));
        Assert.Equal(ActionStatus.Refused, Act("preset.remove_slot", "Writing", new() { ["slot"] = "0" }).Status); // never the last one: delete the preset instead

        var ask = Act("preset.delete", "Home");
        Assert.Equal(ActionStatus.NeedsConfirm, ask.Status); // two-step
        Assert.Equal(ActionStatus.Ok, Act("preset.delete", "Home", confirm: ask.Token).Status);
        Assert.Equal(["Writing"], feature.Store.Current.Presets.Select(x => x.Name));
        Assert.Equal(ActionStatus.Refused, Act("preset.rename", "Writing", new() { ["name"] = "x" }, origin: Origin.Rule).Status); // people only
    }
}

