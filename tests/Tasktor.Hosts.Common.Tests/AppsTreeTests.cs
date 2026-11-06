using System.Text.Json.Nodes;
using Tasktor.Hosts.Common.Deep;

namespace Tasktor.Hosts.Common.Tests;

/// <summary>U4 D-G3 System: apps → processes, with actions only where they can work.</summary>
public sealed class AppsTreeTests
{
    private static readonly JsonNode Pulled = JsonNode.Parse("""
        [ { "app_id": "chrome", "name": "Chrome", "cpu_pct": 22.5, "private_mb": 5800, "count": 3, "system": false,
            "processes": [ { "pid": 100, "created": "638000", "exe": "chrome.exe", "cpu_pct": 12.0, "private_mb": 900 },
                           { "pid": 101, "created": "638001", "exe": "chrome.exe", "cpu_pct": 0.4, "private_mb": 120, "locked": "Runs as administrator" } ] },
          { "app_id": "windows", "name": "Windows", "cpu_pct": 2.0, "private_mb": 400, "count": 1, "system": true,
            "processes": [ { "pid": 4, "exe": "System", "cpu_pct": 1.0, "private_mb": 1 } ] } ]
        """)!;

    [Fact]
    public void Apps_carry_totals_and_group_actions()
    {
        var apps = AppsTree.From(Pulled);
        var chrome = apps[0];
        Assert.Equal(("Chrome", "22%", "5.7 GB", 3), (chrome.Name, chrome.Cpu, chrome.Memory, chrome.Count));
        Assert.Equal(["priority.set", "efficiency.set", "app.end"], chrome.Actions.Select(a => a.Action!));
        Assert.Equal("chrome", chrome.Actions[0].Target!["app"]);
        Assert.True(chrome.Actions[2].Destructive);
    }

    [Fact]
    public void Processes_target_pid_and_creation_time_and_locked_ones_say_why()
    {
        var chrome = AppsTree.From(Pulled)[0];
        var first = chrome.Processes[0];
        Assert.Equal(("100", "638000"), (first.Actions[0].Target!["pid"], first.Actions[0].Target!["created"]));
        Assert.Equal("proc.end", first.Actions[1].Action);
        var locked = chrome.Processes[1];
        Assert.Equal("Runs as administrator", locked.Locked);
        Assert.Empty(locked.Actions);
    }

    [Fact]
    public void System_groups_offer_nothing()
    {
        var windows = AppsTree.From(Pulled)[1];
        Assert.Empty(windows.Actions);
        Assert.Equal("Part of Windows", windows.Processes[0].Locked);
        Assert.Empty(windows.Processes[0].Actions);
        Assert.Empty(AppsTree.From(null));
    }
}
