using System.Text;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Jobs;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Snapshot;
using Tasktor.Core.Features;
using Tasktor.Core.Features.Jobs;
using Tasktor.Core.Features.SystemMonitor;
using Tasktor.Core.Learning;
using Tasktor.Tests.Shared;
using AppIdentity = Tasktor.Core.Apps.AppIdentity;
using AppsFeature = Tasktor.Core.Features.SystemMonitor.Apps;

namespace Tasktor.Core.Tests;

/// <summary>A process world the job watch reads: names table + per-process reads + command lines.</summary>
internal sealed class FakeWorld
{
    public Dictionary<uint, ProcessRow> Procs { get; } = [];

    public Dictionary<uint, string> Cmd { get; } = [];

    public int ReadOneCalls { get; private set; }

    public void Add(uint pid, uint parent, string exe, string cmd, long created, double cpuS = 0, ProcessPriority prio = ProcessPriority.Normal)
    {
        Procs[pid] = new ProcessRow { Key = new ProcKey(pid, created), ParentPid = parent, Exe = exe, CpuTimeS = cpuS, Priority = prio };
        Cmd[pid] = cmd;
    }

    public void Cpu(uint pid, double cpuS) => Procs[pid] = Procs[pid] with { CpuTimeS = cpuS };

    public void Io(uint pid, double bytes) => Procs[pid] = Procs[pid] with { IoBytes = bytes };

    public ProcessRow? ReadOne(ProcKey k)
    {
        ReadOneCalls++;
        return Procs.TryGetValue(k.Pid, out var r) && (k.CreatedTicks == 0 || r.Key.CreatedTicks == k.CreatedTicks) ? r : null;
    }

    public string? CommandLine(ProcKey k) => Cmd.GetValueOrDefault(k.Pid);

    public List<ProcessRow> Names() => Procs.Values.Select(r => r with { Key = new ProcKey(r.Key.Pid, 0), CpuTimeS = 0 }).ToList();
}

public sealed class JobWatchTests : IDisposable
{
    private readonly FeatureRig _rig = new();
    private readonly FakeWorld _w = new();
    private readonly FakeTable<ProcKey, ProcessRow> _table = new();
    private readonly AppsFeature _apps;
    private readonly JobWatch _jobs;
    private readonly DateTimeOffset _t0;

    public JobWatchTests()
    {
        _t0 = _rig.Clock.UtcNow;
        _apps = _rig.Add(new AppsFeature(_rig.Context, _table, new AppIdentity(), _ => null));
        _jobs = _rig.Add(new JobWatch(_rig.Context, _apps, _w.ReadOne, _w.CommandLine, null, null, null, new InternalTasks()));
        _w.Add(1, 0, "explorer.exe", "explorer.exe", 1);
    }

    public void Dispose() => _rig.Dispose();

    // ProcKey.CreatedTicks is a Windows FILETIME (what GetProcessTimes returns), not DateTime ticks.
    private long Ticks(TimeSpan since) => (_t0 + since).ToFileTime();

    /// <summary>Advance minute by minute, the process CPU growing at <paramref name="cores"/>, scanning on each tick.</summary>
    private void Run(uint pid, double cores, TimeSpan span, TimeSpan? step = null)
    {
        var s = step ?? TimeSpan.FromMinutes(1);
        var end = _rig.Clock.UtcNow + span;
        while (_rig.Clock.UtcNow < end)
        {
            _rig.Clock.Advance(s);
            if (pid != 0 && _w.Procs.ContainsKey(pid))
            {
                _w.Cpu(pid, _w.Procs[pid].CpuTimeS + (cores * s.TotalSeconds));
            }

            _jobs.Scan(_w.Names(), _rig.Clock.UtcNow);
            _jobs.ReadJobs(_rig.Clock.UtcNow);
            _rig.Loop.RunPending();
        }

        foreach (var kv in _w.Procs)
        {
            _rig.ProcessTable[kv.Key] = kv.Value; // what the guards re-read at action time
        }
    }

    private Job? JobOf(uint pid) => _jobs.Jobs.FirstOrDefault(j => j.Root.Pid == pid);

    [Fact]
    public void A_busy_script_becomes_a_job_within_3_minutes_and_an_idle_stdio_helper_never_does()
    {
        // B1 acceptance 1.
        _w.Add(100, 1, "python.exe", "python.exe train.py --fold 3", Ticks(TimeSpan.Zero));
        _w.Add(200, 1, "node.exe", "node.exe server.js --stdio", Ticks(TimeSpan.Zero));
        Run(100, 1.0, TimeSpan.FromMinutes(3));
        var job = Assert.Single(_jobs.Jobs);
        Assert.Equal("train.py", job.Label);
        Run(200, 0.001, TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(5));
        Assert.Null(JobOf(200));
    }

