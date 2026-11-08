using System.Diagnostics;
using System.Text.Json;
using Tasktor.Contracts;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Plugins;
using Tasktor.Contracts.Snapshot;
using Tasktor.Core.Loop;
using Tasktor.Core.Plugins;
using Tasktor.Core.Storage;
using Tasktor.Tests.Shared;

namespace Tasktor.Core.Tests;

public sealed class PluginHostTests : IDisposable
{
    private static readonly string Python = FindPython();
    private readonly TempStore _store = new();
    private readonly FakeClock _clock = new();
    private readonly LogicLoop _loop;
    private readonly ConsentStore _consents;
    private readonly PluginHost _host;

    public PluginHostTests()
    {
        _loop = new LogicLoop(_clock);
        _consents = new ConsentStore(_store.Layout);
        _host = new PluginHost(_store.Layout, _consents, new PortableProcessControl(new ChildProcessRunner()), _loop.Post)
        {
            PythonPath = Python,
            SecretProtection = new KeyProtection(b => [.. b.Reverse()], b => [.. b.Reverse()], "test"),
        };
    }

    public void Dispose() => _store.Dispose();

    private static string FindPython()
    {
        foreach (var name in OperatingSystem.IsWindows() ? new[] { "python.exe", "py.exe" } : ["python3", "python"])
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
            {
                var p = Path.Combine(dir, name);
                if (File.Exists(p))
                {
                    return p;
                }
            }
        }

