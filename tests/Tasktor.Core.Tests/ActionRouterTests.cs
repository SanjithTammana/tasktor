using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Platform;
using Tasktor.Core.Actions;
using Tasktor.Core.Logging;
using Tasktor.Core.Loop;
using Tasktor.Tests.Shared;

namespace Tasktor.Core.Tests;

public sealed class ActionRouterTests
{
    private readonly FakeClock _clock = new();
    private readonly LogicLoop _loop;
    private readonly MemoryLogWriter _logLines = new();
    private readonly Dictionary<uint, ProcessRow> _procs = [];
    private readonly List<(string Action, IReadOnlyList<ProcKey> Keys)> _ran = [];
    private bool _allowDestructive;
    private readonly ActionRouter _router;

    public ActionRouterTests()
    {
        _loop = new LogicLoop(_clock);
        _router = new ActionRouter(
            _loop,
            new OriginPolicy { CliAllowDestructive = () => _allowDestructive },
            GuardChain.Default(),
            k => _procs.GetValueOrDefault(k.Pid),
            new LogSink(_clock, _logLines),
            offLoop: work => Task.FromResult(work()));
        foreach (var def in ActionCatalog.All)
        {
            var id = def.Id;
            _router.Register(id, req => new PreparedAction
            {
                Summary = id + " " + string.Join(",", req.Target?.Values ?? []),
                TargetName = "app.exe",
                Processes = req.Target is { } t && t.TryGetValue("pids", out var pids)
                    ? pids.Split(',').Select(p => _procs[uint.Parse(p, System.Globalization.CultureInfo.InvariantCulture)].Key).ToList()
                    : [],
                Run = keys =>
                {
                    _ran.Add((id, keys));
                    return new ActionResult { Action = id, Status = ActionStatus.Ok };
                },
            });
        }

        Add(100, "app.exe");
        Add(101, "app.exe");
        Add(102, "app.exe", elevated: true);
        Add(103, "lsass.exe");
        Add(104, "widgets.exe");
    }

    private void Add(uint pid, string exe, bool elevated = false) =>
        _procs[pid] = new ProcessRow { Key = new ProcKey(pid, 1000 + pid), Exe = exe, Elevated = elevated };

    private ActionResult Act(string action, Origin origin, string? pids = null, string? confirm = null, Dictionary<string, string>? args = null, string? rule = null, string? toast = null)
    {
        var req = new ActionRequest
        {
            Action = action,
            Origin = origin,
            Target = pids is null ? null : new Dictionary<string, string> { ["pids"] = pids },
            Args = args,
            Confirm = confirm,
            Rule = rule,
            Toast = toast,
        };
        return LoopPump.Run(_loop, _router.Submit(req));
    }

