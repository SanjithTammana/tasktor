using System.Text.Json;
using Tasktor.Contracts;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Agents;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Snapshot;
using Tasktor.Core.Apps;
using Tasktor.Core.Features.Agents;
using Tasktor.Core.Rules;
using Tasktor.Tests.Shared;
using AppsFeature = Tasktor.Core.Features.SystemMonitor.Apps;

namespace Tasktor.Core.Tests;

/// <summary>The process list as the names scan sees it: whatever the test's dictionary holds right now.</summary>
internal sealed class LiveTable(Dictionary<uint, ProcessRow> procs) : ITableSource<ProcKey, ProcessRow>
{
    public string Name => "live";

    public void Read(ICollection<KeyValuePair<ProcKey, ProcessRow>> into, ColumnSet columns)
    {
        foreach (var p in procs.Values)
        {
            into.Add(new(p.Key, p));
        }
    }
}

/// <summary>U2 I1 / I2: agent sessions from the agents group and hook files, busy estimates, the permission toast, hooks setup.</summary>
public sealed class AgentWatchTests : IDisposable
{
    private readonly FeatureRig _rig = new();
    private readonly Dictionary<uint, ProcessRow> _procs = [];
    private readonly Dictionary<uint, string> _cmd = [];
    private readonly AgentWatch _agents;
    private readonly List<IReadOnlyList<uint>> _front = [];
    private bool _inFront;

    public AgentWatchTests()
    {
        var apps = _rig.Add(new AppsFeature(_rig.Context, new LiveTable(_procs), new AppIdentity(), _ => null));
        _agents = new AgentWatch(_rig.Context, apps, new AgentHookSource(_rig.Store.Layout.AgentsDir), k => _procs.GetValueOrDefault(k.Pid), k => _cmd.GetValueOrDefault(k.Pid))
        {
            AgentGroup = () => ["claude.exe", "codex.exe", "aider", "AutoHotkey*.exe"],
            WindowInFront = chain =>
            {
                _front.Add(chain);
                return _inFront;
            },
        };
        _rig.Add(_agents);
        Proc(1, 0, "explorer.exe", 1);
    }

    public void Dispose() => _rig.Dispose();

    private void Proc(uint pid, uint parent, string exe, long created, double cpu = 0, string? cmd = null)
    {
        _procs[pid] = new ProcessRow { Key = new ProcKey(pid, created), ParentPid = parent, Exe = exe, CpuTimeS = cpu };
        if (cmd is not null)
        {
            _cmd[pid] = cmd;
        }
    }

    private void Scan() => _agents.Scan(_procs.Values.ToList(), _rig.Clock.UtcNow);

    private void Hook(string session, AgentSessionState state, uint? pid = null, string folder = "proj")
    {
        Directory.CreateDirectory(_rig.Store.Layout.AgentsDir);
        var f = new AgentHookFile { Agent = "claude", SessionId = session, Folder = folder, State = state, Since = _rig.Clock.UtcNow, Pid = pid };
        File.WriteAllBytes(_rig.Store.Layout.AgentFile(session), JsonSerializer.SerializeToUtf8Bytes(f, ContractsJsonContext.Default.AgentHookFile));
        _agents.ReadHooks();
        _rig.Loop.RunPending();
    }

    [Fact]
    public void Agents_match_by_exe_glob_or_script_host_plus_script_name_and_only_the_topmost_counts()
    {
        Proc(10, 1, "claude.exe", 100);
        Proc(11, 10, "claude.exe", 101); // its own helper folds in
        Proc(20, 1, "python.exe", 200, cmd: "python.exe -m aider --model x");
        Proc(21, 1, "python.exe", 210, cmd: "python.exe train.py");
        Proc(30, 1, "AutoHotkey64.exe", 300);
        Scan();
        Assert.Equal(["AutoHotkey64", "aider", "claude"], _agents.Sessions.Select(s => s.Agent).Order(StringComparer.Ordinal).Select(a => a == "AutoHotkey" ? "AutoHotkey64" : a));
    }

