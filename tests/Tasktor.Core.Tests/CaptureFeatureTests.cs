using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Capture;
using Tasktor.Core.Features;
using Tasktor.Tests.Shared;

namespace Tasktor.Core.Tests;

/// <summary>U4 D-G6 / T1, agent side: the capture card and actions over the capture host link; rules add, never lift.</summary>
public sealed class CaptureFeatureTests : IDisposable
{
    private readonly FeatureRig _rig = new();
    private readonly FakeHost _host = new();
    private readonly CaptureFeature _capture;

    public CaptureFeatureTests()
    {
        _capture = _rig.Add(new CaptureFeature(_rig.Context, _host.Handle) { ForegroundApp = () => "Slack.exe" });
        _rig.Loop.RunPending();
    }

    public void Dispose() => _rig.Dispose();

    /// <summary>A minimal capture host: blocks with a by-rule flag, a pause, a record count.</summary>
    private sealed class FakeHost
    {
        public List<CaptureCommand> Seen { get; } = [];

        public List<CaptureBlockInfo> Blocks { get; } = [];

        public DateTimeOffset? Until { get; private set; }

        public bool Indefinite { get; private set; }

        public long Records { get; set; } = 40;

        public bool Down { get; set; }

        /// <summary>Nothing listens on the pipe: the connect times out (the host was stopped).</summary>
        public bool Stopped { get; set; }

        public List<string> Extensions { get; set; } = [];

        public Task<CaptureStatus> Handle(CaptureCommand c, CancellationToken ct)
        {
            if (Stopped)
            {
                return Task.FromException<CaptureStatus>(new TimeoutException("no pipe server"));
            }

            if (Down)
            {
                return Task.FromException<CaptureStatus>(new IOException("pipe closed"));
            }

            Seen.Add(c);
            string? refusal = null;
            switch (c.Verb)
            {
                case CaptureVerb.Pause:
                    (Until, Indefinite) = (c.Until, c.Until is null);
                    break;
                case CaptureVerb.Resume:
                    (Until, Indefinite) = (null, false);
                    break;
                case CaptureVerb.Exclude:
                    Blocks.Add(new CaptureBlockInfo { Id = "b" + Blocks.Count, Kind = c.App is null ? "site" : "app", Text = "Not capturing " + (c.App ?? c.Site), ByRule = !c.ByUser });
                    break;
                case CaptureVerb.Unexclude:
                    var b = Blocks.FirstOrDefault(x => x.Id == c.BlockId);
                    refusal = b is null ? "not_found" : !c.ByUser ? "rule_owned" : null;
                    if (refusal is null)
                    {
                        Blocks.Remove(b!);
                    }

                    break;
                case CaptureVerb.Forget:
                    return Task.FromResult(Status() with { Forgotten = 12 });
                case CaptureVerb.Search:
                    return Task.FromResult(Status() with { Hits = c.Query == "revenue" ? [new ActivityHit { Start = DateTimeOffset.UnixEpoch, End = DateTimeOffset.UnixEpoch.AddMinutes(10), Snippet = "Quarterly [revenue] review" }] : [] });
            }

            return Task.FromResult(Status() with { Refusal = refusal });
        }

        private CaptureStatus Status() => new() { On = true, PausedUntil = Until, PausedIndefinitely = Indefinite, Blocks = [.. Blocks], RecordsToday = Records, Extensions = [.. Extensions] };
    }

    private ActionResult Act(string action, Origin origin = Origin.Flyout, Dictionary<string, string>? args = null, Dictionary<string, string>? target = null, string? confirm = null, string? rule = null)
    {
        var r = _rig.Act(new ActionRequest { Action = action, Origin = origin, Args = args, Target = target, Confirm = confirm, Rule = rule });
        _rig.Loop.RunPending();
        return r;
    }

    [Fact]
    public void The_card_shows_recording_then_paused_and_the_state_follows_the_host()
    {
        Assert.Equal(40, _rig.State.Current.Capture!.RecordsToday);
        Assert.Contains(_capture.Cards()[0].Blocks, b => b is Contracts.Cards.BadgeBlock bb && bb.Text.ToString().Contains("Recording", StringComparison.Ordinal));
        Assert.Equal(ActionStatus.Ok, Act("capture.pause", args: new() { ["for"] = "1h" }).Status);
        Assert.Equal(_rig.Clock.UtcNow.AddHours(1), _rig.State.Current.Capture.PausedUntil);
        Assert.Equal("capture.resume", _capture.Cards()[0].Actions![0].Action);
        Assert.Equal(ActionStatus.Ok, Act("capture.resume").Status);
        Assert.Null(_rig.State.Current.Capture.PausedUntil);
        Assert.Equal(ActionStatus.Ok, Act("capture.pause").Status);
        Assert.True(_rig.State.Current.Capture.PausedIndefinitely);
    }

