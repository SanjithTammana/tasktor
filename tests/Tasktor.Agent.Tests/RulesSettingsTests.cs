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
}
