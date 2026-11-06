using Tasktor.Core.Rules;

namespace Tasktor.Core.Tests;

/// <summary>U4 D-G3 Settings → Rules: a switch writes <c>{ "id": …, "enabled": … }</c> into rules.json, keeping the user's text.</summary>
public sealed class RulesToggleTests
{
    private static RuleSet Load(string? user, string? defaults = null) => RulesLoader.Load(defaults, user, _ => true);

    private const string Defaults = """
        { "rules": [
          { "id": "hot", "description": "CPU hot", "when": { "metric": "system.cpu_pct", "above": 90 }, "then": [ { "pause": "refits" } ] },
          { "id": "quiet", "enabled": false, "when": { "metric": "system.cpu_pct", "below": 1 }, "then": [ { "pause": "refits" } ] }
        ] }
        """;

    [Fact]
    public void A_missing_file_becomes_a_rules_file_with_one_toggle()
    {
        var text = RulesToggle.Apply(null, "hot", enabled: false);
        Assert.False(Load(text, Defaults).IsOn("hot"));
        Assert.Contains("// ", text, StringComparison.Ordinal); // says what it is
    }

    [Fact]
    public void Comments_layout_and_other_rules_are_kept_and_the_toggle_is_appended()
    {
        const string user = """
            // my rules
            {
              "rules": [
                // a note about mine
                { "id": "mine", "when": { "metric": "system.cpu_pct", "above": 95 }, "then": [ { "pause": "refits" } ] },
              ],
            }
            """;
        var text = RulesToggle.Apply(user, "hot", enabled: false);
        Assert.StartsWith("// my rules", text, StringComparison.Ordinal);
        Assert.Contains("// a note about mine", text, StringComparison.Ordinal);
        var set = Load(text, Defaults);
        Assert.False(set.IsOn("hot"));
        Assert.NotNull(set.Find("mine"));
        Assert.Empty(set.Errors);
    }

    [Fact]
    public void Toggling_again_replaces_the_toggle_instead_of_adding_another()
    {
        var off = RulesToggle.Apply("""{ "rules": [] }""", "hot", enabled: false);
        var on = RulesToggle.Apply(off, "hot", enabled: true);
        Assert.Equal(1, on.Split("\"hot\"").Length - 1);
        Assert.True(Load(on, Defaults).IsOn("hot"));
        Assert.NotNull(Load(RulesToggle.Apply(on, "quiet", enabled: true), Defaults).Find("quiet")); // a shipped-off rule turned on
    }

    [Fact]
    public void A_users_own_rule_is_switched_by_a_toggle_after_it()
    {
        const string user = """{ "rules": [ { "id": "mine", "when": { "metric": "system.cpu_pct", "above": 95 }, "then": [ { "pause": "refits" } ] } ] }""";
        var off = RulesToggle.Apply(user, "mine", enabled: false);
        Assert.False(Load(off, Defaults).IsOn("mine"));
        Assert.NotNull(Load(RulesToggle.Apply(off, "mine", enabled: true), Defaults).Find("mine"));
    }

    [Fact]
    public void A_file_without_a_rules_list_gets_one_and_unreadable_text_is_refused()
    {
        var text = RulesToggle.Apply("""{ "groups": { "call_apps": ["zoom.exe"] } }""", "hot", enabled: false);
        var set = Load(text, Defaults);
        Assert.False(set.IsOn("hot"));
        Assert.Equal(["zoom.exe"], set.Groups["call_apps"]);
        Assert.Throws<FormatException>(() => RulesToggle.Apply("{ not json", "hot", enabled: false)); // never overwrite what we cannot read
    }
}