    [Fact]
    public void Busy_means_cpu_or_a_new_child_in_the_last_30_seconds_and_sampling_runs_only_while_agents_run()
    {
        Assert.False(_agents.Sampling);
        Proc(10, 1, "claude.exe", 100, cpu: 5);
        Scan();
        Assert.True(_agents.Sampling);
        Assert.Equal(AgentSessionState.Idle, _agents.Sessions.Single().State);

        _procs[10] = _procs[10] with { CpuTimeS = 8 };
        _rig.Advance(AgentWatch.Sample);
        Assert.Equal(AgentSessionState.Busy, _agents.Sessions.Single().State);
        Assert.Equal(0.2, _agents.Sessions.Single().CoresNow, 2);

        _rig.Advance(TimeSpan.FromSeconds(45), AgentWatch.Sample); // no more CPU
        Assert.Equal(AgentSessionState.Idle, _agents.Sessions.Single().State);

        Proc(12, 10, "bash.exe", 120);
        Scan();
        Assert.Equal(AgentSessionState.Busy, _agents.Sessions.Single().State);

        _procs.Remove(10);
        _procs.Remove(12);
        Scan();
        Assert.Empty(_agents.Sessions);
        Assert.False(_agents.Sampling);
    }

    [Fact]
    public void A_reported_state_wins_for_its_process_and_the_card_orders_needs_permission_first()
    {
        Proc(10, 1, "claude.exe", 100);
        Proc(20, 1, "codex.exe", 200);
        Scan();
        Hook("s1", AgentSessionState.NeedsPermission, pid: 10);
        var rows = _rig.State.Current.Agents!;
        Assert.Equal(2, rows.Count);
        Assert.Equal(AgentSessionState.NeedsPermission, rows[0].State);
        Assert.True(rows[0].Reported);
        Assert.Equal("proj", rows[0].Folder);
        Assert.Equal("Codex", rows[1].Agent);
        Assert.Equal("1", _agents.Cards()[0].Blocks[0] is Contracts.Cards.MetricBlock m ? m.Value.ToString() : "");
    }

    [Fact]
    public void The_permission_toast_fires_once_after_2_minutes_unless_the_window_is_in_front()
    {
        _rig.LoadRules();
        Proc(10, 1, "claude.exe", 100);
        Scan();
        Hook("s1", AgentSessionState.NeedsPermission, pid: 10, folder: "webapp");
        _rig.Advance(TimeSpan.FromSeconds(90));
        Assert.DoesNotContain(_rig.Rules.Evaluate(), o => o.Toast is not null);

        _inFront = true;
        _rig.Advance(TimeSpan.FromSeconds(30));
        Assert.DoesNotContain(_rig.Rules.Evaluate(), o => o.Toast is not null);
        Assert.Contains(_front, c => c[0] == 10);

        _inFront = false;
        Scan();
        var toast = Assert.Single(_rig.Rules.Evaluate(), o => o.Toast is not null).Toast!;
        Assert.Equal("Claude needs your permission in webapp", toast.Title);
        Assert.Equal(["agent.show", "alert.snooze"], toast.Buttons.Select(b => b.Request.Action));
        Scan();
        Assert.DoesNotContain(_rig.Rules.Evaluate(), o => o.Toast is not null);

        _rig.Settings.Update(c => c with { Agents = new Contracts.Config.AgentWatchSettings { PermissionToast = false } });
        Hook("s1", AgentSessionState.Working, pid: 10);
        Hook("s1", AgentSessionState.NeedsPermission, pid: 10);
        _rig.Advance(TimeSpan.FromMinutes(3));
        Assert.DoesNotContain(_rig.Rules.Evaluate(), o => o.Toast is not null);
    }

    [Fact]
    public void Ended_sessions_and_sessions_whose_process_exited_expire_and_their_files_go()
    {
        Proc(10, 1, "claude.exe", 100);
        Scan();
        Hook("gone", AgentSessionState.Working, pid: 10);
        Hook("done", AgentSessionState.Ended);
        _procs.Remove(10);
        _rig.Advance(TimeSpan.FromMinutes(2));
        Scan();
        Assert.Empty(_agents.Sessions);
        Assert.Empty(Directory.GetFiles(_rig.Store.Layout.AgentsDir));
    }

