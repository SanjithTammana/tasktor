using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Platform;
using Tasktor.Core.Actions;
using Tasktor.Core.Apps;
using Tasktor.Core.Features;
using Tasktor.Core.Features.Guards;
using Tasktor.Core.Rules;
using Tasktor.Tests.Shared;
using AppsFeature = Tasktor.Core.Features.SystemMonitor.Apps;

namespace Tasktor.Core.Tests;

/// <summary>A feature that contributes fixed states (stands in for Battery / NextEvent in guard tests).</summary>
internal sealed class StatesFeature(FeatureContext context) : Feature(context)
{
    public Dictionary<string, bool> States { get; } = new(StringComparer.Ordinal);

    public override string Id => "states";

    public override FeatureTier Tier => FeatureTier.Core;

    public override IReadOnlyList<Contracts.Cards.Card> Cards() => [];

    public override void Contribute(RuleInputs inputs, DateTimeOffset now)
    {
        foreach (var (k, v) in States)
        {
            inputs.State(k, v);
        }
    }

    protected override void OnEnable()
    {
    }

    protected override void OnDisable()
    {
    }
}

/// <summary>U2 I3 / I4 / D-S4: guards are default rules over reversible actions, opt-in per group, with undo.</summary>
public sealed class GuardTests : IDisposable
{
    private readonly FeatureRig _rig = new();
    private readonly Dictionary<uint, ProcessRow> _procs = [];
    private readonly AppsFeature _apps;
    private readonly StatesFeature _states;
    private readonly PowerGuard _power;
    private readonly List<string> _refits = [];

    public GuardTests()
    {
        _apps = _rig.Add(new AppsFeature(_rig.Context, new LiveTable(_procs), new AppIdentity(), name => _rig.Rules.Engine.Rules.Groups.GetValueOrDefault(name)));
        _apps.RuleGroups = () => _rig.Rules.Engine.Rules.Groups;
        _states = _rig.Add(new StatesFeature(_rig.Context));
        _power = _rig.Add(new PowerGuard(_rig.Context, _apps, null));
        _rig.Rules.ExtraGuards.Add(_power.ManualGuards);
        _rig.Router.Register("refits.pause", _ => Refits("pause"));
        _rig.Router.Register("refits.resume", _ => Refits("resume"));
        _rig.Scheduler.Lease(AppsFeature.SamplerName, TimeSpan.FromSeconds(60), ColumnSet.Names, holder: "test");
    }

    public void Dispose() => _rig.Dispose();

    private PreparedAction Refits(string verb) => new()
    {
        Summary = verb,
        Immediate = new ActionResult { Action = "refits." + verb, Status = ActionStatus.Ok },
        Done = _ => _refits.Add(verb),
    };

    private void Proc(uint pid, string exe)
    {
        _procs[pid] = new ProcessRow { Key = new ProcKey(pid, pid * 10), ParentPid = 1, Exe = exe };
        _rig.ProcessTable[pid] = _procs[pid];
    }

    private void Evaluate()
    {
        _rig.Pump();
        _rig.Rules.Evaluate();
        _rig.Loop.RunPending();
    }

    [Fact]
    public void On_battery_refits_wait_and_resume_when_plugged_in_and_the_guard_is_listed_meanwhile()
    {
        _rig.LoadRules();
        _states.States["on_battery"] = true;
        Evaluate();
        Assert.Equal(["pause"], _refits);
        Assert.Contains(_rig.State.Current.Guards!, g => g.Rule == "battery-refits");
        _states.States["on_battery"] = false;
        Evaluate();
        Assert.Equal(["pause", "resume"], _refits);
        Assert.Null(_rig.State.Current.Guards);
    }

    [Fact]
    public void App_affecting_guards_are_off_and_act_only_on_the_groups_the_user_opted_in()
    {
        Proc(50, "msbuild.exe");
        _rig.LoadRules("""{ "rules": [ { "id": "battery-cap", "enabled": true } ] }""");
        _states.States["on_battery"] = true;
        Evaluate();
        Assert.DoesNotContain(_rig.Processes.Calls, c => c.StartsWith("cap", StringComparison.Ordinal)); // battery_guarded is empty

        _rig.LoadRules("""{ "groups": { "battery_guarded": ["msbuild.exe"] }, "rules": [ { "id": "battery-cap", "enabled": true } ] }""");
        _states.States["on_battery"] = false;
        Evaluate();
        _states.States["on_battery"] = true;
        Evaluate();
        Assert.Contains("cap:2:50", _rig.Processes.Calls);
        Assert.Single(_power.Caps);
        Assert.Contains(_rig.State.Current.Guards!, g => g.Text.Contains("capped to 2 cores", StringComparison.Ordinal));

        _states.States["on_battery"] = false;
        Evaluate();
        Assert.Contains("cap:0:", _rig.Processes.Calls); // undo released it
        Assert.Empty(_power.Caps);
    }