        return "python3";
    }

    private const string Echo = """
        import json, sys
        req = json.load(sys.stdin)
        print(json.dumps({"contract": 1, "cards": [{"id": "main", "title": "T", "blocks": [{"kind": "Metric", "priority": 1, "label": "n", "value": 1}]}]}))
        """;

    private string Plugin(string id, string script, string interval = "5m", string timeout = "10s", string? actions = null, string? settings = null, string? dir = null)
    {
        var folder = Path.Combine(dir ?? _store.Layout.PluginsDir, id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "main.py"), script);
        File.WriteAllText(Path.Combine(folder, "plugin.json"), $$"""
            { "contract": 1, "id": "{{id}}", "name": "Test {{id}}", "version": "0.1.0", "command": ["{python}", "main.py"],
              "interval": "{{interval}}", "timeout": "{{timeout}}", "reads": ["nothing"], "network": [],
              "settings": {{settings ?? "[]"}}, "actions": {{actions ?? "[]"}} }
            """);
        return folder;
    }

    private PluginRunResult Run(string id) => LoopPump.Run(_loop, _host.RunNow(id, _clock.UtcNow), 30000);

    private PluginEntry Entry(string id) => _host.Find(id)!;

    [Fact]
    public void A_new_folder_is_found_and_off()
    {
        Plugin("p1", Echo);
        _host.Discover(_clock.UtcNow);
        Assert.Equal(PluginLifecycle.Found, Entry("p1").State);
        Assert.Equal(PluginState.Found, Assert.Single(_host.Statuses()).State);
        Assert.Empty(_host.Cards());
        Assert.Equal(0, _host.Tick(_clock.UtcNow));
    }

    [Fact]
    public void Consent_shows_the_exact_command_and_enabling_runs_it()
    {
        Plugin("p1", Echo);
        _host.Discover(_clock.UtcNow);
        var view = _host.ConsentView("p1")!;
        Assert.Contains("main.py", view.CommandLine, StringComparison.Ordinal);
        Assert.Contains(Python, view.CommandLine, StringComparison.Ordinal);
        Assert.Equal(["nothing"], view.Reads);
        Assert.True(_host.Enable("p1", _clock.UtcNow));
        Assert.True(Run("p1").Ok);
        var card = Assert.Single(_host.Cards());
        Assert.Equal("plugin.p1.main", card.Id);
        Assert.Equal(CardSourceKind.Plugin, card.Source!.Kind);
        Assert.Equal("Test p1", card.Source.Name);
        Assert.Equal(900, card.StaleAfterS);
    }

    [Fact]
    public void Any_change_to_the_files_asks_again()
    {
        var dir = Plugin("p1", Echo);
        _host.Discover(_clock.UtcNow);
        _host.Enable("p1", _clock.UtcNow);
        _host.Discover(_clock.UtcNow);
        Assert.Equal(PluginLifecycle.On, Entry("p1").State);
        File.AppendAllText(Path.Combine(dir, "main.py"), "\n# changed\n");
        _host.Discover(_clock.UtcNow);
        Assert.Equal(PluginLifecycle.Found, Entry("p1").State);
        Assert.True(Entry("p1").ChangedSinceConsent);

        var fresh = new PluginHost(_store.Layout, _consents, new PortableProcessControl(new ChildProcessRunner()), _loop.Post) { PythonPath = Python };
        _host.Enable("p1", _clock.UtcNow);
        _consents.Load();
        fresh.Discover(_clock.UtcNow);
        Assert.Equal(PluginLifecycle.On, fresh.Find("p1")!.State); // the consent persisted with the new hashes
    }

    [Fact]
    public void A_hung_plugin_is_killed_at_its_timeout()
    {
        Plugin("slow", "import time\ntime.sleep(30)\n", timeout: "1s");
        _host.Discover(_clock.UtcNow);
        _host.Enable("slow", _clock.UtcNow);
        var sw = Stopwatch.StartNew();
        var r = Run("slow");
        Assert.False(r.Ok);
        Assert.Contains("timed out", r.Error, StringComparison.Ordinal);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), "killed at the timeout, not after the sleep");
    }

    [Fact]
    public void Three_failures_in_a_row_turn_it_off_with_an_error_card()
    {
        Plugin("bad", "print('not json')\n");
        _host.Discover(_clock.UtcNow);
        _host.Enable("bad", _clock.UtcNow);
        for (var i = 0; i < 3; i++)
        {
            var r = Run("bad");
            Assert.False(r.Ok);
            Assert.Contains("not valid", r.Error, StringComparison.Ordinal);
        }

        Assert.Equal(PluginLifecycle.Error, Entry("bad").State);
        var card = Assert.Single(_host.Cards());
        Assert.Equal("plugin.bad.error", card.Id);
        Assert.Equal(PluginState.Error, _host.Statuses()[0].State);
        _clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, _host.Tick(_clock.UtcNow));
    }

    [Fact]
    public void A_success_resets_the_failure_count()
    {
        var dir = Plugin("flaky", "import sys\nsys.exit(3)\n");
        _host.Discover(_clock.UtcNow);
        _host.Enable("flaky", _clock.UtcNow);
        var r = Run("flaky");
        Assert.Contains("exited with code 3", r.Error, StringComparison.Ordinal);
        Run("flaky");
        File.WriteAllText(Path.Combine(dir, "main.py"), Echo);
        _host.Enable("flaky", _clock.UtcNow); // consent for the new hash
        Assert.True(Run("flaky").Ok);
        Assert.Equal(0, Entry("flaky").Failures);
    }

    [Fact]
    public void Output_over_64_kb_is_rejected()
    {
        Plugin("big", "import sys\nsys.stdout.write('x' * 70000)\n");
        _host.Discover(_clock.UtcNow);
        _host.Enable("big", _clock.UtcNow);
        Assert.Contains("64 KB", Run("big").Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Stderr_tail_is_kept_for_the_error_card()
    {
        Plugin("noisy", "import sys\nsys.stderr.write('boom ' * 2000)\nsys.exit(1)\n");
        _host.Discover(_clock.UtcNow);
        _host.Enable("noisy", _clock.UtcNow);
        Run("noisy");
        Assert.InRange(Entry("noisy").StderrTail!.Length, 1, ChildProcessRunner.StderrTailBytes);
    }

    [Fact]
    public void The_plugin_gets_settings_data_dir_and_snapshot_path_but_not_pythonpath()
    {
        Environment.SetEnvironmentVariable("PYTHONPATH", "/should/not/leak");
        try
        {
            Plugin("env", """
                import json, os, sys, pathlib
                req = json.load(sys.stdin)
                pathlib.Path(req["data_dir"], "seen.json").write_text(json.dumps({"req": req, "pythonpath": os.environ.get("PYTHONPATH")}))
                print(json.dumps({"contract": 1, "cards": []}))
                """, settings: """[ { "key": "url", "label": "URL", "type": "secret" }, { "key": "file", "label": "File", "type": "path" } ]""");
            _host.Discover(_clock.UtcNow);
            _host.Enable("env", _clock.UtcNow);
            _host.SetSetting("env", "url", "https://example.com/secret.ics");
            _host.SetSetting("env", "file", "notes.txt");
            Assert.DoesNotContain("secret.ics", File.ReadAllText(Path.Combine(_store.Layout.PluginData("env"), "settings.json")), StringComparison.Ordinal);
            Assert.True(Run("env").Ok);
            using var seen = JsonDocument.Parse(File.ReadAllText(Path.Combine(_store.Layout.PluginData("env"), "seen.json")));
            Assert.Equal(JsonValueKind.Null, seen.RootElement.GetProperty("pythonpath").ValueKind);
            var req = seen.RootElement.GetProperty("req");
            Assert.Equal("render", req.GetProperty("call").GetString());
            Assert.Equal("https://example.com/secret.ics", req.GetProperty("settings").GetProperty("url").GetString());
            Assert.Equal(_store.Layout.Snapshot, req.GetProperty("snapshot_path").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PYTHONPATH", null);
        }
    }

    [Fact]
    public void Only_declared_actions_call_the_plugin_back()
    {
        Plugin("act", """
            import json, sys, pathlib
            req = json.load(sys.stdin)
            if req["call"] == "action":
                pathlib.Path(req["data_dir"], "acted").write_text(req["action"])
            print(json.dumps({"contract": 1, "cards": [{"id": "main", "title": "T", "blocks": [{"kind": "Actions", "priority": 1, "actions": ["refresh"]}], "actions": [{"id": "refresh", "label": "Refresh", "priority": 1}]}]}))
            """, actions: """[ { "id": "refresh", "label": "Refresh", "class": "reversible" } ]""");
        _host.Discover(_clock.UtcNow);
        _host.Enable("act", _clock.UtcNow);
        Assert.True(Run("act").Ok);
        var action = _host.Cards()[0].Actions![0];
        Assert.Equal("plugin.action", action.Action);
        Assert.Equal("act", action.Target!["plugin"]);
        Assert.True(LoopPump.Run(_loop, _host.Act("act", "refresh", null, _clock.UtcNow), 30000).Ok);
        Assert.Equal("refresh", File.ReadAllText(Path.Combine(_store.Layout.PluginData("act"), "acted")));
        Assert.False(LoopPump.Run(_loop, _host.Act("act", "format_disk", null, _clock.UtcNow)).Ok);
    }

    [Fact]
    public void Scheduled_runs_follow_the_interval_and_manual_runs_once()
    {
        Plugin("every", Echo, interval: "30s");
        Plugin("manual", Echo, interval: "manual");
        _host.Discover(_clock.UtcNow);
        _host.Enable("every", _clock.UtcNow);
        _host.Enable("manual", _clock.UtcNow);
        Assert.Equal(2, _host.Tick(_clock.UtcNow));
        AgentlessWait(() => !Entry("every").Running && !Entry("manual").Running);
        Assert.Equal(0, _host.Tick(_clock.UtcNow));
        _clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(1, _host.Tick(_clock.UtcNow)); // only the 30 s one
        AgentlessWait(() => !Entry("every").Running);
    }

    private void AgentlessWait(Func<bool> done)
    {
        var sw = Stopwatch.StartNew();
        while (!done())
        {
            _loop.RunPending();
            Thread.Sleep(5);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), "timed out");
        }
    }

    [Fact]
    public void Packs_get_one_consent_and_their_rules_start_unticked()
    {
        var pack = Path.Combine(_store.Layout.PluginsDir, "my-pack");
        Directory.CreateDirectory(pack);
        File.WriteAllText(Path.Combine(pack, "pack.json"), """{ "contract": 1, "id": "my-pack", "name": "My pack", "version": "1.0", "plugins": ["a", "b"], "rules": "rules.pack.json" }""");
        File.WriteAllText(Path.Combine(pack, "rules.pack.json"), """{ "rules": [ { "id": "pack-r1", "when": { "state": "on_battery" }, "then": [ { "pause": "refits" } ] }, { "id": "pack-r2", "when": { "state": "in_call" }, "then": [ { "pause": "refits" } ] } ] }""");
        Plugin("a", Echo, dir: pack);
        Plugin("b", Echo, dir: pack);
        _host.Discover(_clock.UtcNow);
        Assert.Single(_host.Packs);
        Assert.Null(_host.TickedPackRules((_, _) => true)); // pack not enabled yet
        Assert.Equal(2, _host.EnablePack("my-pack", _clock.UtcNow));
        Assert.All(_host.Plugins, p => Assert.Equal(PluginLifecycle.On, p.State));
        Assert.Null(_host.TickedPackRules((_, _) => false)); // off until ticked
        var text = _host.TickedPackRules((_, rule) => rule == "pack-r2")!;
        var set = Rules.RulesLoader.Load(null, null, null, [text]);
        Assert.Equal("pack-r2", Assert.Single(set.Rules).Id);
        Assert.Equal(Rules.RuleOrigin.Pack, set.Rules[0].Origin);
    }

    [Theory]
    [InlineData("plugins-builtin/tasktor.calendar")]
    [InlineData("plugins-builtin/tasktor.repos")]
    [InlineData("examples/plugins/todo-count")]
    public void Every_shipped_plugin_passes_the_agents_own_manifest_check(string folder)
    {
        // The schema alone missed a 120 s timeout the agent refuses (max 60 s); this is the check the agent runs.
        var r = PluginCheck.Manifest(Path.Combine(RepoPaths.Root, folder), new Version(1, 0));
        Assert.True(r.Problem is null, folder + ": " + r.Problem);
        Assert.Contains(r.Consent, line => line.StartsWith("Runs: ", StringComparison.Ordinal));
    }

    [Fact]
    public void The_example_plugin_works_through_the_host()
    {
        var src = Path.Combine(RepoPaths.Root, "examples", "plugins", "todo-count");
        var dst = Path.Combine(_store.Layout.PluginsDir, "todo-count");
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
        {
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
        }

        var notes = Path.Combine(_store.Root, "todo.txt");
        File.WriteAllText(notes, "TODO: write docs\n- [ ] fix login\n- [x] done already\nplain line\n");
        _host.Discover(_clock.UtcNow);
        Assert.Null(Entry("todo-count").Problem);
        _host.Enable("todo-count", _clock.UtcNow);
        _host.SetSetting("todo-count", "file", notes);
        var r = Run("todo-count");
        Assert.True(r.Ok, r.Error);
        var card = Assert.Single(_host.Cards());
        Assert.Equal("2", ((MetricBlock)card.Blocks[0]).Value.Raw);
        Assert.Contains("fix login", ((ListBlock)card.Blocks[1]).Items.Raw, StringComparison.Ordinal);
    }
}