    [Fact]
    public void A_pinned_program_off_the_watch_list_becomes_a_job_on_the_next_scan()
    {
        // The backend check found job.pin answered "Watching ... as a job" while the scan, which only looked at the watch
        // list, never considered the process.
        _w.Add(400, 1, "render-tool.exe", "render-tool.exe --scene a", Ticks(TimeSpan.Zero));
        _rig.ProcessTable[400] = _w.Procs[400];
        Run(0, 0, TimeSpan.FromMinutes(2));
        Assert.Null(JobOf(400)); // not on the watch list: never a job by itself
        var pin = _rig.Act(new ActionRequest { Action = "job.pin", Origin = Origin.Flyout, Target = new Dictionary<string, string> { ["pid"] = "400" } });
        Assert.Equal(ActionStatus.Ok, pin.Status);
        Run(0, 0, TimeSpan.FromMinutes(1));
        Assert.NotNull(JobOf(400));

        // A reused PID is a different process: the pin does not carry over.
        _w.Procs.Remove(400);
        _w.Add(400, 1, "render-tool.exe", "render-tool.exe --scene b", Ticks(TimeSpan.FromMinutes(10)));
        _jobs.OnExit(new ProcessExit(new ProcKey(400, Ticks(TimeSpan.Zero)), 0, _rig.Clock.UtcNow));
        Run(0, 0, TimeSpan.FromMinutes(2));
        Assert.DoesNotContain(_jobs.Jobs, j => j.Live && j.Root.Pid == 400 && j.Root.CreatedTicks == Ticks(TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void A_job_started_when_its_process_was_created()
    {
        // The pin check on Windows showed started_at in the year 0426: the FILETIME creation time was read as DateTime
        // ticks, so every duration (and every learned usual duration) was about 1,600 years.
        _w.Add(100, 1, "python.exe", "python.exe train.py", Ticks(TimeSpan.Zero));
        Run(100, 1.0, TimeSpan.FromMinutes(3));
        Assert.Equal(_t0, JobOf(100)!.StartedAt);
    }

    [Fact]
    public void A_fresh_busy_process_waits_out_the_minimum_age()
    {
        _rig.Clock.Advance(TimeSpan.FromMinutes(10));
        _w.Add(100, 1, "python.exe", "python.exe train.py", _rig.Clock.UtcNow.ToFileTime());
        Run(100, 2.0, TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(10));
        Assert.Null(JobOf(100));
    }

    [Fact]
    public void Creation_time_reads_as_a_filetime()
    {
        var at = new DateTimeOffset(2026, 11, 13, 18, 54, 1, TimeSpan.Zero);
        Assert.Equal(at, new ProcKey(1, at.ToFileTime()).CreatedUtc);
        Assert.Null(new ProcKey(1, 0).CreatedUtc);
    }

    [Fact]
    public void The_old_total_cpu_floor_is_gone_a_long_idle_helper_is_not_a_job()
    {
        _w.Add(300, 1, "node.exe", "node.exe codex-runtime.js", Ticks(TimeSpan.FromHours(-15)), cpuS: 25.5);
        Run(300, 0.0005, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(1));
        Assert.Empty(_jobs.Jobs);
    }

    [Fact]
    public void A_job_that_drops_to_zero_stays_listed_until_it_exits_with_its_code()
    {
        // B1 acceptance 2 + 4.
        _w.Add(100, 1, "python.exe", "python.exe train.py", Ticks(TimeSpan.Zero));
        Run(100, 1.0, TimeSpan.FromMinutes(3));
        Run(100, 0, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(1));
        var job = JobOf(100)!;
        Assert.True(job.Live);
        _jobs.OnExit(new ProcessExit(job.Root, 3, _rig.Clock.UtcNow));
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(3, _rig.State.Current.Jobs!.Single(r => r.Id == job.Id).ExitCode);
    }

    [Fact]
    public void Workers_fold_under_the_root_and_cores_are_their_sum()
    {
        // B1 acceptance 3 (multiprocessing: the root idles, the workers work).
        _w.Add(100, 1, "python.exe", "python.exe train.py", Ticks(TimeSpan.Zero));
        for (uint i = 0; i < 4; i++)
        {
            _w.Add(101 + i, 100, "python.exe", "python.exe -c worker", Ticks(TimeSpan.FromSeconds(5)));
        }

        var end = _rig.Clock.UtcNow + TimeSpan.FromMinutes(3);
        while (_rig.Clock.UtcNow < end)
        {
            _rig.Clock.Advance(TimeSpan.FromSeconds(10));
            for (uint i = 0; i < 4; i++)
            {
                _w.Cpu(101 + i, _w.Procs[101 + i].CpuTimeS + 10);
            }

            _jobs.Scan(_w.Names(), _rig.Clock.UtcNow);
            _jobs.ReadJobs(_rig.Clock.UtcNow);
        }

        var job = Assert.Single(_jobs.Jobs);
        Assert.Equal(100u, job.Root.Pid);
        Assert.Equal(4, job.Workers.Count);
        Assert.Equal(4.0, job.CoresNow, 1);
    }

    [Fact]
    public void Hogging_needs_normal_priority_half_the_cpus_and_a_busy_system_for_2_minutes()
    {
        // B1 acceptance 5.
        var half = Environment.ProcessorCount / 2.0;
        _rig.State.Update(s => s with { System = new SystemState { CpuPct = 85 } });
        _w.Add(100, 1, "python.exe", "python.exe big.py", Ticks(TimeSpan.Zero));
        Run(100, half, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(10));
        Assert.True(JobOf(100)!.Hogging);
        _rig.State.Update(s => s with { System = new SystemState { CpuPct = 50 } });
        Run(100, half, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10));
        Assert.False(JobOf(100)!.Hogging);
        _w.Add(200, 1, "python.exe", "python.exe low.py", Ticks(TimeSpan.FromMinutes(5)), prio: ProcessPriority.BelowNormal);
        _rig.State.Update(s => s with { System = new SystemState { CpuPct = 85 } });
        Run(200, half, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(10));
        Assert.False(JobOf(200)!.Hogging);
    }

    [Fact]
    public void With_no_job_and_no_viewer_one_names_only_scan_per_minute_and_no_handles_for_unwatched()
    {
        // B1 acceptance 6 / D-B8 acceptance 1.
        for (uint i = 0; i < 50; i++)
        {
            _w.Add(500 + i, 1, "app" + i + ".exe", "app.exe", Ticks(TimeSpan.Zero));
        }

        _table.Rows = _w.Names().Select(r => new KeyValuePair<ProcKey, ProcessRow>(r.Key, r)).ToList();
        _rig.Advance(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(30));
        Assert.InRange(_table.Reads.Count, 10, 11);
        Assert.All(_table.Reads, c => Assert.Equal(ColumnSet.Names, c));
        Assert.Equal(0, _w.ReadOneCalls); // no handle opened: nothing is on the watch list
    }

    [Fact]
    public void Eta_appears_after_3_good_runs_as_their_median()
    {
        // B1 acceptance 8.
        var kind = JobGate.Kind(_rig.Context.Key!.Hasher("job.kind"), "python.exe", "python.exe train.py");
        foreach (var d in new[] { 3000.0, 4200, 3600 })
        {
            Assert.Null(_jobs.Learner.Eta(kind));
            _jobs.Learner.OnRunEnded(kind, new RunOutcome { Good = true, DurationS = d }, false, _rig.Clock.UtcNow);
        }

        Assert.Equal(3600, _jobs.Learner.Eta(kind)!.Value);
    }

    [Fact]
    public void A_new_kind_goes_silent_after_10_minutes_toasts_once_and_clears_on_resume()
    {
        // B2 acceptance 1 (fallback).
        _rig.LoadRules();
        _w.Add(100, 1, "python.exe", "python.exe embed.py", Ticks(TimeSpan.Zero));
        Run(100, 1.0, TimeSpan.FromMinutes(3));
        Run(100, 0, TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(1));
        _rig.Rules.Evaluate();
        Assert.Empty(_rig.Notifier.Shown);
        Run(100, 0, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(1));
        _rig.Rules.Evaluate();
        var t = Assert.Single(_rig.Notifier.Shown);
        Assert.StartsWith("embed.py has been silent for 1", t.Title, StringComparison.Ordinal);
        Assert.EndsWith("new job, default limit", t.Title, StringComparison.Ordinal);
        Assert.Equal(["Show", "Open log", "It's fine"], t.Buttons.Select(b => b.Label));
        Assert.DoesNotContain(t.Buttons, b => b.Request.Action.Contains("end", StringComparison.Ordinal));
        Run(100, 0, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1));
        _rig.Rules.Evaluate();
        Assert.Single(_rig.Notifier.Shown);
        Run(100, 1, TimeSpan.FromMinutes(1));
        Assert.Equal(JobStatus.Working, JobOf(100)!.Status);
    }

    [Fact]
    public void Log_only_activity_counts_as_work()
    {
        // B2 acceptance 2: a job that only writes (I/O, no CPU) is never silent.
        _w.Add(100, 1, "python.exe", "python.exe dl.py", Ticks(TimeSpan.Zero));
        Run(100, 1.0, TimeSpan.FromMinutes(3));
        for (var i = 0; i < 30; i++)
        {
            _w.Io(100, _w.Procs[100].IoBytes + (1024 * 1024));
            Run(100, 0, TimeSpan.FromMinutes(1));
        }

        Assert.NotEqual(JobStatus.Silent, JobOf(100)!.Status);
    }

    [Fact]
    public void A_busy_gpu_keeps_it_working()
    {
        // B2 acceptance 3.
        _jobs.GpuOf = _ => 80;
        _w.Add(100, 1, "python.exe", "python.exe train.py", Ticks(TimeSpan.Zero));
        Run(100, 1.0, TimeSpan.FromMinutes(3));
        Run(100, 0, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(1));
        Assert.Equal(JobStatus.WorkingGpu, JobOf(100)!.Status);
    }

    [Fact]
    public void Stuck_busy_uses_the_writers_heartbeat()
    {
        // B2 acceptance 4: heartbeat 30 → flagged after 60 s, not 10 min; without a progress file never stuck.
        _w.Add(100, 1, "python.exe", "python.exe loop.py", Ticks(TimeSpan.Zero));
        Run(100, 1.0, TimeSpan.FromMinutes(3));
        var row = ProgressParser.Parse("loop", Encoding.UTF8.GetBytes($$"""{"schema":1,"id":"loop","pid":100,"pct":5,"heartbeat_s":30,"updated":"{{_rig.Clock.UtcNow:O}}"}"""), _rig.Clock.UtcNow);
        _jobs.OnProgress([row], []);
        Run(100, 1.0, TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(10));
        Assert.Equal(JobStatus.Working, JobOf(100)!.Status);
        Run(100, 1.0, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10));
        Assert.Equal(JobStatus.StuckBusy, JobOf(100)!.Status);
        _w.Add(200, 1, "python.exe", "python.exe loop2.py", Ticks(_rig.Clock.UtcNow - _t0));
        Run(200, 1.0, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(1));
        Assert.Equal(JobStatus.Working, JobOf(200)!.Status);
    }

