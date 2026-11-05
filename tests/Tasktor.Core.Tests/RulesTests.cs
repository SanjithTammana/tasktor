using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Snapshot;
using Tasktor.Core.Rules;
using Tasktor.Tests.Shared;

namespace Tasktor.Core.Tests;

public sealed class RulesLoaderTests
{
    private const string Groups = "\"groups\": { \"builds\": [\"msbuild.exe\"], \"agents\": [\"claude.exe\"] },";

    private static RuleSet User(string rulesJson, Func<string, bool>? consent = null) =>
        RulesLoader.Load(null, "{ " + Groups + " \"rules\": [ " + rulesJson + " ] }", consent);

    [Fact]
    public void The_spec_example_loads()
    {
        var text = File.ReadAllText(Path.Combine(RepoPaths.Fixtures, "tasktor.rules-1", "spec-example.json"));
        var set = RulesLoader.Load(text, null);
        Assert.Empty(set.Errors);
        Assert.Equal(["disk-low", "job-silent", "battery-restrain", "new-pdf"], set.Rules.Select(r => r.Id));
        Assert.Equal(TimeSpan.FromMinutes(5), set.Find("disk-low")!.HoldFor);
        Assert.Equal(TimeSpan.FromHours(6), set.Find("disk-low")!.Cooldown);
        Assert.True(set.Find("job-silent")!.IsBuiltin);
    }