    [Fact]
    public void Overlap_counts_busy_sessions_only_and_lower_the_newest_lowers_the_newest_busy_agent()
    {
        _rig.LoadRules("""{ "rules": [ { "id": "overlap", "enabled": true } ] }""");
        for (uint i = 0; i < 4; i++)
        {
            Proc(10 + i, 1, "claude.exe", 100 + i, cpu: 0);
        }

        Scan();
        foreach (var p in _procs.Values.Where(p => p.Exe == "claude.exe" && p.Key.Pid != 13).ToList())
        {
            _procs[p.Key.Pid] = p with { CpuTimeS = 10 };
        }

        _rig.Advance(AgentWatch.Sample);
        var inputs = new RuleInputs();
        _agents.Contribute(inputs, _rig.Clock.UtcNow);
        Assert.Equal(new GroupStatus(4, 3), inputs.Groups["agents"]);
        _rig.Rules.Evaluate();
        _rig.Advance(TimeSpan.FromSeconds(61), AgentWatch.Sample);
        foreach (var p in _procs.Values.Where(p => p.Exe == "claude.exe" && p.Key.Pid != 13).ToList())
        {
            _procs[p.Key.Pid] = p with { CpuTimeS = p.CpuTimeS + 10 };
        }

        _rig.Advance(AgentWatch.Sample);
        var toast = Assert.Single(_rig.Rules.Evaluate(), o => o.Toast is not null).Toast!;
        Assert.Equal("3 agents are busy at once", toast.Title);

        foreach (var p in _procs.Values)
        {
            _rig.ProcessTable[p.Key.Pid] = p;
        }

        var r = _rig.Act(new ActionRequest { Action = "agent.lower_newest", Origin = Origin.Flyout });
        Assert.Equal(ActionStatus.Ok, r.Status);
        Assert.Contains("priority:BelowNormal:12", _rig.Processes.Calls); // 13 is newer but idle
    }

    [Fact]
    public void Hook_setup_shows_the_exact_change_writes_only_on_confirm_keeps_a_backup_and_remove_takes_out_only_its_own()
    {
        var path = Path.Combine(_rig.Store.Root, "claude", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const string Original = """
            {
              "model": "x",
              "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "my-own-script" } ] } ] }
            }
            """;
        File.WriteAllText(path, Original);
        _agents.ClaudeSettingsPath = () => path;

        var first = _rig.Act(new ActionRequest { Action = "agent.hooks.install", Origin = Origin.Flyout });
        Assert.Equal(ActionStatus.NeedsConfirm, first.Status);
        Assert.Contains("+ ", first.Summary, StringComparison.Ordinal);
        Assert.Contains("tasktor agent-hook needs-permission --agent claude", first.Summary, StringComparison.Ordinal);
        Assert.Equal(Original, File.ReadAllText(path)); // nothing written yet

        var done = _rig.Act(new ActionRequest { Action = "agent.hooks.install", Origin = Origin.Flyout, Confirm = first.Token });
        Assert.Equal(ActionStatus.Ok, done.Status);
        Assert.Equal(Original, File.ReadAllText(path + ".bak"));
        var installed = File.ReadAllText(path);
        Assert.Contains("my-own-script", installed, StringComparison.Ordinal);
        Assert.Contains("permission_prompt", installed, StringComparison.Ordinal);
        Assert.Equal(ActionStatus.Ok, _rig.Act(new ActionRequest { Action = "agent.hooks.install", Origin = Origin.Flyout }).Status); // already set up

        var remove = HookInstaller.Remove(installed);
        using var doc = JsonDocument.Parse(remove.NewText);
        Assert.Equal("x", doc.RootElement.GetProperty("model").GetString());
        var stop = doc.RootElement.GetProperty("hooks").GetProperty("Stop");
        Assert.Equal("my-own-script", Assert.Single(stop.EnumerateArray()).GetProperty("hooks")[0].GetProperty("command").GetString());
        Assert.False(doc.RootElement.GetProperty("hooks").TryGetProperty("UserPromptSubmit", out _));
    }

    [Fact]
    public void A_settings_file_that_is_not_json_is_left_alone()
    {
        var path = Path.Combine(_rig.Store.Root, "bad.json");
        File.WriteAllText(path, "{ not json");
        _agents.ClaudeSettingsPath = () => path;
        var r = _rig.Act(new ActionRequest { Action = "agent.hooks.install", Origin = Origin.Flyout });
        Assert.NotEqual(ActionStatus.Ok, r.Status);
        Assert.Equal("{ not json", File.ReadAllText(path));
    }

    [Fact]
    public void The_line_diff_marks_adds_and_removes()
    {
        Assert.Equal(["  a", "- b", "+ c", "  d"], HookInstaller.LineDiff(["a", "b", "d"], ["a", "c", "d"]));
    }
}