    [Fact]
    public void Rules_may_add_a_block_but_never_lift_one()
    {
        Assert.Equal(ActionStatus.Ok, Act("capture.exclude", Origin.Rule, args: new() { ["app"] = "zoom.exe" }, rule: "call-quiet").Status);
        Assert.True(_host.Blocks.Single().ByRule);
        Assert.Equal(ActionStatus.Refused, Act("capture.unexclude", Origin.Rule, target: new() { ["block"] = "b0" }, rule: "call-quiet").Status);
        Assert.Equal(ActionStatus.Refused, Act("capture.resume", Origin.Rule, rule: "call-quiet").Status);
        Assert.DoesNotContain(_host.Seen, c => c.Verb is CaptureVerb.Unexclude or CaptureVerb.Resume); // refused before reaching the host
        Assert.Equal(ActionStatus.Ok, Act("capture.unexclude", target: new() { ["block"] = "b0" }).Status);
        Assert.Empty(_host.Blocks);
    }

    [Fact]
    public void This_app_resolves_the_foreground_app_and_bad_input_is_refused()
    {
        Assert.Equal(ActionStatus.Ok, Act("capture.exclude", target: new() { ["app"] = CaptureFeature.Foreground }).Status);
        Assert.Equal("slack.exe", _host.Seen.Last().App);
        Assert.Equal(ActionStatus.Refused, Act("capture.exclude", args: new() { ["site"] = "https://x.example/path" }).Status);
        Assert.Equal(ActionStatus.Refused, Act("capture.pause", args: new() { ["for"] = "forever" }).Status);
        Assert.Equal(ActionStatus.Refused, Act("capture.tab_pause", target: new() { ["tab"] = "x" }).Status);
        Assert.Equal(ActionStatus.Ok, Act("capture.tab_pause", target: new() { ["tab"] = "7" }).Status);
        Assert.Equal(7, _host.Seen.Last().Tab);
    }

    [Fact]
    public void A_browser_in_front_for_a_minute_without_the_extension_gets_the_setup_offer()
    {
        var front = "notepad.exe";
        _capture.ForegroundApp = () => front;
        var setups = new List<string>();
        _capture.SetupExtension = b => { setups.Add(b); return true; };
        bool Offered() => _capture.Cards()[0].Actions!.Any(a => a.Action == "capture.extension_setup");

        front = "chrome.exe";
        _capture.Refresh();
        _rig.Loop.RunPending();
        Assert.False(Offered()); // just came to the front
        _rig.Clock.Advance(TimeSpan.FromSeconds(30));
        _capture.Refresh();
        _rig.Loop.RunPending();
        Assert.False(Offered());
        _rig.Clock.Advance(TimeSpan.FromSeconds(30));
        _capture.Refresh();
        _rig.Loop.RunPending();
        Assert.True(Offered());
        var card = _capture.Cards()[0];
        Assert.Contains(card.Blocks, b => b is Contracts.Cards.AlertBlock a && a.Text.ToString().Contains("Chrome", StringComparison.Ordinal));
        Assert.Equal("chrome", card.Actions!.Single(a => a.Action == "capture.extension_setup").Args!["browser"]);

        front = "notepad.exe"; // the offer stays until the extension connects
        _capture.Refresh();
        _rig.Loop.RunPending();
        Assert.True(Offered());
        Assert.Equal(ActionStatus.Ok, Act("capture.extension_setup", args: new() { ["browser"] = "chrome" }).Status);
        Assert.Equal(["chrome"], setups);
        Assert.Equal(ActionStatus.Refused, Act("capture.extension_setup", args: new() { ["browser"] = "firefox" }).Status);
        Assert.Equal(ActionStatus.Refused, Act("capture.extension_setup", Origin.Rule, args: new() { ["browser"] = "chrome" }, rule: "r").Status); // people only
        Assert.Single(setups);

        _host.Extensions = ["chrome"];
        _capture.Refresh();
        _rig.Loop.RunPending();
        Assert.False(Offered());

        front = "msedge.exe"; // connected browsers never get it; a paused capture still does (the extension connects while paused)
        _host.Extensions = ["chrome", "edge"];
        _rig.Clock.Advance(TimeSpan.FromMinutes(2));
        _capture.Refresh();
        _rig.Loop.RunPending();
        Assert.False(Offered());
    }

