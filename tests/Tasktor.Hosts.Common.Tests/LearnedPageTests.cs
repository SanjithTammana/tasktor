using System.Text.Json.Nodes;
using Tasktor.Hosts.Common.Deep;
using Tasktor.Hosts.Common.Render;

namespace Tasktor.Hosts.Common.Tests;

/// <summary>U4 D-G3 "What Tasktor learned" (B6): the learned pull as rows with Relearn and Forget.</summary>
public sealed class LearnedPageTests
{
    private static readonly JsonNode Pulled = JsonNode.Parse("""
        { "learning": true,
          "kinds": [ { "kind": "a1b2", "name": "train.py", "runs": 12, "good_runs": 10, "usual_duration_s": 1080, "silent_threshold_s": 360, "source": "learned", "false_alarms": 1, "mode": "normal" },
                     { "kind": "c3d4", "name": "render.sh", "runs": 2, "good_runs": 0, "usual_duration_s": null, "silent_threshold_s": 600, "source": "default", "false_alarms": 0 } ],
          "guardrails": [ { "kind": "a1b2", "at": "2026-11-06T09:00:00Z", "action": "fallback", "reason": "3 false alarms" } ] }
        """)!;

    [Fact]
    public void Each_kind_shows_what_was_learned_with_relearn_and_a_two_step_forget()
    {
        var s = LearnedPage.Sections(Pulled);
        Assert.Equal(["Learning from your jobs", "train.py", "render.sh", "Guardrails"], s.Select(x => x.Title));
        var train = s[1];
        Assert.Equal(
            [("Runs", "12 (10 finished well)"), ("Usually takes", "18 min"), ("Flagged when quiet for", "6 min (learned)"), ("False alarms", "1")],
            train.Rows.Select(r => (r.Label, r.Value!)));
        Assert.Equal(["learned.relearn", "learned.forget"], train.Actions.Select(a => a.Action!));
        Assert.True(train.Actions[1].Destructive);
        Assert.Equal("a1b2", train.Actions[0].Target!["kind"]);
        Assert.Equal("10 min (default)", s[2].Rows.Single(r => r.Label == "Flagged when quiet for").Value);
        Assert.DoesNotContain(s[2].Rows, r => r.Label is "Usually takes" or "False alarms");
    }

    [Fact]
    public void Learning_can_be_turned_off_and_on_and_guardrails_name_the_kind()
    {
        var s = LearnedPage.Sections(Pulled);
        Assert.Equal(("Stop learning", "false"), (s[0].Actions[0].Label, s[0].Actions[0].Args!["on"]));
        var log = s[^1].Rows.Single(r => r.Kind == RowKind.TableRow);
        Assert.Equal("train.py", log.Cells![1]);
        Assert.Equal("3 false alarms", log.Cells[3]);
    }

    [Fact]
    public void Nothing_learned_says_so_and_no_pull_is_no_page()
    {
        var s = LearnedPage.Sections(JsonNode.Parse("""{ "learning": false, "kinds": [], "guardrails": [] }"""));
        Assert.Equal("Learn from my jobs", s[0].Actions[0].Label);
        Assert.Contains("Nothing learned yet", s[1].Rows[0].Label, StringComparison.Ordinal);
        Assert.Empty(LearnedPage.Sections(null));
    }

    [Theory]
    [InlineData(45, "45 s")]
    [InlineData(1080, "18 min")]
    [InlineData(3600, "1 h")]
    [InlineData(3900, "1 h 5 min")]
    public void Durations_read_naturally(double seconds, string expected) => Assert.Equal(expected, LearnedPage.Duration(seconds));
}