    [Fact]
    public void Finished_toast_only_for_long_jobs_not_in_front()
    {
        // B2 acceptance 5.
        _w.Add(100, 1, "python.exe", "python.exe long.py", Ticks(TimeSpan.Zero));
        Run(100, 1.0, TimeSpan.FromMinutes(15));
        _jobs.OnExit(new ProcessExit(JobOf(100)!.Root, 1, _rig.Clock.UtcNow));
        var t = Assert.Single(_rig.Notifier.Shown);
        Assert.Equal("long.py failed after 15 min (exit 1)", t.Title);

        _w.Add(200, 1, "python.exe", "python.exe short.py", Ticks(_rig.Clock.UtcNow - _t0));
        Run(200, 1.0, TimeSpan.FromMinutes(5));
        _jobs.OnExit(new ProcessExit(JobOf(200)!.Root, 0, _rig.Clock.UtcNow));
        Assert.Single(_rig.Notifier.Shown);

        _jobs.WindowInFront = _ => true;
        _w.Add(300, 1, "python.exe", "python.exe front.py", Ticks(_rig.Clock.UtcNow - _t0));
        Run(300, 1.0, TimeSpan.FromMinutes(15));
        _jobs.OnExit(new ProcessExit(JobOf(300)!.Root, 0, _rig.Clock.UtcNow));
        Assert.Single(_rig.Notifier.Shown);
    }