    [Fact]
    public void Terminal_marks_off_gets_an_offer_with_the_change_to_make_and_tasktor_never_writes_the_file()
    {
        var settings = """
            // Windows Terminal settings, with comments
            { "profiles": { "defaults": { "font": { "size": 11 } }, "list": [] }, }
            """;
        var opened = new List<string>();
        _capture.TerminalSettings = () => ("settings.json", settings);
        _capture.OpenFile = p => { opened.Add(p); return true; };
        bool Offered() => _capture.Cards()[0].Actions!.Any(a => a.Action == "capture.terminal_marks");

        _rig.Clock.Advance(CaptureFeature.TerminalCheckEvery); // read at most every 10 minutes
        _capture.Refresh();
        _rig.Loop.RunPending();
        Assert.True(Offered());
        var r = Act("capture.terminal_marks");
        Assert.Equal(ActionStatus.Ok, r.Status);
        Assert.Contains("\"autoMarkPrompts\": true", r.Message, StringComparison.Ordinal);
        Assert.Equal(["settings.json"], opened);
        Assert.Equal(ActionStatus.Refused, Act("capture.terminal_marks", Origin.Rule, rule: "r").Status); // people only

        settings = """{ "profiles": { "defaults": { "autoMarkPrompts": true } } }""";
        _rig.Clock.Advance(CaptureFeature.TerminalCheckEvery);
        _capture.Refresh();
        _rig.Loop.RunPending();
        Assert.False(Offered());
    }

    [Theory]
    [InlineData("""{ "profiles": { "defaults": { "autoMarkPrompts": true } } }""", true)]
    [InlineData("""{ "profiles": { "defaults": { "autoMarkPrompts": false } } }""", false)]
    [InlineData("""{ "profiles": { "defaults": {} } }""", false)]
    [InlineData("""{ "profiles": [ { "name": "x" } ] }""", false)]
    [InlineData("not json", null)]
    [InlineData(null, null)]
    public void Terminal_marks_state_is_read_from_the_settings(string? json, bool? on) => Assert.Equal(on, TerminalMarks.IsOn(json));

    [Fact]
    public void Search_my_activity_is_relayed_to_the_capture_host_as_a_persons_search()
    {
        var pull = _rig.Pulls["activity"];
        var hits = pull(new Contracts.Pipe.PullMessage { Id = "1", Table = "activity", Args = new Dictionary<string, string> { ["q"] = " revenue ", ["since"] = "7d" } })!.AsArray();
        Assert.Equal("Quarterly [revenue] review", (string)hits.Single()!["snippet"]!);
        var sent = _host.Seen.Last();
        Assert.Equal((CaptureVerb.Search, true, "revenue"), (sent.Verb, sent.ByUser, sent.Query));
        Assert.Equal(_rig.Clock.UtcNow.AddDays(-7), sent.Since);
        Assert.Null(pull(new Contracts.Pipe.PullMessage { Id = "2", Table = "activity", Args = new Dictionary<string, string> { ["q"] = "  " } })); // nothing to search
        _host.Down = true;
        Assert.Null(pull(new Contracts.Pipe.PullMessage { Id = "3", Table = "activity", Args = new Dictionary<string, string> { ["q"] = "revenue" } })); // capture not running
    }

    [Fact]
    public void Forget_is_two_step_and_reports_the_count()
    {
        var first = Act("capture.forget", args: new() { ["since"] = "1h" });
        Assert.Equal(ActionStatus.NeedsConfirm, first.Status);
        Assert.DoesNotContain(_host.Seen, c => c.Verb == CaptureVerb.Forget);
        var done = Act("capture.forget", args: new() { ["since"] = "1h" }, confirm: first.Token);
        Assert.Equal("Forgot 12 records", done.Message);
        Assert.Equal(_rig.Clock.UtcNow.AddHours(-1), _host.Seen.Last().Since);
    }

    [Fact]
    public void A_hits_page_reopens_in_the_browser_for_a_person_and_nothing_else_is_launched()
    {
        Assert.Equal(ActionStatus.Ok, Act("activity.open", args: new() { ["url"] = "https://docs.example.com/guide?id=7" }).Status);
        Assert.Equal("https://docs.example.com/guide?id=7", _rig.Launched.Last());
        var before = _rig.Launched.Count;
        foreach (var bad in new[] { "file:///C:/Windows/System32/calc.exe", "javascript:alert(1)", "C:\\tools\\run.cmd", "ms-settings:privacy", "edit:C:\\x.json", "" })
        {
            Assert.Equal(ActionStatus.Refused, Act("activity.open", args: new() { ["url"] = bad }).Status);
        }

        Assert.Equal(ActionStatus.Refused, Act("activity.open", origin: Origin.Rule, args: new() { ["url"] = "https://example.com/" }, rule: "r1").Status);
        Assert.Equal(before, _rig.Launched.Count);
    }

