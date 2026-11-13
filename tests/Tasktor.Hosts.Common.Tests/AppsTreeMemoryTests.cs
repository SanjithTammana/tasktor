using System.Text.Json.Nodes;
using Tasktor.Hosts.Common.Deep;

namespace Tasktor.Hosts.Common.Tests;

public sealed class AppsTreeMemoryTests
{
    [Fact]
    public void Memory_order_uses_numeric_totals_keeps_unknown_last_and_preserves_actions()
    {
        var source = JsonNode.Parse("""
            [{"app_id":"small","name":"Small","private_mb":900,"processes":[]},
             {"app_id":"unknown","name":"Unknown","processes":[]},
             {"app_id":"large","name":"Large","private_mb":1200,"processes":[]},
             {"app_id":"zero","name":"Zero","private_mb":0,"system":true,"processes":[]}]
            """);
        var original = source!.ToJsonString();
        var apps = AppsTree.From(source, byMemory: true);
        Assert.Equal(["large", "small", "zero", "unknown"], apps.Select(a => a.AppId));
        Assert.Equal("large", apps[0].Actions[0].Target!["app"]);
        Assert.True(apps[0].Actions.Single(a => a.Action == "app.end").Destructive);
        Assert.Empty(apps[2].Actions);
        Assert.Equal(original, source.ToJsonString());
        Assert.Equal(["small", "unknown", "large", "zero"], AppsTree.From(source).Select(a => a.AppId));
    }
}
