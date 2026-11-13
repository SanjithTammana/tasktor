using System.Diagnostics;
using System.Text.Json;
using Tasktor.Contracts;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Plugins;
using Tasktor.Contracts.Snapshot;
using Tasktor.Core.Loop;
using Tasktor.Core.Plugins;
using Tasktor.Core.Storage;
using Tasktor.Tests.Shared;

namespace Tasktor.Core.Tests;

/// <summary>Plugin contract 2 in the host (P1 spec): reading, consent, wakes, output limits, notices, state, requests.</summary>
public sealed class PluginHost2Tests : IDisposable
{
    private readonly TempStore _store = new();
    private readonly FakeClock _clock = new();
    private readonly LogicLoop _loop;
    private readonly ConsentStore _consents;
    private readonly ScriptedWorkers _workers = new();
    private readonly PluginHost _host;
    private readonly List<(string Plugin, PluginNotice Notice)> _notices = [];
    private readonly FakeRequests _requests = new();

    public PluginHost2Tests()
    {
        _loop = new LogicLoop(_clock);
        _consents = new ConsentStore(_store.Layout);
        _host = new PluginHost(_store.Layout, _consents, _workers, _loop.Post) { PythonPath = "python" };
        _host.Requests = _requests;
        _host.Notice += (p, n) => _notices.Add((p, n));
    }

    public void Dispose() => _store.Dispose();

    private const string Caps = """["page", "notify", "timer", "state", "events:process", "act:job", "browser:block"]""";

