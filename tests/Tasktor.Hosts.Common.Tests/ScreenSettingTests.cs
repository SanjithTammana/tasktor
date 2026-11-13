using System.Text.Json.Nodes;
using Tasktor.Hosts.Common.Deep;

namespace Tasktor.Hosts.Common.Tests;

public class ScreenSettingTests
{
    private static JsonNode Root(string? screen) =>
        JsonNode.Parse(screen is null ? """{"state":{}}""" : "{\"state\":{\"screen\":\"" + screen + "\"}}")!;

    [Fact]
    public void One_screen_never_asks_and_never_offers_the_setting()
    {
        Assert.False(ScreenSetting.Ask(Root(null), screens: 1, connected: true));
        Assert.False(ScreenSetting.Offered(1));
    }

    [Fact]
    public void Two_screens_ask_once_until_a_choice_is_kept()
    {
        Assert.True(ScreenSetting.Ask(Root(null), screens: 2, connected: true));
        Assert.True(ScreenSetting.Ask(Root(null), screens: 3, connected: true)); // any number of screens, not just two
        Assert.False(ScreenSetting.Ask(Root("main"), screens: 2, connected: true));
        Assert.False(ScreenSetting.Ask(Root("working"), screens: 3, connected: true));
        Assert.True(ScreenSetting.Offered(2));
    }

    [Fact]
    public void Without_the_agent_the_shelf_does_not_ask_because_the_answer_could_not_be_kept() =>
        Assert.False(ScreenSetting.Ask(Root(null), screens: 2, connected: false));

    [Theory]
    [InlineData(null, "working")]
    [InlineData("main", "main")]
    [InlineData("working", "working")]
    [InlineData("left", "working")] // an unknown value is ignored, not trusted
    public void The_effective_choice_defaults_to_the_working_screen(string? stored, string effective) =>
        Assert.Equal(effective, ScreenSetting.Effective(Root(stored)));

    [Fact]
    public void The_action_carries_the_choice()
    {
        var set = ScreenSetting.Set(ScreenSetting.Main);
        Assert.Equal(("settings.screen", "main"), (set.Action, set.Args!["screen"]));
    }
}
