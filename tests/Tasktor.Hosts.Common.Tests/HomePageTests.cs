using System.Text.Json.Nodes;
using Tasktor.Hosts.Common.Deep;

namespace Tasktor.Hosts.Common.Tests;

/// <summary>U4 D-G3 Home: undo the last 30 minutes' actions.</summary>
public sealed class HomePageTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Undoable_entries_become_rows_with_an_undo_on_the_same_target()
    {
        var recent = JsonNode.Parse("""
            [ { "at": "2026-11-06T10:25:00Z", "action": "priority.set", "text": "Lower Chrome", "origin": "shelf", "undo": "priority.restore", "target": { "app": "chrome" } },
              { "at": "2026-11-06T10:10:00Z", "action": "job.pause", "text": "Pause train.py", "origin": "rule", "undo": "job.resume", "target": { "job": "j1" } },
              { "at": "2026-11-06T10:05:00Z", "action": "priority.set", "text": "Lower Code", "origin": "flyout" } ]
            """);
        var undo = HomePage.Undo(recent, Now)!;
        Assert.Equal([["Lower Chrome", "5 min ago"], ["Pause train.py", "a rule, 20 min ago"]], undo.Rows.Select(r => r.Cells!.ToArray()));
        Assert.Equal(("priority.restore", "chrome"), (undo.Actions[0].Action, undo.Actions[0].Target!["app"]));
        Assert.Equal("Undo Pause train.py", undo.Actions[1].Label);
    }

    [Fact]
    public void Nothing_to_undo_is_no_section()
    {
        Assert.Null(HomePage.Undo(null, Now));
        Assert.Null(HomePage.Undo(JsonNode.Parse("[]"), Now));
        Assert.Null(HomePage.Undo(JsonNode.Parse("""[ { "at": "2026-11-06T10:25:00Z", "text": "Lower Chrome" } ]"""), Now));
    }

    [Theory]
    [InlineData(30, "just now")]
    [InlineData(300, "5 min ago")]
    [InlineData(7300, "2 h ago")]
    public void Ago_reads_naturally(int seconds, string expected) => Assert.Equal(expected, HomePage.Ago(TimeSpan.FromSeconds(seconds)));
}
