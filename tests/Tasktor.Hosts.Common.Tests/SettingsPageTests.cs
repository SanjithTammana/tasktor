using Tasktor.Contracts.Config;
using Tasktor.Hosts.Common.Deep;
using Tasktor.Tests.Shared;

namespace Tasktor.Hosts.Common.Tests;

/// <summary>U4 D-G3 Settings → Modules: switches, restart notes, consent text.</summary>
public sealed class SettingsPageTests
{
    [Fact]
    public void Every_Module_is_listed_with_its_state_and_a_note_when_a_restart_is_pending()
    {
        var report = new ModulesReport
        {
            Modules =
            [
                new ModuleState { Id = "tracking", On = true, Running = true, Consented = true },
                new ModuleState { Id = "focus", On = true, Running = false, Consented = true },
                new ModuleState { Id = "ports", On = false, Running = true },
            ],
        };
        var rows = SettingsPage.Modules(report);
        Assert.Equal(ModuleCatalog.All.Count, rows.Count);
        Assert.Equal((true, (string?)null), (rows.Single(r => r.Info.Id == "tracking").On, rows.Single(r => r.Info.Id == "tracking").Note));
        Assert.Equal("On after Tasktor restarts", rows.Single(r => r.Info.Id == "focus").Note);
        Assert.Equal("Off after Tasktor restarts", rows.Single(r => r.Info.Id == "ports").Note);
        Assert.False(rows.Single(r => r.Info.Id == "batteries").On);
    }

    [Fact]
    public void Capture_is_never_turned_on_from_the_switch()
    {
        var rows = SettingsPage.Modules(null);
        Assert.False(rows.Single(r => r.Info.Id == "capture").CanTurnOnHere);
        Assert.All(rows.Where(r => r.Info.Id != "capture"), r => Assert.True(r.CanTurnOnHere));
    }

    [Fact]
    public void Consent_says_what_it_reads_where_it_is_kept_and_what_it_contacts()
    {
        var local = SettingsPage.Consent(ModuleCatalog.Find("tracking")!);
        Assert.Contains("What it reads: Which app is in front", local, StringComparison.Ordinal);
        Assert.Contains("Where it is kept: On this PC", local, StringComparison.Ordinal);
        Assert.Contains("It makes no network connection.", local, StringComparison.Ordinal);
        Assert.Contains("What it contacts: the calendar address you give.", SettingsPage.Consent(ModuleCatalog.Find("next_event")!), StringComparison.Ordinal);
    }

    [Fact]
    public void Catalog_ids_are_unique_and_cover_every_Module_the_agent_composes()
    {
        Assert.Equal(ModuleCatalog.All.Count, ModuleCatalog.All.Select(m => m.Id).Distinct().Count());
        // Every id the source reads with ModuleOn("…") or registers with RegisterModule("…") (as the G-net runtime gate finds
        // them): a new Module fails here until it has its consent text.
        var composed = Directory.EnumerateFiles(Path.Combine(RepoPaths.Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), "(?:ModuleOn|RegisterModule)\\(\"([a-z_]+)\"").Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(composed);
        Assert.Equal(composed.Order(), ModuleCatalog.All.Select(m => m.Id).Order());
    }

    [Fact]
    public void General_shows_the_command_line_shortcut_and_says_when_another_program_holds_it()
    {
        static System.Text.Json.Nodes.JsonNode Root(string state) => System.Text.Json.Nodes.JsonNode.Parse("{\"state\":" + state + "}")!;
        Assert.Equal(new HotkeyRow("Win+Alt+T", null), SettingsPage.Hotkey(Root("""{"hotkey":{"keys":"Win+Alt+T","registered":true}}""")));
        Assert.Equal(new HotkeyRow("Ctrl+Alt+K", "Another program uses this shortcut. Pick another."), SettingsPage.Hotkey(Root("""{"hotkey":{"keys":"Ctrl+Alt+K","registered":false}}""")));
        Assert.Null(SettingsPage.Hotkey(Root("{}"))); // no tray (headless): nothing to set
        var req = SettingsPage.SetHotkey(" ctrl+shift+space ");
        Assert.Equal(("settings.hotkey", "ctrl+shift+space"), (req.Action, req.Args!["keys"]));
    }

    [Fact]
    public void Rules_list_each_rule_with_its_source_and_last_firing_on_ones_first()
    {
        var report = new Contracts.Rules.RulesReport
        {
            Rules =
            [
                new() { Id = "quiet", Source = "default", On = false },
                new() { Id = "hot", Description = "CPU hot for 5 min", Source = "default", On = true, LastFired = new DateTimeOffset(2026, 11, 6, 14, 5, 0, TimeSpan.Zero) },
                new() { Id = "mine", Source = "user", On = true },
            ],
            Errors = ["rules.json line 4 (bad): unknown metric: x"],
        };
        var rows = SettingsPage.Rules(report, TimeZoneInfo.Utc);
        Assert.Equal(["CPU hot for 5 min", "mine", "quiet"], rows.Select(r => r.Title));
        Assert.Equal("Shipped with Tasktor · last fired 14:05", rows[0].Note);
        Assert.Equal(("Yours (rules.json)", true), (rows[1].Note, rows[1].On));
        Assert.Equal(("rule.disable", "hot"), (SettingsPage.SwitchRule(rows[0], on: false).Action, SettingsPage.SwitchRule(rows[0], false).Target!["rule"]));
        Assert.Equal("rule.enable", SettingsPage.SwitchRule(rows[2], on: true).Action);
        Assert.Empty(SettingsPage.Rules(null, TimeZoneInfo.Utc));
    }
}
