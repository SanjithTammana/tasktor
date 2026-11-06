using System.Text.Json.Nodes;
using Tasktor.Hosts.Common.Deep;

namespace Tasktor.Hosts.Common.Tests;

/// <summary>U4 D-G3 Windows page (D-W5): the presets pull as rows with Arrange, Apply and Undo.</summary>
public sealed class WindowsPageTests
{
    private static readonly JsonNode Pulled = JsonNode.Parse("""
        { "presets": [ { "name": "Work", "slots": 3, "launches": 1, "layouts": 0, "hotkey": "Ctrl+Alt+1", "can_undo": true },
                       { "name": "Reading", "slots": 1, "launches": 0, "layouts": 2, "hotkey": null, "can_undo": false } ] }
        """)!;

    [Fact]
    public void Each_preset_shows_what_it_places_and_whether_it_starts_apps()
    {
        var s = WindowsPage.Sections(Pulled);
        Assert.Equal(["Work", "Reading"], s.Select(x => x.Title));
        Assert.Equal(
            [("Windows placed", "3"), ("Starts apps", "1 of them"), ("Hotkey", "Ctrl+Alt+1")],
            s[0].Rows.Select(r => (r.Label, r.Value!)));
        Assert.Equal([("Windows placed", "1"), ("FancyZones layouts", "2")], s[1].Rows.Select(r => (r.Label, r.Value!)));
    }

    [Fact]
    public void Arrange_and_apply_name_the_preset_as_their_target()
    {
        var work = WindowsPage.Sections(Pulled)[0];
        Assert.Equal(["preset.arrange", "preset.apply", "preset.undo"], work.Actions.Select(a => a.Action!));
        Assert.Equal(["Arrange", "Apply", "Undo"], work.Actions.Select(a => a.Label));
        Assert.All(work.Actions, a => Assert.Equal("Work", a.Target!["preset"]));
        Assert.All(work.Actions, a => Assert.Null(a.Args));
        Assert.All(work.Actions, a => Assert.NotEqual(true, a.Destructive));
    }

    [Fact]
    public void Undo_is_offered_only_while_there_is_something_to_undo()
    {
        var reading = WindowsPage.Sections(Pulled)[1];
        Assert.Equal(["preset.arrange", "preset.apply"], reading.Actions.Select(a => a.Action!));
    }

    [Fact]
    public void Action_ids_are_unique_within_a_section()
    {
        Assert.All(WindowsPage.Sections(Pulled), s => Assert.Equal(s.Actions.Count, s.Actions.Select(a => a.Id).Distinct().Count()));
    }

    [Fact]
    public void No_presets_says_how_to_make_one_and_no_pull_is_no_page()
    {
        var s = WindowsPage.Sections(JsonNode.Parse("""{ "presets": [] }"""));
        Assert.Contains("No presets yet", Assert.Single(s).Rows[0].Label, StringComparison.Ordinal);
        Assert.Empty(WindowsPage.Sections(null));
        Assert.Empty(WindowsPage.Sections(JsonNode.Parse("[]")));
    }

    [Fact]
    public void A_preset_without_a_name_is_skipped()
    {
        var s = WindowsPage.Sections(JsonNode.Parse("""{ "presets": [ { "slots": 2 }, { "name": "Ok", "slots": 1 } ] }"""));
        Assert.Equal(["Ok"], s.Select(x => x.Title));
    }
}
