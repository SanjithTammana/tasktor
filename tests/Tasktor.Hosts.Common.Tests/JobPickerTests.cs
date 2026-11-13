using System.Text.Json.Nodes;
using Tasktor.Hosts.Common.Deep;

namespace Tasktor.Hosts.Common.Tests;

/// <summary>B1 Jobs → "Watch a program as a job / Choose…": the processes a person can pin, and the job.pin each one sends.</summary>
public sealed class JobPickerTests
{
    private static readonly JsonNode Pulled = JsonNode.Parse("""
        [ { "app_id": "python", "name": "Python", "cpu_pct": 80.0, "private_mb": 2048, "count": 1, "system": false,
            "processes": [ { "pid": 300, "created": "639000", "exe": "python.exe", "cpu_pct": 80.0, "private_mb": 2048 } ] },
          { "app_id": "chrome", "name": "Chrome", "cpu_pct": 22.5, "private_mb": 5800, "count": 3, "system": false,
            "processes": [ { "pid": 100, "created": "638000", "exe": "chrome.exe", "cpu_pct": 12.0, "private_mb": 900 },
                           { "pid": 101, "created": "638001", "exe": "chrome.exe", "cpu_pct": 0.4, "private_mb": 120, "locked": "Runs as administrator" } ] },
          { "app_id": "tasktor", "name": "Tasktor", "cpu_pct": 1.0, "private_mb": 80, "count": 1, "system": false,
            "processes": [ { "pid": 200, "exe": "tasktor-core.exe", "cpu_pct": 1.0, "private_mb": 80, "locked": "Tasktor itself" } ] },
          { "app_id": "windows", "name": "Windows", "cpu_pct": 2.0, "private_mb": 400, "count": 1, "system": true,
            "processes": [ { "pid": 4, "exe": "System", "cpu_pct": 1.0, "private_mb": 1 } ] } ]
        """)!;

    [Fact]
    public void Offers_only_processes_that_can_be_watched_busiest_app_first()
    {
        var choices = JobPicker.From(Pulled);
        Assert.Equal([300u, 100u], choices.Select(c => c.Pid));
        Assert.Equal(("python.exe", "Python"), (choices[0].Exe, choices[0].App));
    }

    [Fact]
    public void Each_choice_pins_its_process_by_pid_and_creation_time()
    {
        var watch = JobPicker.From(Pulled)[1].Watch;
        Assert.Equal("job.pin", watch.Action);
        Assert.Equal(("100", "638000"), (watch.Target!["pid"], watch.Target!["created"]));
        Assert.Equal("Watch chrome.exe as a job", watch.Label);
        Assert.NotEqual(true, watch.Destructive);
    }

    [Fact]
    public void Detail_names_the_app_and_its_current_use()
    {
        Assert.Equal("Chrome · 12% CPU · 900 MB", JobPicker.From(Pulled)[1].Detail);
    }

    [Theory]
    [InlineData(null, 2)]
    [InlineData("  ", 2)]
    [InlineData("PYTH", 1)]
    [InlineData("chrome", 1)]
    [InlineData("300", 1)]
    [InlineData("blender", 0)]
    public void Filter_matches_program_app_or_pid_ignoring_case(string? text, int count)
    {
        Assert.Equal(count, JobPicker.Filter(JobPicker.From(Pulled), text).Count);
    }

    [Fact]
    public void Nothing_pulled_offers_nothing()
    {
        Assert.Empty(JobPicker.From(null));
        Assert.Empty(JobPicker.From(JsonNode.Parse("{}")));
    }
}