    [Fact]
    public void Its_fine_labels_a_false_alarm_and_silences_the_rest_of_the_run()
    {
        // B2 acceptance 7.
        _rig.LoadRules();
        _w.Add(100, 1, "python.exe", "python.exe embed.py", Ticks(TimeSpan.Zero));
        Run(100, 1.0, TimeSpan.FromMinutes(3));
        Run(100, 0, TimeSpan.FromMinutes(11), TimeSpan.FromMinutes(1));
        _rig.Rules.Evaluate();
        var button = _rig.Notifier.Shown.Single().Buttons.Single(b => b.Label == "It's fine");
        Assert.Equal(ActionStatus.Ok, _rig.Act(button.Request).Status);
        Assert.Equal(1, _jobs.Learner.Store.Find(JobOf(100)!.Kind)!.FalseAlarms);
        Run(100, 0, TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(5));
        _rig.Rules.Evaluate();
        Assert.Single(_rig.Notifier.Shown);
    }

    [Fact]
    public void Freeze_and_polite_pause_and_paused_time_is_not_quiet_time()
    {
        // B3 acceptance 8 / 10 / 11.
        _w.Add(100, 1, "python.exe", "python.exe train.py", Ticks(TimeSpan.Zero));
        _w.Add(101, 100, "python.exe", "python.exe -c worker", Ticks(TimeSpan.FromSeconds(1)));
        Run(100, 1.0, TimeSpan.FromMinutes(3));
        var job = JobOf(100)!;
        var target = new Dictionary<string, string> { ["job"] = job.Id };
        Assert.Equal(ActionStatus.Ok, _rig.Act(new ActionRequest { Action = "job.pause", Origin = Origin.Flyout, Target = target }).Status);
        Assert.Equal(PauseMode.Frozen, job.Pause); // no progress file: freeze
        Assert.Equal(2, _rig.Processes.Frozen.Count);
        Run(100, 0, TimeSpan.FromMinutes(40), TimeSpan.FromMinutes(1));
        Assert.Equal(JobStatus.Paused, job.Status);
        Assert.Equal(ActionStatus.Ok, _rig.Act(new ActionRequest { Action = "job.resume", Origin = Origin.Flyout, Target = target }).Status);
        Assert.Empty(_rig.Processes.Frozen);
        Assert.True(job.PausedTotal >= TimeSpan.FromMinutes(39));
        Assert.Equal(JobStatus.Working, job.Status);

        // polite pause writes the control file the helper reads
        var row = ProgressParser.Parse("train", Encoding.UTF8.GetBytes($$"""{"schema":1,"id":"train","pid":100,"updated":"{{_rig.Clock.UtcNow:O}}"}"""), _rig.Clock.UtcNow);
        _jobs.OnProgress([row], []);
        Assert.Equal(ActionStatus.Ok, _rig.Act(new ActionRequest { Action = "job.pause", Origin = Origin.Flyout, Target = target }).Status);
        Assert.Equal(PauseMode.Polite, job.Pause);
        Assert.Contains("\"pause\":true", File.ReadAllText(_rig.Store.Layout.JobControlFile("train")), StringComparison.Ordinal);
    }

