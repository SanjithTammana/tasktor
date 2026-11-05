using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Platform;
using AppIdentity = Tasktor.Core.Apps.AppIdentity;
using AppsFeature = Tasktor.Core.Features.SystemMonitor.Apps;
using Tasktor.Core.Features;
using Tasktor.Core.Features.SystemMonitor;
using Tasktor.Core.Rules;
using Tasktor.Tests.Shared;

namespace Tasktor.Core.Tests;

/// <summary>A metric source that returns whatever the test sets.</summary>
internal sealed class FakeMetricSource : IMetricSource
{
    public Dictionary<MetricKey, double> Values { get; } = [];

    public int Reads { get; private set; }

    public string Name => "fake";

    public void Read(MetricFrame frame)
    {
        Reads++;
        foreach (var kv in Values)
        {
            frame.Set(kv.Key, kv.Value);
        }
    }

    public FakeMetricSource Set(string id, double v, string instance = "")
    {
        Values[new MetricKey(id, instance)] = v;
        return this;
    }

    public FakeMetricSource Machine(double cpu = 20, double ramUsedPct = 50, double commitPct = 40)
    {
        Set(MetricIds.CpuPct, cpu);
        Set(MetricIds.RamTotalMb, 16384);
        Set(MetricIds.RamAvailableMb, 16384 * (1 - (ramUsedPct / 100)));
        Set(MetricIds.CommitLimitBytes, 1000);
        Set(MetricIds.CommitBytes, commitPct * 10);
        return this;
    }
}

internal sealed class FakeTable<TKey, TRow> : ITableSource<TKey, TRow>
    where TKey : notnull
{
    public List<KeyValuePair<TKey, TRow>> Rows { get; set; } = [];

    public List<ColumnSet> Reads { get; } = [];

    public string Name => "fake-table";

    public void Read(ICollection<KeyValuePair<TKey, TRow>> into, ColumnSet columns)
    {
        Reads.Add(columns);
        foreach (var r in Rows)
        {
            into.Add(r);
        }
    }
}

public sealed class SystemTotalsTests : IDisposable
{
    private readonly FeatureRig _rig = new();
    private readonly FakeMetricSource _src = new();
    private readonly SystemTotals _totals;

    public SystemTotalsTests()
    {
        _src.Machine();
        _totals = _rig.Add(new SystemTotals(_rig.Context, _src));
        _rig.Pump();
    }

    public void Dispose() => _rig.Dispose();

    [Fact]
    public void Ticks_every_minute_and_fills_the_hour_ring()
    {
        // A1 acceptance 3: after 60 min with no viewer, the flyout shows 60 tick points immediately.
        _rig.Advance(TimeSpan.FromMinutes(70), TimeSpan.FromSeconds(30));
        Assert.Equal(60, _totals.HourRing["system.cpu_pct"].Count);
        Assert.InRange(_src.Reads, 70, 72); // the tick only: once a minute
        var series = _rig.Pulls["series"](new Contracts.Pipe.PullMessage { Table = "series", Args = new Dictionary<string, string> { ["id"] = "system.cpu_pct" } })!;
        Assert.Equal(60, series["hour"]!.AsArray().Count);
    }

    [Fact]
    public void Ram_used_percent_leads_and_commit_is_a_percent_of_the_limit()
    {
        _src.Machine(cpu: 34, ramUsedPct: 71, commitPct: 64);
        _rig.Advance(TimeSpan.FromMinutes(1));
        var s = _rig.State.Current.System!;
        Assert.Equal(34, s.CpuPct);
        Assert.Equal(71, s.RamUsedPct);
        Assert.Equal(64, s.CommitPct);
        Assert.Equal(16, s.RamTotalGb);
    }

    [Fact]
    public void No_gpu_counters_hide_the_gpu_row_and_an_idle_virtual_adapter_is_hidden()
    {
        // A1 acceptance 5.
        _rig.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(_rig.State.Current.System!.Gpu);
        Assert.DoesNotContain(_totals.Cards()[0].Blocks.OfType<MetricBlock>(), m => m.Label == "GPU");
        _src.Set(MetricIds.GpuPct, 12, "luid_a").Set(MetricIds.GpuDedicatedMb, 128, "luid_a")
            .Set(MetricIds.GpuPct, 0, "luid_b").Set(MetricIds.GpuSharedMb, 0.008, "luid_b");
        _rig.Advance(TimeSpan.FromMinutes(1));
        var gpu = Assert.Single(_rig.State.Current.System!.Gpu!);
        Assert.Equal("luid_a", gpu.Id);
        Assert.Contains(_totals.Cards()[0].Blocks.OfType<MetricBlock>(), m => m.Label == "GPU");
    }

    [Fact]
    public void Network_rates_come_from_cumulative_counters()
    {
        _src.Set(MetricIds.NetRxBytes, 1000, "eth").Set(MetricIds.NetTxBytes, 500, "eth");
        _rig.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(_rig.State.Current.System!.Net); // first read: no rate yet
        _src.Set(MetricIds.NetRxBytes, 1000 + (60 * 2048), "eth").Set(MetricIds.NetTxBytes, 500 + (60 * 512), "eth");
        _rig.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(2048, _rig.State.Current.System!.Net!.RxBps);
        Assert.Equal(512, _rig.State.Current.System!.Net!.TxBps);
    }