    public static TheoryData<Origin, string, ActionStatus> Table() => new()
    {
        // origin, action, expected status (no confirm)
        { Origin.Flyout, "status", ActionStatus.Ok },
        { Origin.Flyout, "priority.set", ActionStatus.Ok },
        { Origin.Flyout, "proc.end", ActionStatus.NeedsConfirm },
        { Origin.Widget, "app.end", ActionStatus.NeedsConfirm },
        { Origin.Tray, "tracking.pause", ActionStatus.Ok },
        { Origin.Cli, "status", ActionStatus.Ok },
        { Origin.Cli, "job.pause", ActionStatus.Ok },
        { Origin.Cli, "proc.end", ActionStatus.Refused },
        { Origin.Rule, "priority.set", ActionStatus.Ok },
        { Origin.Rule, "status", ActionStatus.Refused },
        { Origin.Rule, "proc.end", ActionStatus.Refused },
        { Origin.Rule, "run", ActionStatus.Refused },
        { Origin.Rule, "preset.apply", ActionStatus.Refused },
        { Origin.Rule, "capture.pause", ActionStatus.Ok },
        { Origin.Rule, "capture.resume", ActionStatus.Refused },
        { Origin.Rule, "capture.unexclude", ActionStatus.Refused },
        { Origin.Rule, "learned.use_fallback", ActionStatus.Refused },
        { Origin.Toast, "job.log", ActionStatus.Refused },
        { Origin.Toast, "run", ActionStatus.Refused },
        { Origin.Cli, "plugin.enable", ActionStatus.Refused },
        { Origin.Tray, "proc.end", ActionStatus.Refused },
        { Origin.Widget, "settings.reset", ActionStatus.Refused },
        { Origin.Flyout, "plugin.enable", ActionStatus.Ok },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void Origin_policy_table(Origin origin, string action, ActionStatus expected) =>
        Assert.Equal(expected, Act(action, origin).Status);

    [Fact]
    public void Unknown_action_is_rejected()
    {
        var r = Act("format.disk", Origin.Flyout);
        Assert.Equal(ActionStatus.Refused, r.Status);
        Assert.Equal("unknown_action", r.Reason);
        Assert.Throws<ArgumentException>(() => _router.Register("format.disk", _ => new PreparedAction { Summary = "x" }));
    }

    [Fact]
    public void Destructive_is_two_step_with_a_30_second_single_use_token()
    {
        var first = Act("proc.end", Origin.Flyout, "100");
        Assert.Equal(ActionStatus.NeedsConfirm, first.Status);
        Assert.StartsWith("proc.end", first.Summary, StringComparison.Ordinal);
        Assert.Empty(_ran);
        var second = Act("proc.end", Origin.Flyout, "100", first.Token);
        Assert.Equal(ActionStatus.Ok, second.Status);
        Assert.Single(_ran);
        Assert.Equal(ActionStatus.NeedsConfirm, Act("proc.end", Origin.Flyout, "100", first.Token).Status); // used up
    }

    [Fact]
    public void Token_expires_after_30_seconds_and_binds_to_its_request()
    {
        var t1 = Act("proc.end", Origin.Flyout, "100").Token;
        Assert.Equal(ActionStatus.NeedsConfirm, Act("proc.end", Origin.Flyout, "101", t1).Status); // other target
        var t2 = Act("proc.end", Origin.Flyout, "100").Token;
        _clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(ActionStatus.NeedsConfirm, Act("proc.end", Origin.Flyout, "100", t2).Status);
        Assert.Empty(_ran);
    }

    [Fact]
    public void Cli_destructive_needs_yes_and_the_setting()
    {
        Assert.Equal("origin_policy:cli_destructive_off", Act("proc.end", Origin.Cli, "100", OriginPolicy.CliYes).Reason);
        _allowDestructive = true;
        var noYes = Act("proc.end", Origin.Cli, "100");
        Assert.Equal(ActionStatus.NeedsConfirm, noYes.Status);
        Assert.Null(noYes.Token);
        Assert.Equal(ActionStatus.Ok, Act("proc.end", Origin.Cli, "100", OriginPolicy.CliYes).Status);
    }

    [Fact]
    public void Export_include_flags_confirm_like_destructive()
    {
        Assert.Equal(ActionStatus.Ok, Act("export.data", Origin.Cli).Status);
        var raw = new Dictionary<string, string> { ["include_raw"] = "true" };
        Assert.Equal(ActionStatus.NeedsConfirm, Act("export.data", Origin.Cli, args: raw).Status);
        Assert.Equal(ActionStatus.Ok, Act("export.data", Origin.Cli, confirm: OriginPolicy.CliYes, args: raw).Status);
        Assert.Equal(ActionStatus.NeedsConfirm, Act("export.data", Origin.Flyout, args: raw).Status);
    }

    [Fact]
    public void Toasts_run_only_what_they_offered()
    {
        var offered = new ActionRequest { Action = "run", Origin = Origin.Toast, Toast = "new-pdf:1", Target = new Dictionary<string, string> { ["rule"] = "new-pdf", ["command"] = "convert" } };
        _router.Toasts.Offer("new-pdf:1", offered);
        Assert.Equal(ActionStatus.Ok, LoopPump.Run(_loop, _router.Submit(offered)).Status);
        Assert.Equal(ActionStatus.Refused, Act("proc.end", Origin.Toast, "100", toast: "new-pdf:1").Status);
        _router.Toasts.Withdraw("new-pdf:1");
        Assert.Equal(ActionStatus.Refused, LoopPump.Run(_loop, _router.Submit(offered)).Status);
    }

    [Fact]
    public void Builtin_rules_may_use_guardrail_actions()
    {
        _router.IsBuiltinRule = id => id == "job-silent-guard";
        Assert.Equal(ActionStatus.Ok, Act("learned.use_fallback", Origin.Rule, rule: "job-silent-guard").Status);
        Assert.Equal(ActionStatus.Refused, Act("learned.use_fallback", Origin.Rule, rule: "mine").Status);
    }

    [Theory]
    [InlineData("102", "guard:integrity")]
    [InlineData("103", "guard:denylist")]
    public void Guards_refuse_with_a_reason_and_call_nothing(string pid, string reason)
    {
        var r = Act("priority.set", Origin.Flyout, pid);
        Assert.Equal(ActionStatus.Refused, r.Status);
        Assert.StartsWith(reason, r.Reason, StringComparison.Ordinal);
        Assert.Empty(_ran);
    }

    [Fact]
    public void Freeze_spares_the_shell_hosts()
    {
        Assert.Equal(ActionStatus.Refused, Act("proc.freeze", Origin.Flyout, "104").Status);
        Assert.Equal(ActionStatus.Ok, Act("priority.set", Origin.Flyout, "104").Status);
    }

    [Fact]
    public void Pid_reuse_between_snapshot_and_click_is_caught()
    {
        // A3 acceptance 6: a process replaced between snapshot and click is not ended.
        var token = Act("proc.end", Origin.Flyout, "100").Token;
        _procs[100] = _procs[100] with { Key = new ProcKey(100, 999_999) };
        var req = new ActionRequest { Action = "proc.end", Origin = Origin.Flyout, Target = new Dictionary<string, string> { ["pids"] = "100" }, Confirm = token };
        _router.Register("proc.end", _ => new PreparedAction { Summary = "End app", Processes = [new ProcKey(100, 1100)], Run = k => { _ran.Add(("proc.end", k)); return new ActionResult { Action = "proc.end", Status = ActionStatus.Ok }; } });
        var result = LoopPump.Run(_loop, _router.Submit(req));
        Assert.Equal(ActionStatus.Refused, result.Status);
        Assert.Contains("Process changed", result.Message, StringComparison.Ordinal);
        Assert.Empty(_ran);
    }

    [Fact]
    public void Mixed_group_reports_a_partial_result()
    {
        // A3 acceptance 7: ends what it can, reports the rest.
        var r = Act("priority.set", Origin.Flyout, "100,101,102");
        Assert.Equal(ActionStatus.Partial, r.Status);
        Assert.Equal(2, _ran[0].Keys.Count);
        Assert.Single(r.Items!, i => i.Status == ActionStatus.Refused);
    }

    [Fact]
    public void Every_action_is_logged_without_command_lines()
    {
        Act("priority.set", Origin.Cli, "100");
        var line = Assert.Single(_logLines.Lines);
        Assert.Contains("action=priority.set target=app.exe origin=cli result=ok", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Handlers_run_off_loop_and_report_back_on_the_loop()
    {
        var loop = new LogicLoop(_clock);

        // A dedicated thread, not the shared pool: under a parallel test run the pool can take seconds to start work.
        static Task<ActionResult> OwnThread(Func<ActionResult> work)
        {
            var tcs = new TaskCompletionSource<ActionResult>();
            new Thread(() => tcs.SetResult(work())) { IsBackground = true }.Start();
            return tcs.Task;
        }

        var router = new ActionRouter(loop, new OriginPolicy(), GuardChain.Default(), _ => null, offLoop: OwnThread);
        var offLoopThread = -1;
        var doneOnLoop = false;
        router.Register("refits.pause", _ => new PreparedAction
        {
            Summary = "pause refits",
            Run = _ =>
            {
                offLoopThread = Environment.CurrentManagedThreadId;
                return new ActionResult { Action = "refits.pause", Status = ActionStatus.Ok };
            },
            Done = _ => doneOnLoop = true,
        });
        var task = router.Submit(new ActionRequest { Action = "refits.pause", Origin = Origin.Flyout });
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref offLoopThread) != -1, 5000));
        Assert.NotEqual(Environment.CurrentManagedThreadId, offLoopThread); // the loop is this test's thread
        Assert.False(doneOnLoop); // not until the loop runs
        LoopPump.Run(loop, task);
        Assert.True(doneOnLoop);
    }

    [Fact]
    public void Tray_menu_rows_come_from_the_catalog()
    {
        var tray = ActionCatalog.ForSurface(Surface.Tray).Select(d => d.Id).ToList();
        Assert.Contains("tracking.pause", tray);
        Assert.Contains("capture.pause", tray);
        Assert.Contains("alerts.snooze_all", tray);
        Assert.Contains("quit", tray);
        Assert.DoesNotContain("proc.end", tray);
    }
}
