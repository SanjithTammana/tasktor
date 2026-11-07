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

    [Fact]
    public async Task A_pack_is_listed_and_turned_on_with_one_consent()
    {
        using var store = new TempStore();
        var pack = Path.Combine(store.Layout.PluginsDir, "my-pack");
        Directory.CreateDirectory(pack);
        File.WriteAllText(Path.Combine(pack, "pack.json"), """{ "contract": 1, "id": "my-pack", "name": "My pack", "author": "Example", "version": "1.0", "plugins": ["a", "b"] }""");
        foreach (var id in new[] { "a", "b" })
        {
            Directory.CreateDirectory(Path.Combine(pack, id));
            File.WriteAllText(Path.Combine(pack, id, "main.py"), "print('{}')");
            File.WriteAllText(Path.Combine(pack, id, "plugin.json"), $$"""
                { "contract": 1, "id": "{{id}}", "name": "Plugin {{id}}", "version": "0.1.0", "command": ["{python}", "main.py"],
                  "interval": "5m", "timeout": "10s", "reads": ["nothing"], "network": [], "settings": [], "actions": [] }
                """);
        }

        await using var host = new AgentHost(new AgentOptions { Store = store.Layout, PipeName = "tasktor-test-" + Guid.NewGuid().ToString("N")[..12] });
        host.Start();
        var report = await host.Loop.InvokeAsync(host.PluginsReport);
        Assert.Equal(("my-pack", "My pack", "Example"), (report.Packs.Single().Id, report.Packs.Single().Name, report.Packs.Single().Author));
        Assert.All(report.Plugins.Where(p => p.Id is "a" or "b"), p => Assert.Equal(("my-pack", PluginState.Found), (p.Pack, p.State)));

        var on = await (await host.Loop.InvokeAsync(() => host.Router.Submit(new ActionRequest { Action = "plugin.enable_pack", Origin = Origin.Flyout, Target = new Dictionary<string, string> { ["pack"] = "my-pack" } }))).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((ActionStatus.Ok, "Turned on My pack"), (on.Status, on.Message));
        Assert.All((await host.Loop.InvokeAsync(host.PluginsReport)).Plugins.Where(p => p.Id is "a" or "b"), p => Assert.NotEqual(PluginState.Found, p.State));
        var none = await (await host.Loop.InvokeAsync(() => host.Router.Submit(new ActionRequest { Action = "plugin.enable_pack", Origin = Origin.Flyout, Target = new Dictionary<string, string> { ["pack"] = "nope" } }))).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ActionStatus.Refused, none.Status);
    }

    [Fact]
    public async Task A_packs_rules_start_unticked_and_ticking_one_loads_it()
    {
        using var store = new TempStore();
        var pack = Path.Combine(store.Layout.PluginsDir, "my-pack");
        Directory.CreateDirectory(Path.Combine(pack, "a"));
        File.WriteAllText(Path.Combine(pack, "pack.json"), """{ "contract": 1, "id": "my-pack", "name": "My pack", "version": "1.0", "plugins": ["a"], "rules": "rules.pack.json" }""");
        File.WriteAllText(Path.Combine(pack, "rules.pack.json"), """{ "rules": [ { "id": "pack-quiet", "description": "Quiet on battery", "when": { "state": "on_battery" }, "then": [ { "pause": "refits" } ] } ] }""");
        File.WriteAllText(Path.Combine(pack, "a", "main.py"), "print('{}')");
        File.WriteAllText(Path.Combine(pack, "a", "plugin.json"), """{ "contract": 1, "id": "a", "name": "A", "version": "0.1.0", "command": ["{python}", "main.py"], "interval": "5m", "timeout": "10s", "reads": ["nothing"], "network": [], "settings": [], "actions": [] }""");
        await using var host = new AgentHost(new AgentOptions { Store = store.Layout, PipeName = "tasktor-test-" + Guid.NewGuid().ToString("N")[..12] });
        host.Start();
        async Task<ActionResult> Act(string action, Dictionary<string, string>? target = null, Dictionary<string, string>? args = null) =>
            await (await host.Loop.InvokeAsync(() => host.Router.Submit(new ActionRequest { Action = action, Origin = Origin.Flyout, Target = target, Args = args }))).WaitAsync(TimeSpan.FromSeconds(10));
        Dictionary<string, string> Tick(bool on) => new() { ["pack"] = "my-pack", ["rule"] = "pack-quiet", ["on"] = on ? "true" : "false" };

        Assert.Empty((await host.Loop.InvokeAsync(host.RulesReport)).PackRules); // a pack that is off offers no rules
        Assert.Equal(ActionStatus.Refused, (await Act("pack.rule", args: Tick(true))).Status);
        await Act("plugin.enable_pack", target: new() { ["pack"] = "my-pack" });

        var offered = (await host.Loop.InvokeAsync(host.RulesReport)).PackRules.Single();
        Assert.Equal(("my-pack", "My pack", "pack-quiet", "Quiet on battery", false), (offered.Pack, offered.PackName, offered.Id, offered.Description, offered.Ticked));
        Assert.DoesNotContain((await host.Loop.InvokeAsync(host.RulesReport)).Rules, r => r.Id == "pack-quiet");

        Assert.Equal(ActionStatus.Ok, (await Act("pack.rule", args: Tick(true))).Status);
        var report = await host.Loop.InvokeAsync(host.RulesReport);
        Assert.True(report.PackRules.Single().Ticked);
        Assert.Equal("pack", report.Rules.Single(r => r.Id == "pack-quiet").Source); // loaded

        Assert.Equal(ActionStatus.Ok, (await Act("pack.rule", args: Tick(false))).Status);
        Assert.DoesNotContain((await host.Loop.InvokeAsync(host.RulesReport)).Rules, r => r.Id == "pack-quiet");
        Assert.Equal(ActionStatus.Refused, (await Act("pack.rule", args: new() { ["pack"] = "my-pack", ["rule"] = "nope", ["on"] = "true" })).Status);
    }
}