public sealed class PluginValidationTests
{
    private static PluginManifest M(string interval = "5m", string timeout = "10s", string id = "p", string? actions = null)
    {
        var json = $$"""{ "contract": 1, "id": "{{id}}", "name": "P", "version": "1", "command": ["x"], "interval": "{{interval}}", "timeout": "{{timeout}}", "reads": [], "network": [], "actions": {{actions ?? "[]"}} }""";
        return JsonSerializer.Deserialize(json, ContractsJsonContext.Default.PluginManifest)!;
    }

    [Theory]
    [InlineData("10s", "10s", "p", null, "interval")]
    [InlineData("5m", "90s", "p", null, "timeout")]
    [InlineData("5m", "10s", "Bad Id", null, "id must be")]
    [InlineData("5m", "10s", "p", """[ { "id": "nuke", "label": "Nuke", "class": "destructive" } ]""", "destructive")]
    public void Bad_manifests_are_invalid(string interval, string timeout, string id, string? actions, string reason)
    {
        var problem = PluginValidation.CheckManifest(M(interval, timeout, id, actions), id, new Version(1, 0));
        Assert.Contains(reason, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Id_must_match_the_folder() =>
        Assert.Contains("folder name", PluginValidation.CheckManifest(M(), "other", new Version(1, 0)), StringComparison.Ordinal);

    [Fact]
    public void Newer_min_tasktor_is_refused()
    {
        var m = M() with { MinTasktor = "9.0" };
        Assert.Contains("needs Tasktor", PluginValidation.CheckManifest(m, "p", new Version(1, 0)), StringComparison.Ordinal);
    }

    public static TheoryData<string, string> BadOutputs() => new()
    {
        { """{"contract":1,"cards":[{"id":"a","title":"t","blocks":[{"kind":"Tree","priority":1,"table":"processes"}]}]}""", "Tree" },
        { """{"contract":1,"cards":[{"id":"a","title":"t","blocks":[{"kind":"Alert","priority":1,"text":"x"}]}]}""", "Alert" },
        { """{"contract":1,"cards":[{"id":"a","title":"t","blocks":[{"kind":"Metric","priority":1,"label":"x","value":{"bind":"/state/system/cpu_pct"}}]}]}""", "literal" },
        { """{"contract":1,"cards":[{"id":"a","title":"t","blocks":[]},{"id":"b","title":"t","blocks":[]},{"id":"c","title":"t","blocks":[]},{"id":"d","title":"t","blocks":[]}]}""", "more than 3 cards" },
        { """{"contract":1,"cards":[{"id":"a","title":"t","blocks":[],"actions":[{"id":"r","label":"r"},{"id":"r","label":"r"},{"id":"r","label":"r"},{"id":"r","label":"r"}]}]}""", "more than 3 actions" },
        { """{"contract":1,"cards":[{"id":"a","title":"t","blocks":[],"actions":[{"id":"nope","label":"x"}]}]}""", "not declared" },
        { """{"contract":1,"cards":[{"id":"a","title":"t","blocks":[{"kind":"Sparkline","priority":1,"series":"system.cpu_pct"}]}]}""", "points" },
        { """{"contract":1,"cards":[{"id":"a","title":"t","blocks":[{"kind":"Sparkline","priority":1,"points":[""" + string.Join(",", Enumerable.Range(0, 61)) + """]}]}]}""", "60 points" },
        { """{"contract":1,"cards":[{"id":"a","title":"t","blocks":[{"kind":"Table","priority":1,"columns":[{"key":"k","label":"K"}],"rows":[""" + string.Join(",", Enumerable.Range(0, 13).Select(i => "{\"k\":" + i + "}")) + """]}]}]}""", "12 rows" },
        { """{"contract":2,"cards":[]}""", "contract 2" },
        { """{"contract":1,"cards":[{"id":"../x","title":"t","blocks":[]}]}""", "not allowed" },
        { """{"contract":1,"cards":[{"id":"a","title":"t","blocks":[{"kind":"Media"}]}]}""", "not valid" },
    };

    [Theory]
    [MemberData(nameof(BadOutputs))]
    public void Output_outside_the_subset_is_rejected(string output, string reason)
    {
        var (cards, error) = PluginValidation.CheckOutput(M(actions: """[ { "id": "r", "label": "R", "class": "read" } ]"""), output, DateTimeOffset.UnixEpoch);
        Assert.Null(cards);
        Assert.Contains(reason, error, StringComparison.Ordinal);
    }
}