    [Fact]
    public void Colour_changes_only_after_the_value_holds_10_seconds()
    {
        using var viewer = _rig.Scheduler.Lease(SystemTotals.SamplerName, TimeSpan.FromSeconds(1), ColumnSet.All);
        _src.Machine(cpu: 97);
        _rig.Advance(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1));
        Assert.Equal(Severity.Normal, _totals.RowSeverity);
        _rig.Advance(TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(1));
        Assert.Equal(Severity.Critical, _totals.RowSeverity);
    }

    [Fact]
    public void A_viewer_lease_samples_every_second_and_stops_within_one_interval()
    {
        // A1 acceptance 6.
        var viewer = _rig.Scheduler.Lease(SystemTotals.SamplerName, TimeSpan.FromSeconds(1), ColumnSet.All);
        var before = _src.Reads;
        _rig.Advance(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1));
        Assert.InRange(_src.Reads - before, 10, 11);
        viewer.Dispose();
        before = _src.Reads;
        _rig.Advance(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(1));
        Assert.InRange(_src.Reads - before, 0, 1);
    }
}

public sealed class AppsTests : IDisposable
{
    private readonly FeatureRig _rig = new();
    private readonly FakeMetricSource _src = new();
    private readonly FakeTable<ProcKey, ProcessRow> _table = new();
    private readonly SystemTotals _totals;
    private readonly AppsFeature _apps;

    public AppsTests()
    {
        _src.Machine();
        _totals = _rig.Add(new SystemTotals(_rig.Context, _src));
        _apps = _rig.Add(new AppsFeature(_rig.Context, _table, new AppIdentity(), _ => null));
        _totals.Verdict = _apps.Verdict;
    }

    public void Dispose() => _rig.Dispose();

    private void Procs(params (uint Pid, uint Parent, string Exe, double Mb, double CpuS, bool Elevated)[] rows)
    {
        _table.Rows = rows.Select(r => new KeyValuePair<ProcKey, ProcessRow>(new ProcKey(r.Pid, r.Pid), new ProcessRow
        {
            Key = new ProcKey(r.Pid, r.Pid),
            ParentPid = r.Parent,
            Exe = r.Exe,
            Path = "C:/apps/" + r.Exe,
            PrivateBytes = r.Mb * 1024 * 1024,
            CpuTimeS = r.CpuS,
            Elevated = r.Elevated,
        })).ToList();
        foreach (var r in _table.Rows)
        {
            _rig.ProcessTable[r.Key.Pid] = r.Value;
        }
    }

    [Fact]
    public void Per_process_data_only_while_someone_holds_a_lease_and_buffers_are_freed_after()
    {
        Procs((10, 1, "browser.exe", 3174, 0, false));
        _rig.Pump();
        Assert.Empty(_table.Reads);
        var lease = _rig.Scheduler.Lease(AppsFeature.SamplerName, TimeSpan.FromSeconds(2), ColumnSet.All);
        _rig.Pump();
        Assert.Single(_table.Reads);
        Assert.NotNull(_rig.State.Current.Apps);
        lease.Dispose();
        Assert.Null(_rig.State.Current.Apps);
        Assert.Empty(_apps.Table);
    }

    [Fact]
    public void Cpu_percent_is_a_rate_of_cpu_time_and_the_verdict_names_the_top_group()
    {
        using var lease = _rig.Scheduler.Lease(AppsFeature.SamplerName, TimeSpan.FromSeconds(2), ColumnSet.All);
        Procs((10, 1, "browser.exe", 2048, 0, false), (11, 10, "browser.exe", 1126, 0, false), (20, 1, "editor.exe", 300, 0, false));
        _rig.Pump();
        Procs((10, 1, "browser.exe", 2048, Environment.ProcessorCount * 1.0, false), (11, 10, "browser.exe", 1126, 0, false), (20, 1, "editor.exe", 300, 0, false));
        _rig.Advance(TimeSpan.FromSeconds(2));
        var top = _rig.State.Current.Apps![0];
        Assert.Equal("browser.exe", top.AppId);
        Assert.Equal(2, top.Count);
        Assert.Equal(50, top.CpuPct, 0); // ncores CPU-seconds in 2 s on ncores = 50 %
        _src.Machine(ramUsedPct: 92);
        using var tick = _rig.Scheduler.Lease(SystemTotals.SamplerName, TimeSpan.FromSeconds(1));
        _rig.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("RAM 92%: Browser 3.1 GB", _rig.State.Current.System!.Verdict);
        Assert.Equal(Severity.Warn, _rig.State.Current.System.VerdictSeverity);
    }

