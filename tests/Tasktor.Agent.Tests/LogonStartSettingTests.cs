using Tasktor.Agent.Host;
using Tasktor.Contracts.Actions;
using Tasktor.Core.Platform;
using Tasktor.Tests.Shared;

namespace Tasktor.Agent.Tests;

/// <summary>H8 portable build: start with Windows through the HKCU Run value, from the setting, the switch and uninstall.</summary>
public sealed class LogonStartSettingTests
{
    private const string Command = "\"C:\\Apps\\Tasktor\\tasktor-core.exe\"";
    private string? _value;

    private AgentHost Host(TempStore store, bool logon = true) => new(new AgentOptions
    {
        Store = store.Layout,
        PipeName = "tasktor-test-" + Guid.NewGuid().ToString("N")[..12],
        LogonStart = logon ? new LogonStart(() => _value, v => _value = v, () => _value = null) : null,
        LogonCommand = logon ? Command : null,
    });

    private static async Task<ActionResult> Act(AgentHost host, string action, Dictionary<string, string>? args = null, Origin origin = Origin.Flyout, string? confirm = null) =>
        await (await host.Loop.InvokeAsync(() => host.Router.Submit(new ActionRequest { Action = action, Origin = origin, Args = args, Confirm = confirm }))).WaitAsync(TimeSpan.FromSeconds(10));

    [Fact]
    public async Task The_switch_writes_the_entry_and_saves_the_setting_and_off_removes_it()
    {
        using var store = new TempStore();
        await using var host = Host(store);
        host.Start();
        var on = await Act(host, "settings.start_with_windows", new() { ["on"] = "true" }, Origin.Cli);
        Assert.Equal((ActionStatus.Ok, "Tasktor starts when you sign in to Windows"), (on.Status, on.Message));
        Assert.Equal(Command, _value);
        Assert.True(await host.Loop.InvokeAsync(() => host.Settings.Current.StartWithWindows == true));
        var off = await Act(host, "settings.start_with_windows", new() { ["on"] = "false" });
        Assert.Equal(ActionStatus.Ok, off.Status);
        Assert.Null(_value);
        Assert.False(await host.Loop.InvokeAsync(() => host.Settings.Current.StartWithWindows == true));
        Assert.Equal(ActionStatus.Refused, (await Act(host, "settings.start_with_windows", new() { ["on"] = "maybe" })).Status);
    }

    [Fact]
    public async Task At_start_the_entry_follows_the_saved_setting_and_a_moved_folder_is_rewritten()
    {
        using var store = new TempStore();
        File.WriteAllText(store.Layout.Config, """{"schema_version":1,"start_with_windows":true}""");
        _value = "\"C:\\Old place\\tasktor-core.exe\"";
        await using var host = Host(store);
        host.Start();
        await host.Loop.InvokeAsync(() => true);
        Assert.Equal(Command, _value);
    }

    [Fact]
    public async Task Without_the_setting_nothing_is_written()
    {
        using var store = new TempStore();
        await using var host = Host(store);
        host.Start();
        await host.Loop.InvokeAsync(() => true);
        Assert.Null(_value);
    }

    [Fact]
    public async Task A_check_instance_refuses_and_touches_nothing()
    {
        using var store = new TempStore();
        File.WriteAllText(store.Layout.Config, """{"schema_version":1,"start_with_windows":true}""");
        await using var host = Host(store, logon: false);
        host.Start();
        var r = await Act(host, "settings.start_with_windows", new() { ["on"] = "true" });
        Assert.Equal((ActionStatus.Refused, "Start with Windows is not available in this instance"), (r.Status, r.Message));
        Assert.Null(_value);
    }

    [Fact]
    public async Task Uninstall_removes_the_entry_and_turns_the_setting_off()
    {
        using var store = new TempStore();
        File.WriteAllText(store.Layout.Config, """{"schema_version":1,"start_with_windows":true,"cli":{"allow_destructive":true}}""");
        await using var host = Host(store);
        host.Start();
        await host.Loop.InvokeAsync(() => true);
        Assert.Equal(Command, _value);
        var r = await Act(host, "uninstall", origin: Origin.Cli, confirm: Core.Actions.OriginPolicy.CliYes);
        Assert.Contains("Removed the start-with-Windows entry", r.Message, StringComparison.Ordinal);
        Assert.Null(_value);
        Assert.False(await host.Loop.InvokeAsync(() => host.Settings.Current.StartWithWindows == true));
    }
}
