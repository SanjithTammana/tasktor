using Tasktor.Agent.Host;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Snapshot;
using Tasktor.Tests.Shared;

namespace Tasktor.Agent.Tests;

/// <summary>U4 D-G3 Settings → Plugins: each folder with what its consent dialog must show (U1 D-H7), turned on and off.</summary>
public sealed class PluginsSettingsTests
{
    [Fact]
    public async Task A_found_plugin_lists_its_consent_facts_and_turns_on_and_off()
    {
        using var store = new TempStore();
        var folder = Path.Combine(store.Layout.PluginsDir, "todo");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "main.py"), "print('{}')");
        File.WriteAllText(Path.Combine(folder, "plugin.json"), """
            { "contract": 1, "id": "todo", "name": "To-do count", "author": "Example", "version": "0.1.0", "command": ["{python}", "main.py"],
              "interval": "5m", "timeout": "10s", "reads": ["A file you choose"], "network": ["api.example.com"], "settings": [],
              "actions": [ { "id": "refresh", "label": "Refresh", "class": "reversible" } ] }
            """);
        await using var host = new AgentHost(new AgentOptions { Store = store.Layout, PipeName = "tasktor-test-" + Guid.NewGuid().ToString("N")[..12] });
        host.Start();
        async Task<ActionResult> Act(string action) =>
            await (await host.Loop.InvokeAsync(() => host.Router.Submit(new ActionRequest { Action = action, Origin = Origin.Flyout, Target = new Dictionary<string, string> { ["plugin"] = "todo" } }))).WaitAsync(TimeSpan.FromSeconds(10));

        var p = (await host.Loop.InvokeAsync(host.PluginsReport)).Plugins.Single(x => x.Id == "todo");
        Assert.Equal(("To-do count", "Example", PluginState.Found), (p.Name, p.Author, p.State));
        Assert.Equal(["A file you choose"], p.Reads);
        Assert.Equal(["api.example.com"], p.Network);
        Assert.Equal(["Refresh"], p.Actions);
        Assert.Contains("main.py", p.CommandLine, StringComparison.Ordinal);

        Assert.Equal(ActionStatus.Ok, (await Act("plugin.enable")).Status);
        Assert.NotEqual(PluginState.Found, (await host.Loop.InvokeAsync(host.PluginsReport)).Plugins.Single(x => x.Id == "todo").State);
        Assert.Equal(ActionStatus.Ok, (await Act("plugin.disable")).Status);
        Assert.Equal(PluginState.Found, (await host.Loop.InvokeAsync(host.PluginsReport)).Plugins.Single(x => x.Id == "todo").State); // consent dropped: on again asks again
    }
}