    private void Plugin(string id = "focus", string caps = Caps, string extra = "", string actions = """[{ "id": "skip", "label": "Skip", "class": "reversible" }]""")
    {
        var dir = Path.Combine(_store.Layout.PluginsDir, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "main.py"), "# test");
        File.WriteAllText(Path.Combine(dir, "plugin.json"), $$"""
            { "contract": 2, "id": "{{id}}", "name": "Focus", "version": "2.0.0", "command": ["{python}", "main.py"],
              "timeout": "10s", "reads": ["nothing"], "network": [], "capabilities": {{caps}}, "actions": {{actions}} {{extra}} }
            """);
    }

    private PluginEntry Entry(string id = "focus") => _host.Find(id)!;

    private PluginInput2 LastInput() => JsonSerializer.Deserialize(_workers.Stdins[^1], ContractsJsonContext.Strict.PluginInput2)!;

    /// <summary>Discover and turn on; <paramref name="granted"/> null ticks everything the manifest asks.</summary>
    private void On(string id = "focus", IReadOnlyCollection<string>? granted = null)
    {
        _host.Discover(_clock.UtcNow);
        Assert.Null(Entry(id).Problem);
        granted ??= Entry(id).V2!.Capabilities!.Select(PluginCapabilityText.Name).ToList();
        Assert.True(_host.Enable(id, _clock.UtcNow, granted));
    }

    /// <summary>Tick, then pump the loop until every started run has finished.</summary>
    private int TickAndWait()
    {
        var started = _host.Tick(_clock.UtcNow);
        var sw = Stopwatch.StartNew();
        while (_loop.RunPending() > 0 || _host.Plugins.Any(p => p.Running))
        {
            Thread.Sleep(1);
            Assert.True(sw.ElapsedMilliseconds < 5000, "run did not finish");
        }

        Thread.Sleep(20); // request results post back from the thread pool
        _loop.RunPending();

        return started;
    }

    private void Output(string json) => _workers.Outputs.Enqueue(json);

    // ───────── reading ─────────

    [Fact]
    public void A_contract_2_manifest_loads_with_manual_interval_and_capabilities()
    {
        Plugin();
        _host.Discover(_clock.UtcNow);
        Assert.Null(Entry().Problem);
        Assert.Equal("manual", Entry().Manifest!.Interval);
        Assert.Equal(7, Entry().V2!.Capabilities!.Count);
    }

    [Fact]
    public void A_misspelled_field_is_refused_with_the_closest_name()
    {
        Plugin(extra: """, "watch_exe": ["python.exe"]""");
        _host.Discover(_clock.UtcNow);
        Assert.Equal(PluginLifecycle.Invalid, Entry().State);
        Assert.Equal("plugin.json: Unknown field 'watch_exe'. Did you mean 'watch_exes'?", Entry().Problem);
    }

    [Fact]
    public void A_misspelled_capability_is_refused_with_the_closest_name()
    {
        Plugin(caps: """["notfy"]""");
        _host.Discover(_clock.UtcNow);
        Assert.Equal("plugin.json: Unknown capability 'notfy'. Did you mean 'notify'?", Entry().Problem);
    }

    [Fact]
    public void A_typo_inside_an_action_is_refused_too()
    {
        Plugin(actions: """[{ "id": "skip", "label": "Skip", "clas": "reversible" }]""");
        _host.Discover(_clock.UtcNow);
        Assert.StartsWith("plugin.json: actions: Unknown field 'clas'. Did you mean 'class'?", Entry().Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_resident_plugin_is_not_allowed_yet()
    {
        Plugin(caps: """["resident"]""");
        _host.Discover(_clock.UtcNow);
        Assert.Equal(PluginLifecycle.Invalid, Entry().State);
    }

    [Fact]
    public void A_newer_contract_asks_for_a_newer_Tasktor()
    {
        Plugin();
        var path = Path.Combine(_store.Layout.PluginsDir, "focus", "plugin.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"contract\": 2", "\"contract\": 3", StringComparison.Ordinal));
        _host.Discover(_clock.UtcNow);
        Assert.Equal("This plugin needs a newer Tasktor (contract 3)", Entry().Problem);
    }

    // ───────── consent ─────────

    [Fact]
    public void Consent_lists_one_plain_line_per_capability()
    {
        Plugin(caps: """["events:process", "act:job"]""", extra: """, "watch_exes": ["python.exe", "node.exe"]""");
        _host.Discover(_clock.UtcNow);
        var lines = _host.ConsentView("focus")!.Capabilities!;
        Assert.Equal(["events:process", "act:job"], lines.Select(l => l.Name));
        Assert.Equal("See when python.exe or node.exe starts and stops", lines[0].Line);
    }

    [Fact]
    public void Unticked_capabilities_are_not_granted_or_sent()
    {
        Plugin();
        On(granted: ["page", "timer", "not-asked"]);
        Assert.Equal(new HashSet<PluginCapability> { PluginCapability.Page, PluginCapability.Timer }, _host.Granted("focus"));
        Output("""{ "contract": 2 }""");
        TickAndWait();
        Assert.Equal([PluginCapability.Page, PluginCapability.Timer], LastInput().Granted);
    }

    [Fact]
    public void Contract_2_needs_an_explicit_grant()
    {
        Plugin();
        _host.Discover(_clock.UtcNow);
        Assert.False(_host.Enable("focus", _clock.UtcNow));
        Assert.Equal(PluginLifecycle.Found, Entry().State);
        Assert.True(_host.Enable("focus", _clock.UtcNow, []));
        Assert.Empty(_host.Granted("focus"));
    }

    [Fact]
    public void Turning_off_grants_nothing()
    {
        Plugin();
        On();
        Assert.Equal(7, _host.Granted("focus").Count);
        _host.Disable("focus");
        Assert.Empty(_host.Granted("focus"));
    }

    [Fact]
    public void A_capability_this_build_cannot_serve_is_refused_at_load()
    {
        _host.Unsupported = new HashSet<PluginCapability> { PluginCapability.EventsProcess };
        Plugin();
        _host.Discover(_clock.UtcNow);
        Assert.Equal(PluginLifecycle.Invalid, Entry().State);
        Assert.Equal("Needs events:process, which this version of Tasktor does not support yet", Entry().Problem);
    }

    [Fact]
    public void A_contract_that_is_not_a_number_is_refused()
    {
        Plugin();
        var path = Path.Combine(_store.Layout.PluginsDir, "focus", "plugin.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"contract\": 2", "\"contract\": \"2\"", StringComparison.Ordinal));
        _host.Discover(_clock.UtcNow);
        Assert.Equal("plugin.json: contract must be a number", Entry().Problem);
    }

    // ───────── in flight ─────────

    [Fact]
    public void A_run_that_finishes_after_the_plugin_is_turned_off_is_discarded()
    {
        Plugin();
        On();
        var gate = _workers.Hold();
        Output("""{ "contract": 2, "notify": [ { "id": "a", "title": "Late" } ], "page": { "title": "Late", "sections": [] } }""");
        Assert.Equal(1, _host.Tick(_clock.UtcNow));
        _host.Disable("focus");
        gate.SetResult();
        PumpUntil(() => !Entry().Running);
        Assert.Empty(_notices);
        Assert.Null(_host.Page("focus"));
        Assert.Equal(0, Entry().Failures);
    }

    [Fact]
    public void A_request_result_for_an_older_consent_is_dropped()
    {
        Plugin();
        On();
        var answer = _requests.Hold();
        Output("""{ "contract": 2, "requests": [ { "kind": "job.pause", "id": "p1", "job": "j1" } ] }""");
        TickAndWait();
        Assert.Single(_requests.Seen);
        _host.Pause("focus", true, _clock.UtcNow);
        _host.Pause("focus", false, _clock.UtcNow);
        answer.SetResult(new PluginRequestResult { Id = "p1", Status = PluginRequestStatus.Ok });
        Thread.Sleep(20);
        _loop.RunPending();
        Assert.Empty(Entry().PendingResults);
    }

    [Fact]
    public void Waiting_requests_are_capped()
    {
        Plugin();
        On();
        _requests.Hold();
        const string five = """
            { "contract": 2, "requests": [ { "kind": "job.resume", "id": "a", "job": "j" }, { "kind": "job.resume", "id": "b", "job": "j" },
              { "kind": "job.resume", "id": "c", "job": "j" }, { "kind": "job.resume", "id": "d", "job": "j" }, { "kind": "job.resume", "id": "e", "job": "j" } ] }
            """;
        for (var i = 0; i < 3; i++)
        {
            Output(five);
            LoopPump.Run(_loop, _host.RunNow("focus", _clock.UtcNow));
        }

        Assert.Equal(PluginHost.MaxOutstandingRequests, _requests.Seen.Count);
        Assert.Equal(5, Entry().PendingResults.Count(r => r.Message == "Too many requests are still waiting"));
    }

    [Fact]
    public void A_run_from_a_removed_folder_does_not_land_on_its_replacement()
    {
        Plugin();
        On();
        var gate = _workers.Hold();
        Output("""{ "contract": 2, "notify": [ { "id": "a", "title": "Old" } ] }""");
        Assert.Equal(1, _host.Tick(_clock.UtcNow));
        var old = Entry();
        Directory.Delete(Path.Combine(_store.Layout.PluginsDir, "focus"), recursive: true);
        _host.Discover(_clock.UtcNow);
        Plugin();
        _host.Discover(_clock.UtcNow);
        Assert.NotSame(old, Entry());
        gate.SetResult();
        PumpUntil(() => !old.Running);
        Assert.Empty(_notices);
    }

    [Fact]
    public void A_narrower_consent_drops_what_the_old_one_set_up()
    {
        Plugin();
        On();
        Output($$"""
            { "contract": 2, "wake_at": "{{_clock.UtcNow.AddMinutes(5):O}}", "state": { "n": 1 }, "page": { "title": "P", "sections": [ { "form": { "id": "f",
              "submit_label": "Save", "fields": [ { "key": "k", "label": "K", "type": "string" } ] } } ] } }
            """);
        TickAndWait();
        Assert.NotNull(_host.Page("focus"));
        Assert.True(_host.Enable("focus", _clock.UtcNow, ["notify"]));
        Assert.Null(_host.Page("focus"));
        Assert.Null(Entry().WakeAt);
        Assert.False(LoopPump.Run(_loop, _host.Submit("focus", "f", new Dictionary<string, string> { ["k"] = "v" }, _clock.UtcNow)).Ok);
        Output("""{ "contract": 2 }""");
        TickAndWait();
        Assert.Null(LastInput().State);
    }

    [Fact]
    public void Pausing_does_not_reset_the_waiting_request_cap()
    {
        Plugin();
        On();
        _requests.Hold();
        const string five = """
            { "contract": 2, "requests": [ { "kind": "job.resume", "id": "a", "job": "j" }, { "kind": "job.resume", "id": "b", "job": "j" },
              { "kind": "job.resume", "id": "c", "job": "j" }, { "kind": "job.resume", "id": "d", "job": "j" }, { "kind": "job.resume", "id": "e", "job": "j" } ] }
            """;
        for (var i = 0; i < 2; i++)
        {
            Output(five);
            LoopPump.Run(_loop, _host.RunNow("focus", _clock.UtcNow));
        }

        _host.Pause("focus", true, _clock.UtcNow);
        _host.Pause("focus", false, _clock.UtcNow);
        Output(five);
        LoopPump.Run(_loop, _host.RunNow("focus", _clock.UtcNow));
        Assert.Equal(PluginHost.MaxOutstandingRequests, _requests.Seen.Count);

        // Nor does removing the folder and adding it back.
        Directory.Delete(Path.Combine(_store.Layout.PluginsDir, "focus"), recursive: true);
        _host.Discover(_clock.UtcNow);
        Plugin();
        On();
        Output(five);
        LoopPump.Run(_loop, _host.RunNow("focus", _clock.UtcNow));
        Assert.Equal(PluginHost.MaxOutstandingRequests, _requests.Seen.Count);
    }

    [Fact]
    public void Turning_a_manual_plugin_on_again_renders_it()
    {
        Plugin();
        On();
        Output("""{ "contract": 2 }""");
        Assert.Equal(1, TickAndWait());
        _host.Disable("focus");
        On();
        Output("""{ "contract": 2 }""");
        Assert.Equal(1, TickAndWait());
        Assert.Equal(PluginCall2.Render, LastInput().Call);
        Assert.Equal(0, TickAndWait());
    }

    [Fact]
    public void The_call_that_waited_longest_goes_first()
    {
        Plugin();
        On();
        Output($$"""{ "contract": 2, "wake_at": "{{_clock.UtcNow.AddSeconds(5):O}}" }""");
        TickAndWait();
        _host.Deliver(new ProcessStartedEvent { At = _clock.UtcNow, Exe = "python.exe", Pid = 1 }, _clock.UtcNow); // due at +2 s
        _clock.Advance(TimeSpan.FromSeconds(10));
        Output("""{ "contract": 2 }""");
        Assert.Equal(1, TickAndWait());
        Assert.Equal(PluginCall2.Event, LastInput().Call);
        Output("""{ "contract": 2 }""");
        Assert.Equal(1, TickAndWait());
        Assert.Equal(PluginCall2.Timer, LastInput().Call);
    }

    // ───────── wakes ─────────

    [Fact]
    public void First_run_renders_then_the_timer_wakes_it()
    {
        Plugin();
        On();
        Output($$"""{ "contract": 2, "wake_at": "{{_clock.UtcNow.AddMinutes(25):O}}" }""");
        Assert.Equal(1, TickAndWait());
        Assert.Equal(PluginCall2.Render, LastInput().Call);
        Assert.Equal(_clock.UtcNow.AddMinutes(25), _host.NextDue);

        _clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(0, TickAndWait());
        _clock.Advance(TimeSpan.FromMinutes(15));
        Output("""{ "contract": 2 }""");
        Assert.Equal(1, TickAndWait());
        Assert.Equal(PluginCall2.Timer, LastInput().Call);
        Assert.Null(Entry().WakeAt);
    }

    [Fact]
    public void Events_are_batched_for_two_seconds_and_capped()
    {
        Plugin();
        On();
        Output("""{ "contract": 2 }""");
        TickAndWait();
        for (var i = 0; i < 105; i++)
        {
            _host.Deliver(new ProcessStartedEvent { At = _clock.UtcNow, Exe = "python.exe", Pid = (uint)i }, _clock.UtcNow);
        }

        Assert.Equal(0, TickAndWait());
        _clock.Advance(PluginHost.EventBatch);
        Output("""{ "contract": 2 }""");
        Assert.Equal(1, TickAndWait());
        var input = LastInput();
        Assert.Equal(PluginCall2.Event, input.Call);
        Assert.Equal(100, input.Events!.Count);
        Assert.Equal(5, input.EventsDropped);
        Assert.Empty(Entry().PendingEvents);
    }

    [Fact]
    public void Events_need_their_capability_and_the_watch_list()
    {
        Plugin(caps: """["events:process"]""", extra: """, "watch_exes": ["python.exe"]""");
        On();
        _host.Deliver(new ProcessStartedEvent { At = _clock.UtcNow, Exe = "node.exe", Pid = 1 }, _clock.UtcNow);
        _host.Deliver(new NetworkEvent { At = _clock.UtcNow, Connected = false }, _clock.UtcNow);
        Assert.Empty(Entry().PendingEvents);
        _host.Deliver(new ProcessStartedEvent { At = _clock.UtcNow, Exe = "PYTHON.EXE", Pid = 2 }, _clock.UtcNow);
        Assert.Single(Entry().PendingEvents);
    }

    [Fact]
    public void An_unticked_event_capability_gets_no_events()
    {
        Plugin();
        On(granted: ["page"]);
        _host.Deliver(new ProcessStartedEvent { At = _clock.UtcNow, Exe = "python.exe", Pid = 2 }, _clock.UtcNow);
        Assert.Empty(Entry().PendingEvents);
    }

    [Fact]
    public void A_form_is_sent_only_if_it_is_on_the_page()
    {
        Plugin();
        On();
        Output("""
            { "contract": 2, "page": { "title": "Focus", "sections": [ { "form": { "id": "notes", "submit_label": "Save",
              "fields": [ { "key": "note", "label": "Notes", "type": "text" } ] } } ] } }
            """);
        TickAndWait();
        Assert.False(LoopPump.Run(_loop, _host.Submit("focus", "other", new Dictionary<string, string>(), _clock.UtcNow)).Ok);
        Assert.False(LoopPump.Run(_loop, _host.Submit("focus", "notes", new Dictionary<string, string> { ["x"] = "1" }, _clock.UtcNow)).Ok);
        Assert.False(LoopPump.Run(_loop, _host.Submit("focus", "notes", new Dictionary<string, string> { ["note"] = new string('x', 65 * 1024) }, _clock.UtcNow)).Ok);
        Output("""{ "contract": 2 }""");
        Assert.True(LoopPump.Run(_loop, _host.Submit("focus", "notes", new Dictionary<string, string> { ["note"] = "line 1\nline 2" }, _clock.UtcNow)).Ok);
        var input = LastInput();
        Assert.Equal(PluginCall2.Submit, input.Call);
        Assert.Equal("notes", input.Action);
        Assert.Equal("line 1\nline 2", input.Form!["note"]);
    }

    [Theory]
    [InlineData("choice", "maybe", false)]
    [InlineData("choice", "short", true)]
    [InlineData("toggle", "yes", false)]
    [InlineData("toggle", "true", true)]
    [InlineData("number", "1,5", false)]
    [InlineData("number", "1.5", true)]
    public void A_form_value_must_fit_its_field(string type, string value, bool ok)
    {
        var form = new PluginForm
        {
            Id = "f",
            SubmitLabel = "Save",
            Fields = [new PluginField { Key = "k", Label = "Length", Type = Enum.Parse<PluginFieldType>(type, ignoreCase: true), Choices = ["short", "long"] }],
        };
        Assert.Equal(ok, PluginValidation.CheckSubmit(form, new Dictionary<string, string> { ["k"] = value }) is null);
    }

    [Fact]
    public void State_is_measured_as_it_is_written()
    {
        Plugin();
        On();
        Output("{ \"contract\": 2, \"state\": { \"s\": \"" + new string('é', 1500) + "\" } }");
        TickAndWait();
        var path = Path.Combine(_store.Layout.PluginData("focus"), "state.json");
        Assert.True(
            Entry().LastError?.Contains("over 4 KB", StringComparison.Ordinal) == true || new FileInfo(path).Length <= PluginValidation.MaxStateBytes,
            "state on disk is over the limit");
    }

    private void PumpUntil(Func<bool> done)
    {
        var sw = Stopwatch.StartNew();
        while (!done())
        {
            _loop.RunPending();
            Thread.Sleep(1);
            Assert.True(sw.ElapsedMilliseconds < 5000, "timed out");
        }
    }

    // ───────── output ─────────

    [Theory]
    [InlineData("""{ "contract": 2, "toast": [] }""", "output is not valid")]
    [InlineData("""{ "contract": 1 }""", "does not match")]
    [InlineData("""{ "contract": 2, "notify": [ {"id":"a","title":"a"}, {"id":"b","title":"b"}, {"id":"c","title":"c"}, {"id":"d","title":"d"} ] }""", "more than 3 notifications")]
    [InlineData("""{ "contract": 2, "wake_at": "2026-11-20T09:00:00Z" }""", "1 second to 7 days")]
    [InlineData("""{ "contract": 2, "state": { "list": [1, 2] } }""", "state values")]
    [InlineData("""{ "contract": 2, "requests": [ {"kind":"job.resume","id":"a","job":"j"}, {"kind":"job.resume","id":"a","job":"j"} ] }""", "its own id")]
    [InlineData("""{ "contract": 2, "requests": [ {"kind":"browser.block","id":"a","hosts":["YouTube.com"],"until":"2026-11-04T10:00:00Z"} ] }""", "host names")]
    [InlineData("""{ "contract": 2, "requests": [ {"kind":"browser.block","id":"a","hosts":["youtube.com"],"until":"2026-11-05T09:00:00Z"} ] }""", "12 hours")]
    [InlineData("""{ "contract": 2, "notify": [ {"id":"a","title":"a","buttons":["delete-all"]} ] }""", "declared action")]
    [InlineData("""{ "contract": 2, "notify": [ {"id":"A b","title":"a"} ] }""", "needs an id")]
    [InlineData("""{ "contract": 2, "requests": [ null ] }""", "contains null")]
    [InlineData("""{ "contract": 2, "notify": [ null ] }""", "contains null")]
    [InlineData("""{ "contract": 2, "page": { "title": "P", "sections": [ null ] } }""", "contains null")]
    public void An_output_over_a_limit_fails_the_run(string output, string reason)
    {
        Plugin();
        On();
        Output(output);
        TickAndWait();
        Assert.Contains(reason, Entry().LastError, StringComparison.Ordinal);
        Assert.Equal(1, Entry().Failures);
    }

    [Fact]
    public void A_part_without_its_capability_is_dropped_with_a_warning_and_the_run_counts()
    {
        Plugin();
        On(granted: ["page"]);
        Output("""{ "contract": 2, "notify": [ { "id": "a", "title": "Done" } ], "page": { "title": "P", "sections": [] } }""");
        TickAndWait();
        Assert.Null(Entry().LastError);
        Assert.Empty(_notices);
        Assert.NotNull(_host.Page("focus"));
        Assert.Equal(["Asked for notify without permission"], Entry().Warnings);
        Assert.Contains(_host.Cards(), c => c.Id == "plugin.focus.warnings");
    }

    [Fact]
    public void An_absent_part_keeps_the_last_one()
    {
        Plugin();
        On();
        Output("""{ "contract": 2, "page": { "title": "First", "sections": [] }, "cards": [ { "id": "now", "title": "Focus", "blocks": [] } ] }""");
        TickAndWait();
        Output("""{ "contract": 2 }""");
        LoopPump.Run(_loop, _host.RunNow("focus", _clock.UtcNow));
        Assert.Equal("First", _host.Page("focus")!.Title);
        Assert.Contains(_host.Cards(), c => c.Id == "plugin.focus.now");
    }

    [Fact]
    public void Notices_are_capped_at_twelve_an_hour()
    {
        Plugin();
        On();
        const string three = """{ "contract": 2, "notify": [ {"id":"a","title":"a"}, {"id":"b","title":"b"}, {"id":"c","title":"c"} ] }""";
        for (var i = 0; i < 5; i++)
        {
            Output(three);
            LoopPump.Run(_loop, _host.RunNow("focus", _clock.UtcNow));
        }

        Assert.Equal(12, _notices.Count);
        Assert.Contains("Notifications limited to 12 an hour", Entry().Warnings);
        _clock.Advance(TimeSpan.FromHours(1));
        Output(three);
        LoopPump.Run(_loop, _host.RunNow("focus", _clock.UtcNow));
        Assert.Equal(15, _notices.Count);
    }

    [Fact]
    public void State_is_kept_on_disk_and_fed_back()
    {
        Plugin();
        On();
        Output("""{ "contract": 2, "state": { "phase": "break", "block": 3, "running": true } }""");
        TickAndWait();
        Assert.True(File.Exists(Path.Combine(_store.Layout.PluginData("focus"), "state.json")));

        var fresh = new PluginHost(_store.Layout, _consents, _workers, _loop.Post) { PythonPath = "python" };
        fresh.Discover(_clock.UtcNow);
        Output("""{ "contract": 2 }""");
        LoopPump.Run(_loop, fresh.RunNow("focus", _clock.UtcNow));
        var state = LastInput().State!;
        Assert.Equal("break", state["phase"].GetString());
        Assert.Equal(3, state["block"].GetInt32());
    }

    // ───────── requests ─────────

    [Fact]
    public void A_request_without_its_capability_is_refused_without_reaching_the_handler()
    {
        Plugin();
        On(granted: ["page"]);
        Output("""{ "contract": 2, "requests": [ { "kind": "job.pause", "id": "p1", "job": "j1" } ] }""");
        TickAndWait();
        Assert.Empty(_requests.Seen);
        Output("""{ "contract": 2 }""");
        LoopPump.Run(_loop, _host.RunNow("focus", _clock.UtcNow));
        var result = Assert.Single(LastInput().Results!);
        Assert.Equal(PluginRequestStatus.Refused, result.Status);
        Assert.Equal("act:job was not granted", result.Message);
    }

    [Fact]
    public void A_granted_request_reaches_the_handler_and_its_result_arrives_next_run()
    {
        Plugin();
        On();
        Output("""{ "contract": 2, "requests": [ { "kind": "job.pause", "id": "p1", "job": "j1", "mode": "polite" } ] }""");
        TickAndWait();
        var (plugin, req) = Assert.Single(_requests.Seen);
        Assert.Equal("focus", plugin);
        Assert.Equal("j1", Assert.IsType<JobPauseRequest>(req).Job);
        var sw = Stopwatch.StartNew();
        while (Entry().PendingResults.Count == 0)
        {
            _loop.RunPending();
            Thread.Sleep(1);
            Assert.True(sw.ElapsedMilliseconds < 5000, "result did not arrive");
        }

        Output("""{ "contract": 2 }""");
        LoopPump.Run(_loop, _host.RunNow("focus", _clock.UtcNow));
        var result = Assert.Single(LastInput().Results!);
        Assert.Equal("p1", result.Id);
        Assert.Equal(PluginRequestStatus.Ok, result.Status);
        Output("""{ "contract": 2 }""");
        LoopPump.Run(_loop, _host.RunNow("focus", _clock.UtcNow));
        Assert.Null(LastInput().Results);
    }

    [Fact]
    public void Contract_1_plugins_run_as_before()
    {
        var dir = Path.Combine(_store.Layout.PluginsDir, "old");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "main.py"), "# test");
        File.WriteAllText(Path.Combine(dir, "plugin.json"), """
            { "contract": 1, "id": "old", "name": "Old", "version": "1.0.0", "command": ["{python}", "main.py"], "interval": "5m",
              "timeout": "10s", "reads": [], "network": [], "someday": true }
            """);
        _host.Discover(_clock.UtcNow);
        Assert.Null(Entry("old").V2);
        Assert.True(_host.Enable("old", _clock.UtcNow));
        Output("""{ "contract": 1, "cards": [] }""");
        Assert.True(LoopPump.Run(_loop, _host.RunNow("old", _clock.UtcNow)).Ok);
        Assert.Contains("\"call\":\"render\"", _workers.Stdins[^1], StringComparison.Ordinal);
        Assert.DoesNotContain("granted", _workers.Stdins[^1], StringComparison.Ordinal);
    }

    private sealed class FakeRequests : IPluginRequests
    {
        private TaskCompletionSource<PluginRequestResult>? _held;

        public List<(string Plugin, PluginRequest Request)> Seen { get; } = [];

        /// <summary>From now on every request waits for the returned answer.</summary>
        public TaskCompletionSource<PluginRequestResult> Hold() => _held = new TaskCompletionSource<PluginRequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<PluginRequestResult> Handle(string pluginId, PluginRequest request)
        {
            Seen.Add((pluginId, request));
            return _held?.Task ?? Task.FromResult(new PluginRequestResult { Id = "ignored", Status = PluginRequestStatus.Ok });
        }
    }

    /// <summary>Answers each run with the next queued stdout and records what it was given on stdin.</summary>
    private sealed class ScriptedWorkers : IProcessControl
    {
        public Queue<string> Outputs { get; } = new();

        public List<string> Stdins { get; } = [];

        public WorkerHandle StartWorker(WorkerSpec spec)
        {
            Stdins.Add(spec.Stdin ?? string.Empty);
            return new WorkerHandle("w" + Stdins.Count, default, spec.Kind);
        }

        private TaskCompletionSource? _gate;

        /// <summary>The next run waits until the returned gate is set.</summary>
        public TaskCompletionSource Hold() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<WorkerExit> WaitWorker(WorkerHandle handle, CancellationToken cancel)
        {
            var output = Outputs.Count > 0 ? Outputs.Dequeue() : """{ "contract": 2 }""";
            if (Interlocked.Exchange(ref _gate, null) is { } gate)
            {
                await gate.Task.ConfigureAwait(false);
            }

            return new WorkerExit(handle, 0, output, string.Empty, false, false);
        }

        public IReadOnlyList<ProcessOutcome> Close(IReadOnlyList<ProcKey> keys) => throw new NotSupportedException();

        public IReadOnlyList<ProcessOutcome> End(IReadOnlyList<ProcKey> keys) => throw new NotSupportedException();

        public IReadOnlyList<(ProcessOutcome Outcome, ProcessPriority Previous)> SetPriority(IReadOnlyList<ProcKey> keys, ProcessPriority priority) => throw new NotSupportedException();

        public IReadOnlyList<ProcessOutcome> SetEfficiency(IReadOnlyList<ProcKey> keys, bool on) => throw new NotSupportedException();

        public IReadOnlyList<ProcessOutcome> Freeze(string jobId, IReadOnlyList<ProcKey> tree) => throw new NotSupportedException();

        public IReadOnlyList<ProcessOutcome> Thaw(string jobId, IReadOnlyList<ProcKey> tree) => throw new NotSupportedException();

        public (ProcessOutcome Outcome, long Before, long After) TrimMemory(ProcKey key) => throw new NotSupportedException();

        public IReadOnlyList<string> RecoverFrozen() => throw new NotSupportedException();

        public void ReleaseOnQuit() => throw new NotSupportedException();

        public IReadOnlyList<ProcessOutcome> CapCores(string capId, IReadOnlyList<ProcKey> keys, int maxCores) => throw new NotSupportedException();

        public void CancelWorker(WorkerHandle handle)
        {
        }

        public bool SendControl(WorkerHandle handle, ControlMessage message) => false;
    }
}