    [Fact]
    public void Mixed_group_end_reports_partial_counts()
    {
        // A3 acceptance 7: "ended 2 of 3, 1 run as admin".
        using var lease = _rig.Scheduler.Lease(AppsFeature.SamplerName, TimeSpan.FromSeconds(2), ColumnSet.All);
        Procs((10, 1, "tool.exe", 10, 0, false), (11, 10, "tool.exe", 10, 0, false), (12, 10, "tool.exe", 10, 0, true));
        _rig.Pump();
        var first = _rig.Act(new ActionRequest { Action = "app.end", Origin = Origin.Flyout, Target = new Dictionary<string, string> { ["app"] = "tool.exe" } });
        Assert.Equal(ActionStatus.NeedsConfirm, first.Status);
        Assert.Equal("End Tool (3)?", first.Summary);
        var r = _rig.Act(new ActionRequest { Action = "app.end", Origin = Origin.Flyout, Target = new Dictionary<string, string> { ["app"] = "tool.exe" }, Confirm = first.Token });
        Assert.Equal(ActionStatus.Partial, r.Status);
        Assert.Contains("Ended 2 of 3", r.Message, StringComparison.Ordinal);
        Assert.Contains("run as admin", r.Message, StringComparison.Ordinal);
        Assert.Equal("end:11,10", _rig.Processes.Calls.Single(c => c.StartsWith("end:", StringComparison.Ordinal))); // leaf-first; the admin one never called
    }

    [Fact]
    public void Lower_then_restore_returns_the_original_priority()
    {
        // A3 acceptance 4.
        using var lease = _rig.Scheduler.Lease(AppsFeature.SamplerName, TimeSpan.FromSeconds(2), ColumnSet.All);
        Procs((10, 1, "tool.exe", 10, 0, false));
        _rig.Pump();
        _rig.Processes.Priorities[new ProcKey(10, 10)] = Contracts.Snapshot.ProcessPriority.AboveNormal;
        var target = new Dictionary<string, string> { ["pid"] = "10" };
        Assert.Equal(ActionStatus.Ok, _rig.Act(new ActionRequest { Action = "priority.set", Origin = Origin.Flyout, Target = target, Args = new Dictionary<string, string> { ["priority"] = "below_normal" } }).Status);
        Assert.Equal(Contracts.Snapshot.ProcessPriority.BelowNormal, _rig.Processes.Priorities[new ProcKey(10, 10)]);
        Assert.Equal(ActionStatus.Ok, _rig.Act(new ActionRequest { Action = "priority.restore", Origin = Origin.Flyout, Target = target }).Status);
        Assert.Equal(Contracts.Snapshot.ProcessPriority.AboveNormal, _rig.Processes.Priorities[new ProcKey(10, 10)]);
    }

    [Fact]
    public void Freeze_and_resume_an_app_group_through_process_control()
    {
        // A3 acceptance 8 (fake IProcessControl).
        using var lease = _rig.Scheduler.Lease(AppsFeature.SamplerName, TimeSpan.FromSeconds(2), ColumnSet.All);
        Procs((10, 1, "tool.exe", 10, 0, false), (11, 10, "tool.exe", 10, 0, false));
        _rig.Pump();
        var app = new Dictionary<string, string> { ["app"] = "tool.exe" };
        Assert.Equal(ActionStatus.Ok, _rig.Act(new ActionRequest { Action = "proc.freeze", Origin = Origin.Flyout, Target = app }).Status);
        Assert.Equal(2, _rig.Processes.Frozen.Count);
        Assert.Equal(ActionStatus.Ok, _rig.Act(new ActionRequest { Action = "proc.resume", Origin = Origin.Flyout, Target = app }).Status);
        Assert.Empty(_rig.Processes.Frozen);
    }

    [Fact]
    public void A_denylisted_exe_cannot_be_ended()
    {
        // A3 acceptance 3.
        using var lease = _rig.Scheduler.Lease(AppsFeature.SamplerName, TimeSpan.FromSeconds(2), ColumnSet.All);
        Procs((10, 1, "dwm.exe", 10, 0, false));
        _rig.Pump();
        Assert.Equal("Critical Windows process", AppsFeature.Locked(_apps.Table.Values.Single()));
        var r = _rig.Act(new ActionRequest { Action = "proc.end", Origin = Origin.Flyout, Target = new Dictionary<string, string> { ["pid"] = "10" } });
        Assert.Equal(ActionStatus.Refused, r.Status);
        Assert.Empty(_rig.Processes.Calls);
    }

    [Fact]
    public void A_process_that_exits_between_samples_leaves_no_ghost()
    {
        using var lease = _rig.Scheduler.Lease(AppsFeature.SamplerName, TimeSpan.FromSeconds(2), ColumnSet.All);
        Procs((10, 1, "a.exe", 10, 0, false), (11, 1, "b.exe", 10, 0, false));
        _rig.Pump();
        Procs((10, 1, "a.exe", 10, 0, false));
        _rig.Advance(TimeSpan.FromSeconds(2));
        Assert.Single(_rig.State.Current.Apps!);
        Assert.False(_apps.CpuPct.ContainsKey(new ProcKey(11, 11)));
    }
}