    [Fact]
    public void False_alarms_above_30_percent_switch_to_the_default_until_5_clean_runs()
    {
        // B adaptive acceptance 4.
        var k = new KindId(1, 2);
        var now = _rig.Clock.UtcNow;
        for (var i = 0; i < 20; i++)
        {
            _jobs.Learner.OnRunEnded(k, new RunOutcome { Good = true, QuietMaxS = 600, DurationS = 3600 }, false, now);
        }

        for (var i = 0; i < 3; i++)
        {
            _jobs.Learner.OnAlarmLabelled(k, falseAlarm: false, shadowFalse: false, now);
        }

        _jobs.Learner.OnAlarmLabelled(k, falseAlarm: true, shadowFalse: true, now); // 1 of 4 = 25 %
        Assert.Equal(PolicyMode.Learned, _jobs.Learner.Store.Find(k)!.Mode);
        _jobs.Learner.OnAlarmLabelled(k, falseAlarm: true, shadowFalse: true, now); // 2 of 5 = 40 %
        Assert.Equal(PolicyMode.Fallback, _jobs.Learner.Store.Find(k)!.Mode);
        Assert.Equal(ValueSource.FallbackGuardrail, _jobs.Learner.SilentFor(k, false).Source);
        Assert.Contains(_jobs.Learner.Guardrails, g => g.Action == "switched to default");
        for (var i = 0; i < 4; i++)
        {
            _jobs.Learner.OnRunEnded(k, new RunOutcome { Good = true, QuietMaxS = 600, DurationS = 3600 }, false, now);
        }

        Assert.Equal(PolicyMode.Fallback, _jobs.Learner.Store.Find(k)!.Mode);
        _jobs.Learner.OnRunEnded(k, new RunOutcome { Good = true, QuietMaxS = 600, DurationS = 3600 }, false, now);
        Assert.Equal(PolicyMode.Learned, _jobs.Learner.Store.Find(k)!.Mode);
    }

