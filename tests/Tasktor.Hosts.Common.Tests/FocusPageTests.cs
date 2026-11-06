using System.Text.Json.Nodes;
using Tasktor.Hosts.Common.Deep;
using Tasktor.Hosts.Common.Render;

namespace Tasktor.Hosts.Common.Tests;

/// <summary>U4 D-G3 Focus page (E11.1, E11.2): state.focus plus the focus pull as rows with the timer's actions.</summary>
public sealed class FocusPageTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);

    private static readonly JsonNode Pulled = JsonNode.Parse("""
        { "tracking": true,
          "presets": [ { "name": "Pomodoro 25/5", "summary": "25 min focus, 5 min break, a long break every 4" }, { "name": "50/10", "summary": "50 min focus, 10 min break" } ],
          "blocks": [ { "at": "2026-11-06T09:00:00Z", "preset": "50/10", "minutes": 50, "end": "completed", "on_intent_share": 0.82, "switches": 3, "excluded": false },
                      { "at": "2026-11-05T09:00:00Z", "preset": "Pomodoro 25/5", "minutes": 12, "end": "stopped", "on_intent_share": null, "switches": null, "excluded": false },
                      { "at": "2026-11-04T09:00:00Z", "preset": "Pomodoro 25/5", "minutes": 25, "end": "skipped", "on_intent_share": 0.3, "switches": 9, "excluded": true } ],
          "stats": [],
          "review": "50/10 kept you on course longer than 25/5 (82% vs 71%, 12 blocks each)" }
        """)!;

    private static JsonNode State(string block = "focus", bool paused = false, int minutes = 12) =>
        JsonNode.Parse($$"""{ "preset": "50/10", "block": "{{block}}", "until": "{{Now.AddMinutes(minutes):O}}", "paused": {{(paused ? "true" : "false")}} }""")!;

    [Fact]
    public void A_running_block_shows_preset_time_left_and_pause_skip_extend_stop()
    {
        var now = FocusPage.Sections(State(), null, Now).Single();
        Assert.Equal(
            [("Preset", "50/10"), ("Block", "Focus"), ("Time left", "12 min left")],
            now.Rows.Select(r => (r.Label, r.Value!)));
        Assert.Equal(["focus.pause", "focus.skip", "focus.extend", "focus.stop"], now.Actions.Select(a => a.Action!));
        Assert.Equal(["Pause", "Skip", "+5 min", "Stop"], now.Actions.Select(a => a.Label));
        Assert.All(now.Actions, a => Assert.NotEqual(true, a.Destructive));
        Assert.All(now.Actions, a => Assert.Null(a.Target));
        Assert.All(now.Actions, a => Assert.Null(a.Args));
    }

    [Fact]
    public void A_paused_block_offers_resume_and_does_not_guess_the_time_left()
    {
        var now = FocusPage.Sections(State(paused: true), null, Now).Single();
        Assert.Equal("Resume", now.Actions[0].Label);
        Assert.Equal("focus.pause", now.Actions[0].Action);
        Assert.Contains(now.Rows, r => r is { Label: "Block", Value: "Focus (paused)" });
        Assert.DoesNotContain(now.Rows, r => r.Label == "Time left");
    }

    [Fact]
    public void A_finished_block_waits_for_next_plus_5_or_stop()
    {
        var now = FocusPage.Sections(State("done", minutes: -1), null, Now).Single();
        Assert.Contains(now.Rows, r => r is { Label: "Block", Value: "Done, waiting for you" });
        Assert.Equal(["focus.next", "focus.extend", "focus.stop"], now.Actions.Select(a => a.Action!));
        Assert.Equal(["Next", "+5 min", "Stop"], now.Actions.Select(a => a.Label));
    }

    [Theory]
    [InlineData("break", "Break")]
    [InlineData("long_break", "Long break")]
    public void Break_kinds_read_as_words(string block, string word) =>
        Assert.Contains(FocusPage.Sections(State(block), null, Now)[0].Rows, r => r is { Label: "Block" } && r.Value == word);

    [Fact]
    public void An_overdue_block_says_now()
    {
        Assert.Contains(FocusPage.Sections(State(minutes: -3), null, Now)[0].Rows, r => r is { Label: "Time left", Value: "now" });
    }

    [Fact]
    public void Presets_to_start_from_carry_the_preset_name_as_an_arg()
    {
        var s = FocusPage.Sections(null, Pulled, Now);
        Assert.Equal(["Focus timer", "Pomodoro 25/5", "50/10", "Recent blocks", "How your blocks went"], s.Select(x => x.Title));
        Assert.Contains(s[0].Rows, r => r.Value == "No block running.");
        Assert.Empty(s[0].Actions);
        var start = Assert.Single(s[1].Actions);
        Assert.Equal(("focus.start", "Start", "Pomodoro 25/5"), (start.Action, start.Label, start.Args!["preset"]));
        Assert.Null(start.Target);
        Assert.NotEqual(true, start.Destructive);
        Assert.Equal("25 min focus, 5 min break, a long break every 4", s[1].Rows.Single().Value);
        Assert.Equal("50/10", s[2].Actions[0].Args!["preset"]);
    }

    [Fact]
    public void While_a_block_runs_starting_another_preset_says_it_replaces_the_current_one()
    {
        var s = FocusPage.Sections(State(), Pulled, Now);
        Assert.Equal("Switch to this", s[2].Actions[0].Label);
        Assert.Equal("focus.start", s[2].Actions[0].Action);
    }

    [Fact]
    public void Recent_blocks_are_a_table_newest_first_with_how_each_ended_and_how_it_went()
    {
        var recent = FocusPage.Sections(null, Pulled, Now).Single(x => x.Id == "focus.recent");
        Assert.Equal(["When", "Preset", "Minutes", "Ended", "On course"], recent.Rows[0].Cells);
        var rows = recent.Rows.Where(r => r.Kind == RowKind.TableRow).Select(r => r.Cells!).ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal(["50/10", "50", "Completed", "82%"], rows[0].Skip(1));
        Assert.Equal(["Pomodoro 25/5", "12", "Stopped", "–"], rows[1].Skip(1));
        Assert.Equal(["Pomodoro 25/5", "25", "Skipped", "left out"], rows[2].Skip(1));
        Assert.Empty(recent.Actions);
    }

    [Fact]
    public void The_comparison_line_is_the_agents_words()
    {
        var review = FocusPage.Sections(null, Pulled, Now).Single(x => x.Id == "focus.review");
        Assert.Equal(RowKind.Text, review.Rows.Single().Kind);
        Assert.StartsWith("50/10 kept you on course longer", review.Rows[0].Label, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_data_is_a_quiet_page_not_an_error()
    {
        var idle = FocusPage.Sections(null, null, Now);
        Assert.Equal("focus.now", idle.Single().Id);

        var empty = FocusPage.Sections(null, JsonNode.Parse("""{ "tracking": false, "presets": [], "blocks": [], "stats": [], "review": "Turn on tracking to see how focused blocks went" }"""), Now);
        Assert.Equal(["Focus timer", "Recent blocks", "How your blocks went"], empty.Select(x => x.Title));
        Assert.Contains("No focus blocks yet", empty[1].Rows[0].Label, StringComparison.Ordinal);
        Assert.Equal("Turn on tracking to see how focused blocks went", empty[2].Rows[0].Label);

        var malformed = FocusPage.Sections(JsonNode.Parse("""{ "block": "focus" }"""), JsonNode.Parse("[1,2]"), Now);
        Assert.Equal("focus.now", malformed.Single().Id);
        Assert.Contains(malformed[0].Rows, r => r.Value == "No block running.");
    }
}
