using System.Text.Json.Nodes;
using Tasktor.Contracts.Cards;
using Tasktor.Hosts.Common.Command;
using Tasktor.Hosts.Common.Render;

namespace Tasktor.Hosts.Common.Tests;

/// <summary>U4 amended D-G12: what the command line offers for a query.</summary>
public sealed class CommandIndexTests
{
    private static readonly BindingResolver Resolver = new(JsonNode.Parse("""
        { "state": {
            "apps": [ { "app_id": "chrome", "name": "Chrome" }, { "app_id": "code", "name": "Code" }, { "app_id": "teams", "name": "Teams" } ],
            "jobs": [ { "id": "j1", "label": "train.py", "status": "working" }, { "id": "j2", "label": "render.sh", "status": "paused" },
                      { "id": "j3", "label": "old.py", "status": "done" } ] } }
        """));

    private static readonly CardAction Lower = new() { Id = "lower", Label = "Lower Chrome's priority", Action = "priority.set", Noun = "Chrome", Target = new Dictionary<string, string> { ["app"] = "chrome" } };
    private static readonly CardAction Snooze = new() { Id = "snooze", Label = "Snooze for an hour", Action = "alerts.snooze_all", Noun = "Memory" };

    private static readonly IReadOnlyList<Card> Cards =
    [
        new Card { Id = "core.memory", Title = "Memory", Severity = Value.Literal("warn"), Blocks = [new BadgeBlock { Text = Value.Literal("Memory is at 86%; Chrome holds 5.7 GB") }], Actions = [Lower, Snooze] },
    ];

    private static List<string> Headings(CommandView v) => [.. v.Sections.Select(s => s.Heading)];

    [Fact]
    public void Empty_query_is_the_verdict_and_the_sentences_own_actions()
    {
        var v = CommandIndex.Query("", Cards, Resolver);
        Assert.Equal("Memory is at 86%; Chrome holds 5.7 GB.", v.Verdict);
        Assert.Equal(["Suggested"], Headings(v));
        Assert.Equal(["Snooze for an hour", "Lower Chrome's priority"], v.Sections[0].Items.Select(i => i.Label)); // reading order of the sentence
    }

    [Fact]
    public void Typing_an_app_leads_with_its_verbs()
    {
        var v = CommandIndex.Query("chr", Cards, Resolver);
        Assert.Equal("Chrome", Headings(v)[0]);
        Assert.Equal(
            ["Lower Chrome's priority", "Turn on efficiency mode for Chrome", "End Chrome…", "Don't capture Chrome"],
            v.Sections[0].Items.Select(i => i.Label));
        Assert.Equal("priority.set", v.First!.Action!.Action);
        Assert.Equal("chrome", v.First.Action.Target!["app"]);
        Assert.True(v.Sections[0].Items.Single(i => i.Label.StartsWith("End", StringComparison.Ordinal)).Action!.Destructive);
        Assert.Contains("Suggested", Headings(v)); // the card's own "Lower Chrome's priority" via its noun
    }

    [Fact]
    public void Jobs_offer_pause_or_resume_and_end_but_nothing_for_finished_ones()
    {
        var train = CommandIndex.Query("train", Cards, Resolver).Sections.Single(s => s.Heading == "Jobs");
        Assert.Equal(["Pause train.py", "End train.py…"], train.Items.Select(i => i.Label));
        Assert.Equal("j1", train.Items[0].Action!.Target!["job"]);

        var render = CommandIndex.Query("render", Cards, Resolver).Sections.Single(s => s.Heading == "Jobs");
        Assert.Equal("Resume render.sh", render.Items[0].Label);

        Assert.DoesNotContain("Jobs", Headings(CommandIndex.Query("old.py", Cards, Resolver)));
    }

    [Fact]
    public void Pages_open_the_deep_window()
    {
        var v = CommandIndex.Query("your", Cards, Resolver);
        var page = v.Sections.Single(s => s.Heading == "Pages").Items.Single();
        Assert.Equal(("Your data", "data", (CardAction?)null), (page.Label, page.Page, page.Action));
        Assert.Equal(9, CommandIndex.Pages.Count);
    }

    [Fact]
    public void No_match_shows_only_the_verdict()
    {
        var v = CommandIndex.Query("zzzz", Cards, Resolver);
        Assert.Empty(v.Sections);
        Assert.Null(v.First);
        Assert.NotEmpty(v.Verdict);
    }

    [Theory]
    [InlineData("chr", "Chrome", 3)]
    [InlineData("CHR", "Chrome", 3)]
    [InlineData("data", "Your data", 2)]
    [InlineData("rom", "Chrome", 1)]
    [InlineData("crm", "Chrome", 0.5)]
    [InlineData("xyz", "Chrome", 0)]
    [InlineData("", "Chrome", 0)]
    public void Score_ranks_prefix_word_substring_subsequence(string query, string text, double expected) =>
        Assert.Equal(expected, CommandIndex.Score(query, text));

    [Fact]
    public void Each_section_is_capped()
    {
        var many = Enumerable.Range(0, 10).Select(i => new CardAction { Id = "a" + i, Label = "Pause thing " + i, Action = "tracking.pause", Target = new Dictionary<string, string> { ["n"] = i.ToString(System.Globalization.CultureInfo.InvariantCulture) } }).ToList();
        var card = new Card { Id = "x", Title = "X", Blocks = [], Actions = many };
        var v = CommandIndex.Query("pause", [card], Resolver);
        Assert.Equal(CommandIndex.PerSection, v.Sections.Single(s => s.Heading == "Suggested").Items.Count);
    }
}