    [Fact]
    public void Forget_an_app_or_site_is_two_step_and_names_what_goes()
    {
        var first = Act("capture.forget", args: new() { ["site"] = "Docs.Example.com" });
        Assert.Equal(ActionStatus.NeedsConfirm, first.Status);
        Assert.Contains("docs.example.com", first.Summary, StringComparison.Ordinal);
        Act("capture.forget", args: new() { ["site"] = "Docs.Example.com" }, confirm: first.Token);
        var sent = _host.Seen.Last();
        Assert.Equal((CaptureVerb.Forget, true, null, "docs.example.com", (DateTimeOffset?)null), (sent.Verb, sent.ByUser, sent.App, sent.Site, sent.Since));

        var app = Act("capture.forget", args: new() { ["app"] = "EXCEL.EXE" });
        Act("capture.forget", args: new() { ["app"] = "EXCEL.EXE" }, confirm: app.Token);
        Assert.Equal("excel.exe", _host.Seen.Last().App);
        _capture.ForegroundApp = () => "Code.exe";
        var card = _capture.Cards()[0].Actions!.Single(x => x.Id == "forget_app");
        var front = Act(card.Action!, target: new(card.Target!));
        Assert.Contains("code.exe", front.Summary, StringComparison.Ordinal); // the confirm names the app it resolved
        _capture.ForegroundApp = () => null;
        Assert.Equal(ActionStatus.Refused, Act(card.Action!, target: new(card.Target!)).Status);
        Assert.Equal(ActionStatus.Refused, Act("capture.forget", args: new() { ["site"] = "not a host/" }).Status);
        Assert.Equal(ActionStatus.Refused, Act("capture.forget", args: new() { ["app"] = "a.exe", ["since"] = "1h" }).Status); // one at a time
    }

    [Fact]
    public void Without_a_running_host_actions_refuse_and_the_card_says_so()
    {
        _host.Down = true;
        Assert.Equal(ActionStatus.Failed, Act("capture.pause", args: new() { ["for"] = "15m" }).Status);
        _capture.Link = null;
        _capture.Refresh();
        Assert.Equal(ActionStatus.Refused, Act("capture.pause", args: new() { ["for"] = "15m" }).Status);
        Assert.False(_rig.State.Current.Capture!.On);
        Assert.Contains(_capture.Cards()[0].Blocks, b => b is Contracts.Cards.BadgeBlock bb && bb.Text.ToString().Contains("not running", StringComparison.Ordinal));
    }

    [Fact]
    public void A_stopped_host_refuses_forget_as_not_running()
    {
        // 2026-11-09 real PC (#4 check 7): with the host stopped the link exists but nothing listens, and the forget
        // failed as "did not answer" instead of refusing.
        _host.Stopped = true;
        var first = Act("capture.forget", args: new() { ["since"] = "1h" });
        var r = Act("capture.forget", args: new() { ["since"] = "1h" }, confirm: first.Token);
        Assert.Equal(ActionStatus.Refused, r.Status);
        Assert.Equal("Capture is not running", r.Message);
        _host.Stopped = false;
        _host.Down = true; // a host that closes the pipe mid-call did not answer: that stays a failure
        first = Act("capture.forget", args: new() { ["since"] = "1h" });
        Assert.Equal(ActionStatus.Failed, Act("capture.forget", args: new() { ["since"] = "1h" }, confirm: first.Token).Status);
    }

    [Fact]
    public void Without_consent_the_supervisor_starts_nothing_and_the_card_says_so()
    {
        using var rig = new FeatureRig();
        var sup = new HostSupervisor(rig.Context, HostSupervisor.CaptureSpec(AppContext.BaseDirectory, null), () => false);
        var capture = rig.Add(new CaptureFeature(rig.Context, null) { Supervisor = sup });
        rig.Pump();
        Assert.False(sup.Running);
        Assert.Equal("no_consent", sup.Failure);
        Assert.Contains(capture.Cards()[0].Blocks, b => b is Contracts.Cards.BadgeBlock bb && bb.Text.ToString().Contains("consent", StringComparison.Ordinal));
        capture.Disable();
        Assert.Null(sup.Failure);
    }
}
