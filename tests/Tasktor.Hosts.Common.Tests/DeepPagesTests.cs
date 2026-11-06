using Tasktor.Contracts.Cards;
using Tasktor.Hosts.Common.Deep;

namespace Tasktor.Hosts.Common.Tests;

/// <summary>U4 D-G3 / amended D-G13: the deep window's pages, their cards, and how a launch picks one.</summary>
public sealed class DeepPagesTests
{
    private static Card C(string id) => new() { Id = id, Title = id, Blocks = [] };

    [Fact]
    public void Nine_pages_with_Module_pages_shown_only_while_their_cards_exist()
    {
        Assert.Equal(9, DeepPages.All.Count);
        var core = DeepPages.Visible([C("core.system"), C("core.jobs")]).Select(p => p.Id).ToList();
        Assert.Equal(["home", "system", "jobs", "activity", "learned", "data", "settings"], core);
        var withModules = DeepPages.Visible([C("core.system"), C("module.focus"), C("module.presets")]).Select(p => p.Id).ToList();
        Assert.Contains("focus", withModules);
        Assert.Contains("windows", withModules);
    }

    [Fact]
    public void Home_shows_the_pinned_cards_in_pin_order_and_other_pages_their_own()
    {
        Card[] cards = [C("core.workflow"), C("core.system"), C("core.jobs"), C("module.ports"), C("plugin.weather")];
        Assert.Equal(["core.system", "core.jobs", "core.workflow"], DeepPages.CardsFor("home", cards).Select(c => c.Id));
        Assert.Equal(["plugin.weather", "core.jobs"], DeepPages.CardsFor("home", cards, ["plugin.weather", "missing", "core.jobs"]).Select(c => c.Id));
        Assert.Equal(["core.system", "module.ports"], DeepPages.CardsFor("system", cards).Select(c => c.Id));
        Assert.Empty(DeepPages.CardsFor("settings", cards));
        Assert.Empty(DeepPages.CardsFor("nope", cards));
    }

    [Fact]
    public void Card_prefixes_match_whole_segments()
    {
        Assert.Single(DeepPages.CardsFor("jobs", [C("core.jobs.gpu")]));
        Assert.Empty(DeepPages.CardsFor("jobs", [C("core.jobsx")]));
    }

    [Theory]
    [InlineData(new[] { "x.exe", "--open", "jobs" }, "jobs")]
    [InlineData(new[] { "x.exe", "--open" }, "home")]
    [InlineData(new[] { "x.exe", "--open", "nope" }, "home")]
    [InlineData(new[] { "x.exe" }, "home")]
    public void A_launch_opens_the_named_page_or_Home(string[] args, string expected) =>
        Assert.Equal(expected, DeepPages.FromArgs(args));

    [Fact]
    public void Unknown_page_ids_normalize_to_Home()
    {
        Assert.Equal("data", DeepPages.Normalize("data"));
        Assert.Equal("home", DeepPages.Normalize(null));
        Assert.Equal("home", DeepPages.Normalize("../etc"));
    }
}