    [Fact]
    public void Release_all_caps_releases_every_cap_set_by_hand()
    {
        Proc(50, "train.exe");
        Proc(51, "render.exe");
        _rig.Pump();
        foreach (var exe in new[] { "train.exe", "render.exe" })
        {
            var r = _rig.Act(new ActionRequest { Action = "cores.cap", Origin = Origin.Flyout, Target = new Dictionary<string, string> { ["process"] = exe }, Args = new Dictionary<string, string> { ["max"] = "4" } });
            Assert.Equal(ActionStatus.Ok, r.Status);
        }

        Assert.Equal(2, _power.Caps.Count);
        Assert.Contains(_power.Cards()[0].Actions!, a => a.Action == "cores.release_all");
        var all = _rig.Act(new ActionRequest { Action = "cores.release_all", Origin = Origin.Flyout });
        Assert.Equal("2 caps released", all.Message);
        Assert.Empty(_power.Caps);
        Assert.Equal(2, _rig.Processes.Calls.Count(c => c == "cap:0:"));
    }

    [Fact]
    public void Undo_on_a_guard_reverses_it_now_and_it_stays_quiet_until_its_condition_clears()
    {
        _rig.LoadRules();
        _states.States["on_battery"] = true;
        Evaluate();
        var r = _rig.Act(new ActionRequest { Action = "rule.undo", Origin = Origin.Flyout, Target = new Dictionary<string, string> { ["rule"] = "battery-refits" } });
        Assert.Equal(ActionStatus.Ok, r.Status);
        _rig.Loop.RunPending();
        Assert.Equal(["pause", "resume"], _refits);
        Evaluate();
        Assert.Equal(["pause", "resume"], _refits); // not re-applied while still on battery
        Assert.Null(_rig.State.Current.Guards);
        _states.States["on_battery"] = false;
        Evaluate();
        _states.States["on_battery"] = true;
        Evaluate();
        Assert.Equal(["pause", "resume", "pause"], _refits); // armed again after the condition cleared
    }

    [Fact]
    public void Badges_are_set_and_cleared_by_rules()
    {
        _rig.Act(new ActionRequest { Action = "badge.set", Origin = Origin.Rule, Rule = "overlap", Target = new Dictionary<string, string> { ["rule"] = "overlap" }, Args = new Dictionary<string, string> { ["text"] = "3" } });
        Assert.Equal("3", _rig.Rules.Badges["overlap"]);
        _rig.Act(new ActionRequest { Action = "badge.clear", Origin = Origin.Rule, Rule = "overlap", Target = new Dictionary<string, string> { ["rule"] = "overlap" } });
        Assert.Empty(_rig.Rules.Badges);
    }

    private sealed class FakeCalls : IEventSource<CallState>
    {
        public Action<CallState>? Post { get; private set; }

        public string Name => "calls";

        public void Start(Action<CallState> post) => Post = post;

        public void Stop() => Post = null;
    }

    [Fact]
    public void In_call_from_a_call_app_s_microphone_or_a_browser_during_a_calendar_meeting()
    {
        var meeting = false;
        var source = new FakeCalls();
        var guard = _rig.Add(new MeetingGuard(_rig.Context, source, () => meeting) { CallApps = () => ["Zoom.exe", "ms-teams.exe", "slack.exe"] });
        source.Post!(new CallState(["C:\\Program Files\\Zoom\\bin\\Zoom.exe"], true));
        _rig.Loop.RunPending();
        Assert.True(guard.InCall());
        guard.Apply(new CallState(["MSTeams_8wekyb3d8bbwe"], true));
        Assert.True(guard.InCall()); // package family of the new Teams
        guard.Apply(new CallState(["msedge.exe"], true));
        Assert.False(guard.InCall());
        meeting = true;
        Assert.True(guard.InCall());
        guard.Apply(new CallState(["audacity.exe"], true));
        Assert.False(guard.InCall());
        Assert.Null(guard.Notice);
    }

    [Fact]
    public void Without_the_microphone_route_in_call_follows_the_calendar_and_the_guards_card_says_so()
    {
        var meeting = true;
        var guard = _rig.Add(new MeetingGuard(_rig.Context, null, () => meeting));
        _power.Notices.Add(() => guard.Notice);
        Assert.True(guard.InCall());
        meeting = false;
        Assert.False(guard.InCall());
        Assert.Contains(_power.Cards()[0].Blocks, b => b is Contracts.Cards.BadgeBlock bb && bb.Text.ToString().Contains("calendar only", StringComparison.Ordinal));
        _rig.LoadRules();
        meeting = true;
        Evaluate();
        Assert.Contains("pause", _refits);
    }
}
