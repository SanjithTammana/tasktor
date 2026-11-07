using Tasktor.Agent.Host;
using Tasktor.Contracts.Actions;
using Tasktor.Tests.Shared;

namespace Tasktor.Agent.Tests;

/// <summary>U4 D-G3 Settings → Rules: the list, its switches (written into rules.json as toggles) and reload.</summary>
public sealed class RulesSettingsTests
{
    [Fact]
    public async Task Switching_a_rule_writes_a_toggle_keeps_the_users_comments_and_reloads()
    {
        using var store = new TempStore();
        File.WriteAllText(store.Layout.Rules, """
            // mine
            { "rules": [ { "id": "mine", "description": "Busy", "when": { "metric": "system.cpu_pct", "above": 95 }, "then": [ { "pause": "refits" } ] } ] }
            """);
        await using var host = new AgentHost(new AgentOptions { Store = store.Layout, PipeName = "tasktor-test-" + Guid.NewGuid().ToString("N")[..12] });
        host.Start();
        async Task<ActionResult> Act(string action, string rule, Origin origin = Origin.Flyout) =>
            await (await host.Loop.InvokeAsync(() => host.Router.Submit(new ActionRequest { Action = action, Origin = origin, Target = new Dictionary<string, string> { ["rule"] = rule } }))).WaitAsync(TimeSpan.FromSeconds(10));
        Task<Contracts.Rules.RulesReport> Report() => host.Loop.InvokeAsync(host.RulesReport);

        var mine = (await Report()).Rules.Single(r => r.Id == "mine");
        Assert.Equal(("Busy", "user", true), (mine.Description, mine.Source, mine.On));

        var off = await Act("rule.disable", "mine");
        Assert.Equal((ActionStatus.Ok, "Turned off mine"), (off.Status, off.Message));
        Assert.False((await Report()).Rules.Single(r => r.Id == "mine").On); // reloaded at once
        var text = File.ReadAllText(store.Layout.Rules);
        Assert.StartsWith("// mine", text, StringComparison.Ordinal);
        Assert.Contains("\"enabled\": false", text, StringComparison.Ordinal);

        Assert.Equal(ActionStatus.Ok, (await Act("rule.enable", "mine")).Status);
        Assert.True((await Report()).Rules.Single(r => r.Id == "mine").On);
        Assert.Equal(ActionStatus.Refused, (await Act("rule.enable", "no-such")).Status);
        Assert.Equal(ActionStatus.Refused, (await Act("rule.disable", "mine", Origin.Rule)).Status); // people only

        File.WriteAllText(store.Layout.Rules, "{ broken");
        var refused = await Act("rule.disable", "mine");
        Assert.Equal(ActionStatus.Failed, refused.Status); // never overwrite a file we cannot read
        Assert.Equal("{ broken", File.ReadAllText(store.Layout.Rules));
    }

    [Fact]
    public async Task Edit_opens_rules_json_creating_a_starter_once_and_never_touching_an_existing_one()
    {
        using var store = new TempStore();
        var launched = new List<string>();
        await using var host = new AgentHost(new AgentOptions
        {
            Store = store.Layout,
            PipeName = "tasktor-test-" + Guid.NewGuid().ToString("N")[..12],
            Launch = t =>
            {
                lock (launched)
                {
                    launched.Add(t);
                }

                return true;
            },
        });
        host.Start();
        async Task<ActionResult> Edit() =>
            await (await host.Loop.InvokeAsync(() => host.Router.Submit(new ActionRequest { Action = "rules.edit", Origin = Origin.Flyout }))).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ActionStatus.Ok, (await Edit()).Status);
        var starter = File.ReadAllText(store.Layout.Rules);
        Assert.Contains("rules.default.json", starter, StringComparison.Ordinal); // says where the shipped rules are
        Assert.Empty(Core.Rules.RulesLoader.Load(null, starter).Errors); // and loads clean

        File.WriteAllText(store.Layout.Rules, "// mine\n{ \"rules\": [] }");
        Assert.Equal(ActionStatus.Ok, (await Edit()).Status);
        Assert.Equal("// mine\n{ \"rules\": [] }", File.ReadAllText(store.Layout.Rules));
        lock (launched)
        {
            Assert.Equal(["edit:" + store.Layout.Rules, "edit:" + store.Layout.Rules], launched);
        }
    }

    [Fact]
    public async Task Saving_rules_json_reloads_the_rules_while_the_agent_runs()
    {
        using var store = new TempStore();
        await using var host = new AgentHost(new AgentOptions { Store = store.Layout, PipeName = "tasktor-test-" + Guid.NewGuid().ToString("N")[..12] });
        host.Start();
        await host.Loop.InvokeAsync(() => true); // started, first load done
        File.WriteAllText(store.Layout.Rules, """{ "rules": [ { "id": "late", "when": { "metric": "system.cpu_pct", "above": 95 }, "then": [ { "pause": "refits" } ] } ] }""");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var seen = false;
        while (!seen && DateTime.UtcNow < deadline)
        {
            seen = await host.Loop.InvokeAsync(() => host.RulesReport().Rules.Any(r => r.Id == "late"));
            await Task.Delay(100);
        }

        Assert.True(seen, "rules.json was saved but the rules did not reload (U1: hot reload)");
    }
}