    public static TheoryData<string, string> Refusals() => new()
    {
        { """{ "id": "a", "when": { "metric": "system.cpu_pct", "above": 90, "state": "on_battery" }, "then": [ { "pause": "refits" } ] }""", "more than one kind" },
        { """{ "id": "a", "when": {}, "then": [ { "pause": "refits" } ] }""", "no kind" },
        { """{ "id": "a", "when": { "metric": "cpu.magic", "above": 1 }, "then": [ { "pause": "refits" } ] }""", "unknown metric" },
        { """{ "id": "a", "when": { "metric": "system.cpu_pct" }, "then": [ { "pause": "refits" } ] }""", "above or below" },
        { """{ "id": "a", "when": { "state": "raining" }, "then": [ { "pause": "refits" } ] }""", "unknown state" },
        { """{ "id": "a", "when": { "event": "file.deleted" }, "then": [ { "pause": "refits" } ] }""", "unknown event" },
        { """{ "id": "a", "when": { "group": "games", "running": true }, "then": [ { "pause": "refits" } ] }""", "unknown group" },
        { """{ "id": "a", "when": { "state": "on_battery" }, "for": "five", "then": [ { "pause": "refits" } ] }""", "not a duration" },
        { """{ "id": "a", "when": { "state": "on_battery" }, "then": [] }""", "at least one action" },
        { """{ "id": "a", "when": { "state": "on_battery" }, "then": [ { "pause": "refits", "toast": "x" } ] }""", "more than one kind" },
        { """{ "id": "a", "when": { "state": "on_battery" }, "then": [ { "toast": "x", "buttons": ["open:a", "open:b", "open:c", "open:d"] } ] }""", "at most 3 buttons" },
        { """{ "id": "a", "when": { "state": "on_battery" }, "then": [ { "toast": "x", "buttons": ["run:convert"] } ] }""", "names no command" },
        { """{ "id": "a", "when": { "state": "on_battery" }, "then": [ { "toast": "x", "buttons": ["format.disk"] } ] }""", "unknown button" },
        { """{ "id": "a", "when": { "state": "on_battery" }, "then": [ { "pause": "the-world" } ] }""", "unknown action target" },
        { """{ "id": "a", "when": { "state": "on_battery" }, "then": [ { "set_priority": "realtime", "target": "group:builds" } ] }""", "set_priority must be" },
        { """{ "id": "a", "when": { "state": "on_battery" }, "then": [ { "pause": "job:group:builds", "mode": "sleep" } ] }""", "mode must be" },
        { """{ "id": "a", "when": { "state": "on_battery" }, "then": [ { "cap_cores": { "max": 0 }, "target": "group:builds" } ] }""", "at least 1" },
        { """{ "id": "a", "when": { "state": "on_battery" }, "then": [ { "use_fallback": "silent" } ] }""", "built-in rules only" },
        { """{ "id": "a", "builtin": true, "when": { "state": "on_battery" }, "then": [ { "pause": "refits" } ] }""", "reserved for shipped" },
        { """{ "id": "a", "when": { "state": "on_battery" }, "then": [ { "arrange": "Writing" } ] }""", "one-time consent" },
        { """{ "id": "a", "when": { "learned": { "stat": "silence", "key": "job.kind", "fallback": "10m", "clamp": ["60m", "2m"] } }, "then": [ { "pause": "refits" } ] }""", "clamp" },
        { """{ "id": "a", "when": { "process": "python.exe" }, "then": [ { "pause": "refits" } ] }""", "cpu_above" },
        { """{ "id": "a", "when": "nope", "then": [] }""", "JSON value could not be converted" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void Bad_rules_are_disabled_with_a_reason(string rule, string reason)
    {
        var set = User(rule);
        Assert.Empty(set.Rules);
        var e = Assert.Single(set.Errors);
        Assert.Contains(reason, e.Message, StringComparison.Ordinal);
        Assert.Equal(RuleOrigin.User, e.Origin);
    }

    [Fact]
    public void Arrange_loads_after_the_one_time_consent()
    {
        var set = User("""{ "id": "focus-layout", "when": { "state": "event_mode" }, "then": [ { "arrange": "Writing" } ], "undo": true }""", id => id == "focus-layout");
        Assert.Single(set.Rules);
    }

    [Fact]
    public void One_bad_rule_does_not_stop_the_rest_and_errors_carry_line_numbers()
    {
        const string text = """
            // my rules
            {
              "rules": [
                { "id": "good-1", "when": { "state": "on_battery" }, "then": [ { "pause": "refits" } ] },
                /* a broken one */
                {
                  "id": "bad",
                  "when": { "state": "nope" },
                  "then": [ { "pause": "refits" } ]
                },
                { "id": "good-2", "when": { "state": "in_call" }, "then": [ { "pause": "refits" } ] },
              ],
            }
            """;
        var set = RulesLoader.Load(null, text);
        Assert.Equal(["good-1", "good-2"], set.Rules.Select(r => r.Id));
        var e = Assert.Single(set.Errors);
        Assert.Equal(6, e.Line);
        Assert.Equal("rules.json line 6 (bad): unknown state: nope", e.ToString());
    }

    [Fact]
    public void Broken_json_keeps_the_defaults_running()
    {
        var defaults = """{ "rules": [ { "id": "d", "when": { "state": "on_battery" }, "then": [ { "pause": "refits" } ] } ] }""";
        var set = RulesLoader.Load(defaults, "{ \"rules\": [ { \"id\": \n");
        Assert.Single(set.Rules);
        Assert.Contains("not valid JSON", Assert.Single(set.Errors).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void User_rules_override_defaults_by_id_and_can_switch_them_off()
    {
        var defaults = """
            { "groups": { "builds": ["msbuild.exe"] }, "rules": [
              { "id": "d1", "when": { "state": "on_battery" }, "then": [ { "pause": "refits" } ] },
              { "id": "d2", "when": { "state": "on_battery" }, "then": [ { "pause": "tracking" } ] } ] }
            """;
        var user = """
            { "groups": { "builds": ["msbuild.exe", "cl.exe"] }, "rules": [
              { "id": "d1", "when": { "state": "in_call" }, "then": [ { "pause": "refits" } ] },
              { "id": "d2", "enabled": false, "when": { "state": "on_battery" }, "then": [ { "pause": "tracking" } ] } ] }
            """;
        var set = RulesLoader.Load(defaults, user);
        var d1 = Assert.Single(set.Rules);
        Assert.Equal("in_call", d1.Rule.When.State);
        Assert.Equal(RuleOrigin.User, d1.Origin);
        Assert.Equal(2, set.Groups["builds"].Count);
    }

    [Fact]
    public void A_newer_schema_version_is_refused()
    {
        var set = RulesLoader.Load(null, """{ "schema_version": 2, "rules": [] }""");
        Assert.Contains("newer", Assert.Single(set.Errors).Message, StringComparison.Ordinal);
    }
}

public sealed class RuleEngineTests
{
    private readonly FakeClock _clock = new();
    private readonly RuleEngine _engine = new(name => name == "Downloads" ? "/home/u/Downloads" : null);

    private void Load(string json)
    {
        var set = RulesLoader.Load(null, json, _ => true);
        Assert.Empty(set.Errors);
        _engine.SetRules(set);
    }

    private List<RuleOutput> Eval(RuleInputs inputs) => _engine.Evaluate(_clock.UtcNow, inputs);

    [Fact]
    public void Disk_low_fires_after_its_hold_once_then_respects_the_cooldown()
    {
        // A5 acceptance 2 shape: fire once; not again until the cooldown passes.
        Load("""
            { "rules": [ { "id": "disk-low", "when": { "metric": "disk.free_pct", "below": 10 }, "for": "5m", "cooldown": "24h",
            "then": [ { "toast": "Drive {instance} is down to {value}% free", "buttons": ["open:core.disk", "disk.storage_settings", "snooze:1d"] } ] } ] }
            """);
        var low = new RuleInputs().Metric("disk.free_pct", 7, "C:").Metric("disk.free_pct", 50, "D:");
        Assert.Empty(Eval(low));
        _clock.Advance(TimeSpan.FromMinutes(5));
        var fired = Assert.Single(Eval(low));
        Assert.Equal("Drive C: is down to 7% free", fired.Toast!.Title);
        Assert.Equal("disk-low:C:", fired.Toast.Tag);
        Assert.Equal(["Show", "Open Storage settings", "Snooze 1 d"], fired.Toast.Buttons.Select(b => b.Label));
        Assert.All(fired.Toast.Buttons, b => Assert.Equal(Origin.Toast, b.Request.Origin));
        _clock.Advance(TimeSpan.FromHours(1));
        Assert.Empty(Eval(low)); // still true: no repeat
        Assert.Empty(Eval(new RuleInputs().Metric("disk.free_pct", 30, "C:")));
        _clock.Advance(TimeSpan.FromHours(1));
        Eval(low);
        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Empty(Eval(low)); // re-armed, but inside the 24 h cooldown
        _clock.Advance(TimeSpan.FromHours(23));
        Assert.Single(Eval(low));
    }

    [Fact]
    public void Undo_reverses_reversible_actions_when_the_condition_clears()
    {
        var text = File.ReadAllText(Path.Combine(RepoPaths.Fixtures, "tasktor.rules-1", "spec-example.json"));
        _engine.SetRules(RulesLoader.Load(text, null));
        var onBattery = new RuleInputs().State("on_battery", true);
        onBattery.Groups["builds"] = new GroupStatus(2, 1);
        var fired = Eval(onBattery);
        Assert.Equal(["priority.set", "refits.pause"], fired.Select(o => o.Action!.Action));
        Assert.Equal("below_normal", fired[0].Action!.Args!["priority"]);
        Assert.Equal("builds", fired[0].Action!.Target!["group"]);
        Assert.All(fired, o => Assert.Equal(Origin.Rule, o.Action!.Origin));

        var plugged = new RuleInputs().State("on_battery", false);
        plugged.Groups["builds"] = new GroupStatus(2, 1);
        var undone = Eval(plugged);
        Assert.Equal(["refits.resume", "priority.restore"], undone.Select(o => o.Action!.Action)); // reverse order
        Assert.All(undone, o => Assert.True(o.IsUndo));
    }

    [Fact]
    public void Capture_blocks_added_by_rules_are_never_lifted_by_undo()
    {
        Load("""{ "rules": [ { "id": "call-capture", "when": { "state": "in_call" }, "then": [ { "capture_pause": "until_resume" } ], "undo": true } ] }""");
        Assert.Equal("capture.pause", Assert.Single(Eval(new RuleInputs().State("in_call", true))).Action!.Action);
        Assert.Empty(Eval(new RuleInputs().State("in_call", false)));
    }

    [Fact]
    public void Learned_rule_fires_per_job_with_its_basis()
    {
        Load("""
            { "rules": [ { "id": "silent", "when": { "learned": { "stat": "silence", "key": "job.kind", "fallback": "10m" } },
            "then": [ { "toast": "{job} has been quiet for {value} ({source})", "buttons": ["open:core.jobs", "job.log", "rule.its_fine"] } ] } ] }
            """);
        LearnedValue threshold = new(600, ValueSource.Default, 0, "silence.fallback", 600);
        RuleSubject Job(string id, double quiet) => new()
        {
            Id = id,
            Name = "train.py",
            Values = new Dictionary<string, double> { ["silence"] = quiet },
            Thresholds = new Dictionary<string, LearnedValue> { ["silence"] = threshold },
            Fields = new Dictionary<string, string> { ["job"] = "train.py" },
            Target = new Dictionary<string, string> { ["job"] = id },
        };
        var inputs = new RuleInputs();
        inputs.Jobs.Add(Job("j1", 660));
        inputs.Jobs.Add(Job("j2", 100));
        var out1 = Assert.Single(Eval(inputs));
        Assert.Equal("train.py has been quiet for 11 min (default)", out1.Toast!.Title);
        Assert.Equal("j1", out1.Toast.Buttons[1].Request.Target!["job"]);
        Assert.Equal("rule.its_fine", out1.Toast.Buttons[2].Request.Action);
    }

    [Fact]
    public void Event_rules_fire_once_per_event_with_a_run_button()
    {
        var text = File.ReadAllText(Path.Combine(RepoPaths.Fixtures, "tasktor.rules-1", "spec-example.json"));
        _engine.SetRules(RulesLoader.Load(text, null));
        var inputs = new RuleInputs();
        inputs.Events.Add(new RuleEvent { Name = "file.created", Id = "e1", Fields = new Dictionary<string, string> { ["folder"] = "/home/u/Downloads", ["file"] = "paper.pdf", ["path"] = "/home/u/Downloads/paper.pdf" } });
        inputs.Events.Add(new RuleEvent { Name = "file.created", Id = "e2", Fields = new Dictionary<string, string> { ["folder"] = "/home/u/Downloads", ["file"] = "song.mp3" } });
        inputs.Events.Add(new RuleEvent { Name = "file.created", Id = "e3", Fields = new Dictionary<string, string> { ["folder"] = "/tmp", ["file"] = "x.pdf" } });
        var fired = Assert.Single(Eval(inputs));
        Assert.Equal("New PDF: paper.pdf", fired.Toast!.Title);
        var run = Assert.Single(fired.Toast.Buttons);
        Assert.Equal("run", run.Request.Action);
        Assert.Equal("convert", run.Request.Target!["command"]);
        Assert.Equal("/home/u/Downloads/paper.pdf", run.Request.Args!["path"]);
        Assert.Empty(Eval(inputs.Events.Count > 0 ? new RuleInputs() : inputs));
    }

    [Fact]
    public void Combinators_and_value_states()
    {
        Load("""
            { "rules": [ { "id": "quiet-games", "when": { "all": [ { "not": { "state": "fullscreen", "is": "none" } }, { "any": [ { "state": "on_battery" }, { "metric": "system.cpu_pct", "instance": "", "above": 90 } ] } ] },
            "then": [ { "pause": "refits" } ] } ] }
            """);
        Assert.Empty(Eval(new RuleInputs().State("fullscreen", "none").State("on_battery", true)));
        Assert.Single(Eval(new RuleInputs().State("fullscreen", "exclusive").State("on_battery", true)));
        _engine.Reset();
        Assert.Single(Eval(new RuleInputs().State("fullscreen", "borderless").Metric("system.cpu_pct", 95)));
    }

    [Fact]
    public void Busy_count_and_process_conditions()
    {
        Load("""
            { "groups": { "agents": ["claude.exe"] }, "rules": [
            { "id": "overlap", "when": { "group": "agents", "busy_count_above": 2 }, "then": [ { "toast": "{count} agents are busy" } ] },
            { "id": "py", "when": { "process": "python*.exe", "cpu_above": 4, "age_above": "10m" }, "then": [ { "efficiency": true } ], "undo": true } ] }
            """);
        var inputs = new RuleInputs();
        inputs.Groups["agents"] = new GroupStatus(5, 3);
        inputs.Processes.Add(new RuleSubject { Id = "p1", Name = "python.exe", Values = new Dictionary<string, double> { ["cores"] = 6, ["age_s"] = 900 }, Target = new Dictionary<string, string> { ["pid"] = "1" } });
        inputs.Processes.Add(new RuleSubject { Id = "p2", Name = "python.exe", Values = new Dictionary<string, double> { ["cores"] = 6, ["age_s"] = 60 } });
        inputs.Processes.Add(new RuleSubject { Id = "p3", Name = "node.exe", Values = new Dictionary<string, double> { ["cores"] = 9, ["age_s"] = 9000 } });
        var outs = Eval(inputs);
        Assert.Equal("3 agents are busy", outs.Single(o => o.RuleId == "overlap").Toast!.Title);
        var eff = outs.Single(o => o.RuleId == "py").Action!;
        Assert.Equal("efficiency.set", eff.Action);
        Assert.Equal("1", eff.Target!["pid"]);

        // the process exits: its subject goes away and undo runs
        inputs.Processes.RemoveAt(0);
        var undo = Eval(inputs).Single(o => o.RuleId == "py");
        Assert.Equal("efficiency.restore", undo.Action!.Action);
        Assert.True(undo.IsUndo);
    }

    [Fact]
    public void Removing_a_rule_on_reload_undoes_its_effects()
    {
        Load("""{ "rules": [ { "id": "r", "when": { "state": "on_battery" }, "then": [ { "pause": "refits" } ], "undo": true } ] }""");
        Eval(new RuleInputs().State("on_battery", true));
        var outs = _engine.SetRules(RuleSet.Empty);
        Assert.Equal("refits.resume", Assert.Single(outs).Action!.Action);
    }

    [Fact]
    public void Placeholders_fill_known_folders_and_leave_unknown_names()
    {
        var text = Placeholders.Fill("{Downloads}/x {file} {nope}", new Dictionary<string, string> { ["file"] = "a.pdf" }, n => n == "Downloads" ? "/d" : null);
        Assert.Equal("/d/x a.pdf {nope}", text);
    }
}
