using Tasktor.Agent.Host;
using Tasktor.Contracts.Actions;
using Tasktor.Core.Storage;
using Tasktor.Core.Windows;
using Tasktor.Tests.Shared;

namespace Tasktor.Agent.Tests;

/// <summary>U4 OQ-G1: the command line's hotkey is configurable; a taken combination keeps the old one and says so.</summary>
public sealed class HotkeySettingTests
{
    [Fact]
    public async Task Changing_the_hotkey_registers_it_live_saves_it_and_keeps_the_old_one_when_taken()
    {
        using var store = new TempStore();
        await using var host = new AgentHost(new AgentOptions { Store = store.Layout, PipeName = "tasktor-test-" + Guid.NewGuid().ToString("N")[..12] });
        var registered = new List<string>();
        var taken = "Ctrl+Alt+K";
        host.ApplyHotkey = h =>
        {
            lock (registered)
            {
                registered.Add(h.ToString());
            }

            return h.ToString() != taken;
        };
        host.Start();
        async Task<ActionResult> Set(string keys, Origin origin = Origin.Flyout) =>
            await (await host.Loop.InvokeAsync(() => host.Router.Submit(new ActionRequest { Action = "settings.hotkey", Origin = origin, Args = new Dictionary<string, string> { ["keys"] = keys } }))).WaitAsync(TimeSpan.FromSeconds(10));

        var hotkey = await host.Loop.InvokeAsync(() => host.State.Current.Hotkey);
        Assert.Equal(("Win+Alt+T", true), (hotkey!.Keys, hotkey.Registered)); // the default, registered at start

        var ok = await Set("ctrl + shift + space");
        Assert.Equal((ActionStatus.Ok, "Command line: Ctrl+Shift+Space"), (ok.Status, ok.Message));
        Assert.Equal("Ctrl+Shift+Space", host.Settings.Current.Flyout!.Hotkey);
        Assert.Equal("Ctrl+Shift+Space", (await host.Loop.InvokeAsync(() => host.State.Current.Hotkey))!.Keys);

        var busy = await Set(taken);
        Assert.Equal(ActionStatus.Failed, busy.Status);
        Assert.Equal("Ctrl+Alt+K is used by another program; kept Ctrl+Shift+Space", busy.Message);
        Assert.Equal("Ctrl+Shift+Space", host.Settings.Current.Flyout!.Hotkey);
        lock (registered)
        {
            Assert.Equal(["Win+Alt+T", "Ctrl+Shift+Space", "Ctrl+Alt+K", "Ctrl+Shift+Space"], registered); // the old one back
        }

        var bad = await Set("Shift+T");
        Assert.Equal((ActionStatus.Refused, "Add Ctrl, Alt or Win"), (bad.Status, bad.Message));
        Assert.Equal(ActionStatus.Refused, (await Set("Win+Alt+J", Origin.Rule)).Status); // people only
        await host.FlushWritesAsync();
        Assert.Equal("Ctrl+Shift+Space", new SettingsStore(store.Layout).Load().Flyout!.Hotkey);
    }
}