    [Fact]
    public void An_out_of_distribution_run_is_detected()
    {
        // B adaptive acceptance 5 (P1).
        var k = new KindId(3, 4);
        for (var i = 0; i < 10; i++)
        {
            _jobs.Learner.OnRunEnded(k, new RunOutcome { Good = true, CoresMedian = 1.0, DurationS = 100 }, false, _rig.Clock.UtcNow);
        }

        Assert.False(_jobs.Learner.OutOfDistribution(k, 1.2));
        Assert.True(_jobs.Learner.OutOfDistribution(k, 8.0));
        Assert.Equal(ValueSource.FallbackOutOfRange, _jobs.Learner.SilentFor(k, true).Source);
    }

    [Fact]
    public void Job_kinds_file_holds_no_names()
    {
        // B adaptive acceptance 6.
        _w.Add(100, 1, "python.exe", "python.exe secret_project_train.py", Ticks(TimeSpan.Zero));
        Run(100, 1.0, TimeSpan.FromMinutes(3));
        _jobs.OnExit(new ProcessExit(JobOf(100)!.Root, 0, _rig.Clock.UtcNow));
        var bytes = File.ReadAllBytes(_rig.Store.Layout.JobKinds);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("python", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_257th_kind_evicts_the_least_recently_used()
    {
        // B adaptive acceptance 7.
        var store = new KindStore("fp");
        var t = _rig.Clock.UtcNow;
        for (ulong i = 0; i < 257; i++)
        {
            store.Get(new KindId(0, i), t.AddSeconds(i));
        }

        Assert.Equal(256, store.Count);
        Assert.Null(store.Find(new KindId(0, 0)));
        Assert.InRange(store.Serialize().Length, 1, 140_000);
    }

    [Fact]
    public void Learning_off_means_every_policy_uses_its_fallback()
    {
        // B adaptive acceptance 1.
        var k = new KindId(5, 6);
        for (var i = 0; i < 20; i++)
        {
            _jobs.Learner.OnRunEnded(k, new RunOutcome { Good = true, QuietMaxS = 1800, DurationS = 3600 }, false, _rig.Clock.UtcNow);
        }

        _jobs.Learner.LearningOn = false;
        Assert.Equal(600, _jobs.Learner.SilentFor(k, false).Value);
        Assert.Null(_jobs.Learner.Eta(k));
    }

    [Fact]
    public void A_progress_file_without_pid_is_its_own_row_and_names_a_process_to_bypass_the_gate()
    {
        var file = ProgressParser.Parse("nightly", Encoding.UTF8.GetBytes($$"""{"schema":1,"id":"nightly","label":"backup","pct":42.5,"updated":"{{_rig.Clock.UtcNow:O}}"}"""), _rig.Clock.UtcNow);
        _jobs.OnProgress([file], []);
        var row = Assert.Single(_rig.State.Current.Jobs!);
        Assert.Equal("backup", row.Label);
        Assert.Equal(42.5, row.Pct);

        // a non-watch-list exe named by a progress file is a job at once
        _w.Add(700, 1, "render.exe", "render.exe scene", Ticks(TimeSpan.Zero));
        var named = ProgressParser.Parse("scene", Encoding.UTF8.GetBytes($$"""{"schema":1,"id":"scene","pid":700,"pct":10,"updated":"{{_rig.Clock.UtcNow:O}}"}"""), _rig.Clock.UtcNow);
        _jobs.OnProgress([named], []);
        Run(0, 0, TimeSpan.FromMinutes(1));
        Assert.NotNull(JobOf(700));
    }
}

public sealed class ProgressFileTests : IDisposable
{
    private readonly TempStore _store = new();

    public void Dispose() => _store.Dispose();

    private static readonly DateTimeOffset Now = new(2026, 11, 4, 14, 2, 30, TimeSpan.Zero);

    [Fact]
    public void The_spec_example_parses_to_a_42_percent_row()
    {
        // B4 acceptance 1 (parse part).
        var bytes = File.ReadAllBytes(Path.Combine(RepoPaths.Fixtures, "tasktor.job-1", "spec-example.json"));
        var row = ProgressParser.Parse("embed-2026-11-04", bytes, Now);
        Assert.Null(row.Invalid);
        Assert.Equal(42.01, row.Pct!.Value, 2); // done / total wins over pct
        Assert.Equal("embed corpus", row.Label);
        Assert.Equal(30, row.HeartbeatS);
    }

    [Theory]
    [InlineData("""{"schema":2,"id":"a","updated":"2026-11-04T14:02:11Z"}""", "schema")]
    [InlineData("""{"schema":1,"id":"b","updated":"2026-11-04T14:02:11Z"}""", "id must equal")]
    [InlineData("""{"schema":1,"id":"a","log":"https://x/y.log","updated":"2026-11-04T14:02:11Z"}""", "local file")]
    [InlineData("""{"schema":1,"id":"a","updated":"2026-11-04T14:02:11Z""", "not valid")]
    public void Invalid_files_become_a_row_that_says_why(string json, string why)
    {
        var row = ProgressParser.Parse("a", Encoding.UTF8.GetBytes(json), Now);
        Assert.Contains(why, row.Invalid, StringComparison.Ordinal);
    }

    [Fact]
    public void A_1_mb_file_is_rejected_unread_and_unknown_fields_are_fine()
    {
        // B4 acceptance 3.
        Assert.Contains("4 KB", ProgressParser.Parse("a", new byte[1024 * 1024], Now).Invalid, StringComparison.Ordinal);
        Assert.Null(ProgressParser.Parse("a", Encoding.UTF8.GetBytes("""{"schema":1,"id":"a","updated":"2026-11-04T14:02:11Z","my_extra":7}"""), Now).Invalid);
    }

    [Fact]
    public void Future_timestamps_count_as_now_and_heartbeat_is_clamped()
    {
        var row = ProgressParser.Parse("a", Encoding.UTF8.GetBytes("""{"schema":1,"id":"a","heartbeat_s":1,"updated":"2030-01-01T00:00:00Z"}"""), Now);
        Assert.Equal(Now, row.UpdatedUtc);
        Assert.Equal(5, row.HeartbeatS);
    }

    [Fact]
    public void Half_written_files_are_never_read_and_a_rescan_loses_nothing()
    {
        // B4 acceptance 2 + 5.
        var dir = Path.Combine(_store.Root, "jobs");
        Directory.CreateDirectory(dir);
        var got = new List<ProgressRow>();
        using var src = new JobProgressSource(dir, () => DateTimeOffset.UtcNow);
        src.Start((rows, _) =>
        {
            lock (got)
            {
                got.AddRange(rows);
            }
        });
        File.WriteAllText(Path.Combine(dir, "a.json.tmp"), """{"schema":1,"id":"a","updated":"2026-11-04T14:02:11Z"}""");
        for (var i = 0; i < 20; i++)
        {
            File.WriteAllText(Path.Combine(dir, "j" + i + ".json"), $$"""{"schema":1,"id":"j{{i}}","updated":"2026-11-04T14:02:11Z"}""");
        }

        src.Rescan(overflow: true); // as after a watch-buffer overflow
        lock (got)
        {
            Assert.DoesNotContain(got, r => r.Id == "a");
            Assert.Equal(20, got.Select(r => r.Id).Distinct().Count());
        }

        Assert.Equal(1, src.Overflows);
    }

    [Fact]
    public void A_vanished_or_unusable_folder_never_throws_and_the_watch_recovers()
    {
        // The watcher's error callback runs on a pool thread: an exception there ends the process. Deleting the folder
        // under the watch (uninstall, a cleaned temp store) must be survived, and the folder recreated when possible.
        var dir = Path.Combine(_store.Root, "jobs");
        var got = new List<ProgressRow>();
        var src = new JobProgressSource(dir, () => DateTimeOffset.UtcNow);
        src.Start((rows, _) =>
        {
            lock (got)
            {
                got.AddRange(rows);
            }
        });

        Directory.Delete(dir, recursive: true);
        File.WriteAllText(dir, "not a folder"); // the folder cannot be recreated for now
        Assert.False(src.Rescan(overflow: true));
        src.Tick(); // the timer retries; still blocked, still no throw

        File.Delete(dir);
        src.Tick(); // recreated and watched again
        Assert.True(Directory.Exists(dir));
        File.WriteAllText(Path.Combine(dir, "j.json"), """{"schema":1,"id":"j","updated":"2026-11-04T14:02:11Z"}""");
        Assert.True(src.Rescan(overflow: false));
        lock (got)
        {
            Assert.Contains(got, r => r.Id == "j");
        }

        src.Dispose();
        int atDispose;
        lock (got)
        {
            // The watcher's own event for j.json may also have posted j before Dispose (a second, legitimate row):
            // what matters is that nothing is posted once Dispose has returned.
            atDispose = got.Count;
        }

        Directory.Delete(dir, recursive: true);
        File.WriteAllText(dir, "gone");
        Assert.False(src.Rescan(overflow: true)); // after Dispose: nothing runs, nothing is posted
        src.Tick();
        Thread.Sleep(200); // room for any late watcher callback to (wrongly) post
        lock (got)
        {
            Assert.Equal(atDispose, got.Count);
            Assert.All(got, r => Assert.Equal("j", r.Id));
        }

        File.Delete(dir);
    }
}

/// <summary>B3.7 crash-safe freeze: intent before the first suspend, threads as they happen, cleared last, thawed at start.</summary>
public sealed class FrozenRegistryTests : IDisposable
{
    private readonly TempStore _store = new();

    public void Dispose() => _store.Dispose();

    private string Path => _store.Layout.Frozen;

    [Fact]
    public void The_intent_and_every_suspended_thread_survive_an_agent_crash()
    {
        var before = new FrozenRegistry(Path);
        before.Begin("job-1", [new ProcKey(10, 100), new ProcKey(11, 110)], DateTimeOffset.UnixEpoch);
        before.AddThread("job-1", 10, 1001);
        before.AddThread("job-1", 11, 1101);

        // The agent dies here. A new process reads the same file.
        var after = new FrozenRegistry(Path);
        var job = Assert.Single(after.Jobs);
        Assert.Equal("job-1", job.JobId);
        Assert.Equal([new FrozenThread(10, 1001), new FrozenThread(11, 1101)], job.Threads);
        Assert.True(after.IsFrozen(11));
        Assert.False(after.IsFrozen(12));
    }

    [Fact]
    public void Recovery_thaws_exactly_the_recorded_processes_and_clears_the_file()
    {
        var crashed = new FrozenRegistry(Path);
        crashed.Begin("a", [new ProcKey(10, 100)], DateTimeOffset.UnixEpoch);
        crashed.Begin("b", [new ProcKey(20, 200), new ProcKey(21, 210)], DateTimeOffset.UnixEpoch);

        var thawed = new List<string>();
        var ids = new FrozenRegistry(Path).Recover((id, keys) => thawed.Add(id + ":" + string.Join(",", keys.Select(k => $"{k.Pid}/{k.CreatedTicks}"))));

        Assert.Equal(["a", "b"], ids);
        Assert.Equal(["a:10/100", "b:20/200,21/210"], thawed);
        Assert.Empty(new FrozenRegistry(Path).Jobs); // a second start finds nothing to thaw
    }

    [Fact]
    public void A_freeze_that_died_before_any_suspend_is_still_recovered()
    {
        new FrozenRegistry(Path).Begin("half", [new ProcKey(30, 300)], DateTimeOffset.UnixEpoch);
        var calls = 0;
        Assert.Equal(["half"], new FrozenRegistry(Path).Recover((_, _) => calls++));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void A_throwing_thaw_keeps_the_record_for_the_next_start()
    {
        new FrozenRegistry(Path).Begin("x", [new ProcKey(40, 400)], DateTimeOffset.UnixEpoch);
        Assert.Throws<InvalidOperationException>(() => new FrozenRegistry(Path).Recover((_, _) => throw new InvalidOperationException()));
        Assert.Single(new FrozenRegistry(Path).Jobs);
    }

    [Fact]
    public void A_torn_file_reads_as_empty_and_does_not_throw()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, "{\"jobs\": [ {\"job_id\": ");
        Assert.Empty(new FrozenRegistry(Path).Jobs);
    }

    [Fact]
    public void Thaw_clears_only_its_own_entry()
    {
        var r = new FrozenRegistry(Path);
        r.Begin("a", [new ProcKey(1, 1)], DateTimeOffset.UnixEpoch);
        r.Begin("b", [new ProcKey(2, 2)], DateTimeOffset.UnixEpoch);
        r.Remove("a");
        Assert.Equal(["b"], new FrozenRegistry(Path).Jobs.Select(j => j.JobId));
    }

    [Fact]
    public void Job_watch_asks_process_control_to_recover_once_at_start()
    {
        using var rig = new FeatureRig();
        rig.Processes.LeftFrozen.Add("job-left");
        var apps = rig.Add(new AppsFeature(rig.Context, new FakeTable<ProcKey, ProcessRow>(), new AppIdentity(), _ => null));
        rig.Add(new JobWatch(rig.Context, apps, _ => null, _ => null, null, null, null, new InternalTasks()));
        rig.Loop.RunPending();
        Assert.Equal(1, rig.Processes.Calls.Count(c => c == "recover"));
        Assert.Empty(rig.Processes.LeftFrozen);
    }
}
