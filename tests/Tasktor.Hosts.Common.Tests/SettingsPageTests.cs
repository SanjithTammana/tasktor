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
        var packRows = SettingsPage.PackRules(report with { PackRules = [new() { Pack = "p", PackName = "Work pack", Id = "quiet", Description = "Quiet on battery", Ticked = false }] });
        Assert.Equal(("Quiet on battery", "From Work pack", false), (packRows[0].Title, packRows[0].Note, packRows[0].On));
        var tick = SettingsPage.TickPackRule(packRows[0], on: true);
        Assert.Equal(("pack.rule", "p", "quiet", "true"), (tick.Action, tick.Args!["pack"], tick.Args["rule"], tick.Args["on"]));
        var asks = SettingsPage.ArrangeRequests(report with { AwaitingArrange = [new() { Id = "focus-layout", Description = "Writing layout in event mode", Presets = ["Writing"] }] });
        Assert.Equal(("focus-layout", "Writing layout in event mode"), (asks[0].Id, asks[0].Title));
        foreach (var part in new[] { "Writing", "moves", "never opens or closes", "Undo" })
        {
            Assert.Contains(part, asks[0].Consent, StringComparison.Ordinal);
        }

        Assert.Equal(("rule.allow_arrange", "focus-layout"), (SettingsPage.AllowArrange(asks[0]).Action, SettingsPage.AllowArrange(asks[0]).Target!["rule"]));
        var hide = SettingsPage.ShowPage("jobs", shown: false);
        Assert.Equal(("settings.page", "jobs", "false"), (hide.Action, hide.Args!["page"], hide.Args["shown"]));
        Assert.Equal(("rules.edit", "Edit rules.json"), (SettingsPage.EditRules.Action, SettingsPage.EditRules.Label));
    }

    [Fact]
    public void Plugins_show_their_state_and_the_consent_says_everything_d_h7_lists()
    {
        var p = new Contracts.Plugins.PluginInfo
        {
            Id = "todo",
            Name = "To-do count",
            Author = "Example",
            State = Contracts.Snapshot.PluginState.Found,
            Reads = ["A file you choose"],
            Network = ["api.example.com"],
            Actions = ["Refresh"],
            CommandLine = "python.exe main.py",
        };
        var rows = SettingsPage.Plugins(new Contracts.Plugins.PluginsReport
        {
            Plugins = [p, p with { Id = "x", Name = "Broken", State = Contracts.Snapshot.PluginState.Error, Error = "exit code 2" }, p with { Id = "y", Name = "Live", State = Contracts.Snapshot.PluginState.Ok }],
        });
        Assert.Equal([("To-do count", "Found, off", false), ("Broken", "Error: exit code 2", true), ("Live", "On", true)], rows.Select(r => (r.Title, r.Note, r.On)));
        var consent = SettingsPage.PluginConsent(p);
        foreach (var part in new[] { "Example", "A file you choose", "api.example.com", "Refresh", "python.exe main.py", "cannot block", "your rights" })
        {
            Assert.Contains(part, consent, StringComparison.Ordinal);
        }

        Assert.Contains("declares no network use", SettingsPage.PluginConsent(p with { Network = [] }), StringComparison.Ordinal);
        Assert.Equal(("plugin.enable", "todo"), (SettingsPage.SwitchPlugin(rows[0], on: true).Action, SettingsPage.SwitchPlugin(rows[0], true).Target!["plugin"]));
        Assert.Equal("plugin.disable", SettingsPage.SwitchPlugin(rows[2], on: false).Action);
    }

    [Fact]
    public void A_packs_consent_lists_every_plugin_in_it_once()
    {
        var a = new Contracts.Plugins.PluginInfo { Id = "a", Name = "Alpha", State = Contracts.Snapshot.PluginState.Found, Reads = ["mail counts"], CommandLine = "python.exe a.py", Pack = "p" };
        var b = a with { Id = "b", Name = "Beta", Reads = ["calendar"], Network = ["api.example.com"], CommandLine = "python.exe b.py" };
        var other = a with { Id = "c", Name = "Gamma", Pack = null };
        var report = new Contracts.Plugins.PluginsReport { Plugins = [a, b, other], Packs = [new() { Id = "p", Name = "Work pack", Author = "Example" }] };
        var (pack, text) = SettingsPage.PackConsent(report, "p")!.Value;
        Assert.Equal("Work pack", pack.Name);
        foreach (var part in new[] { "From: Example", "Alpha", "mail counts", "a.py", "Beta", "api.example.com", "b.py", "cannot block" })
        {
            Assert.Contains(part, text, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("Gamma", text, StringComparison.Ordinal);
        Assert.Equal(1, text.Split("cannot block").Length - 1); // the limits once, not per plugin
        Assert.Null(SettingsPage.PackConsent(report, "nope"));
        Assert.Equal(("plugin.enable_pack", "p"), (SettingsPage.EnablePack("p").Action, SettingsPage.EnablePack("p").Target!["pack"]));
    }
}
