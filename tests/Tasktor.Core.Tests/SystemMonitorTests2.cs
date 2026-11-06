using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Platform;
using Tasktor.Core.Features.SystemMonitor;
using Tasktor.Tests.Shared;
using AppIdentity = Tasktor.Core.Apps.AppIdentity;
using AppsFeature = Tasktor.Core.Features.SystemMonitor.Apps;

namespace Tasktor.Core.Tests;

public sealed class DiskGuardTests : IDisposable
{
    private const double Gb = 1024.0 * 1024 * 1024;
    private readonly FeatureRig _rig = new();
    private readonly FakeMetricSource _src = new();
    private readonly SystemTotals _totals;
    private readonly DiskGuard _disk;

    public DiskGuardTests()
    {
        _src.Machine();
        _totals = _rig.Add(new SystemTotals(_rig.Context, _src));
        _disk = _rig.Add(new DiskGuard(_rig.Context, _totals, new Core.Features.InternalTasks()));
        Volume(200);
        _rig.Pump();
    }

    public void Dispose() => _rig.Dispose();

    private void Volume(double freeGb, double totalGb = 475.6) =>
        _src.Set(MetricIds.VolumeFreeBytes, freeGb * Gb, "C:").Set(MetricIds.VolumeTotalBytes, totalGb * Gb, "C:");

    private int LowToasts() => _rig.Notifier.Shown.Count(t => t.Tag == "disk-low:C:");

    [Fact]
    public void Volumes_show_free_space_and_the_low_flag_uses_max_10pct_15gb()
    {
        Volume(32.2);
        _rig.Advance(TimeSpan.FromMinutes(1));
        var v = Assert.Single(_rig.State.Current.System!.Volumes!);
        Assert.Equal(32.2, v.FreeGb);
        Assert.True(v.Low); // 6.8 % < 10 %
        Volume(14, totalGb: 100);
        _rig.Advance(TimeSpan.FromMinutes(1));
        Assert.True(_rig.State.Current.System!.Volumes![0].Low); // 14 GB < 15 GB though 14 %
        Volume(60, totalGb: 100);
        _rig.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(_rig.State.Current.System!.Volumes![0].Low);
    }

    [Fact]
    public void Low_space_fires_once_then_again_only_after_5_gb_more_or_24_h()
    {
        // A5 acceptance 2.
        Volume(30);
        _rig.Advance(TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(1));
        Assert.Equal(0, LowToasts()); // must hold 5 min
        _rig.Advance(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(1));
        Assert.Equal(1, LowToasts());
        var toast = _rig.Notifier.Shown.Last(t => t.Tag == "disk-low:C:");
        Assert.Equal("C: is low: 30 GB free (6%)", toast.Title);
        Assert.Equal(["Biggest folders", "Open Storage settings", "Snooze 1 day"], toast.Buttons.Select(b => b.Label));
        Volume(27);
        _rig.Advance(TimeSpan.FromHours(1), TimeSpan.FromMinutes(1));
        Assert.Equal(1, LowToasts());
        Volume(24.9);
        _rig.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(2, LowToasts()); // 5 GB more lost
        _rig.Advance(TimeSpan.FromHours(24), TimeSpan.FromMinutes(1));
        Assert.Equal(3, LowToasts()); // 24 h passed
        Assert.True(_rig.Router.Toasts.IsOffered(toast.Buttons[0].Request));
    }

    [Fact]
    public void A_user_rule_can_switch_the_built_in_watch_off()
    {
        _rig.LoadRules("""{ "rules": [ { "id": "disk-low", "enabled": false } ] }""");
        Volume(10);
        _rig.Advance(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1));
        Assert.Equal(0, LowToasts());
    }

    [Fact]
    public void After_learning_the_learned_limit_decides_and_the_toast_says_so()
    {
        // A5 acceptance 6, second half: 20+ learned hours at 2 GB/h, then 4 GB/h near the low mark fires on the learned 3 GB/h.
        var free = 160.0;
        Volume(free, totalGb: 500);
        _rig.Advance(TimeSpan.FromMinutes(1));
        for (var m = 0; m < 22 * 60; m++)
        {
            free -= 2.0 / 60;
            Volume(free, totalGb: 500);
            _rig.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.DoesNotContain(_rig.Notifier.Shown, x => x.Tag == "disk-filling:C:"); // 2 GB/h is this volume's normal
        for (var m = 0; m < 75; m++)
        {
            free -= 4.0 / 60;
            Volume(free, totalGb: 500);
            _rig.Advance(TimeSpan.FromMinutes(1));
        }

        var t = Assert.Single(_rig.Notifier.Shown, x => x.Tag == "disk-filling:C:");
        Assert.Contains("learned limit 3 GB/h", t.Title, StringComparison.Ordinal);
        Assert.Equal("learned", _disk.LastFillSource("C:"));
    }

    [Fact]
    public void Fill_rate_fires_on_the_default_when_low_is_near()
    {
        var free = 80.0;
        Volume(free, totalGb: 500);
        _rig.Advance(TimeSpan.FromMinutes(1));
        for (var m = 0; m < 70; m++)
        {
            free -= 6.0 / 60;
            Volume(free, totalGb: 500);
            _rig.Advance(TimeSpan.FromMinutes(1));
        }

        var t = Assert.Single(_rig.Notifier.Shown, x => x.Tag == "disk-filling:C:");
        Assert.Contains("default limit 5 GB/h", t.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Fill_rate_learns_a_normal_per_volume()
    {
        var free = 470.0;
        Volume(free, totalGb: 500);
        _rig.Advance(TimeSpan.FromMinutes(1));
        for (var m = 0; m < 22 * 60; m++)
        {
            free -= 2.0 / 60; // 2 GB/h every hour: the learned normal
            Volume(free, totalGb: 500);
            _rig.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Single(_disk.Learned.All);
        var stats = _disk.Learned.All.Single().Value;
        Assert.True(stats.GoodRuns >= 20);
        var limit = DiskGuard.FillRate.Current(stats);
        Assert.Equal(Contracts.Snapshot.ValueSource.Learned, limit.Source);
        Assert.InRange(limit.Value, 2.9, 3.1); // 1.5 × 2 GB/h
        Assert.True(File.Exists(_rig.Store.Layout.LearnedVolumes));
        Assert.DoesNotContain("C:", File.ReadAllText(_rig.Store.Layout.LearnedVolumes), StringComparison.Ordinal);
    }

    [Fact]
    public void Folder_scan_counts_each_file_once_and_never_follows_links()
    {
        // A5 acceptance 4: a junction loop terminates and counts each file once.
        var root = Path.Combine(_rig.Store.Root, "scan");
        Directory.CreateDirectory(Path.Combine(root, "a", "b"));
        File.WriteAllBytes(Path.Combine(root, "a", "one.bin"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(root, "a", "b", "two.bin"), new byte[500]);
        Links.Directory(Path.Combine(root, "a", "b", "loop"), root);
        var node = new FolderScanner().Scan(root, CancellationToken.None);
        Assert.Equal(1500, node.Bytes);
        Assert.Equal(2, node.Files);
        Assert.Equal("a", Assert.Single(node.Children).Name);
    }

    [Fact]
    public void Folder_scan_stops_when_cancelled()
    {
        // A5 acceptance 5.
        var root = Path.Combine(_rig.Store.Root, "many");
        for (var i = 0; i < 200; i++)
        {
            Directory.CreateDirectory(Path.Combine(root, "d" + i, "x"));
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<OperationCanceledException>(() => new FolderScanner().Scan(root, cts.Token));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1));
    }
}

public sealed class PressureTests : IDisposable
{
    private readonly FeatureRig _rig = new();
    private readonly FakeMetricSource _src = new();
    private readonly FakeTable<ProcKey, ProcessRow> _table = new();
    private readonly SystemTotals _totals;
    private readonly AppsFeature _apps;
    private readonly Pressure _pressure;

    public PressureTests()
    {
        _src.Machine(ramUsedPct: 50);
        _totals = _rig.Add(new SystemTotals(_rig.Context, _src));
        _apps = _rig.Add(new AppsFeature(_rig.Context, _table, new AppIdentity(), _ => null));
        _pressure = _rig.Add(new Pressure(_rig.Context, _totals, _apps, null));
        _rig.LoadRules();
        Procs(1000);
        _rig.Pump();
    }

    public void Dispose() => _rig.Dispose();

    private void Procs(double browserMb) => _table.Rows =
    [
        new(new ProcKey(10, 10), new ProcessRow { Key = new ProcKey(10, 10), ParentPid = 1, Exe = "browser.exe", Path = "C:/b.exe", PrivateBytes = browserMb * 1024 * 1024 }),
        new(new ProcKey(20, 20), new ProcessRow { Key = new ProcKey(20, 20), ParentPid = 1, Exe = "editor.exe", Path = "C:/e.exe", PrivateBytes = 200 * 1024 * 1024 }),
    ];

    private void Step(TimeSpan by, TimeSpan step)
    {
        var end = _rig.Clock.UtcNow + by;
        while (_rig.Clock.UtcNow < end)
        {
            _rig.Advance(step);
            _rig.Rules.Evaluate();
        }
    }

    private List<Contracts.Platform.ToastRequest> RamToasts() => _rig.Notifier.Shown.Where(t => t.Tag.StartsWith("ram-pressure", StringComparison.Ordinal)).ToList();

    [Fact]
    public void A_short_spike_raises_nothing()
    {
        // A6 acceptance 2: 10 s above the threshold, rule holds for 30 s.
        _src.Machine(ramUsedPct: 95);
        Step(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));
        _src.Machine(ramUsedPct: 60);
        Step(TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(5));
        Assert.Empty(RamToasts());
    }

    [Fact]
    public void Sustained_pressure_raises_one_toast_naming_the_offender_and_its_growth()
    {
        _src.Machine(ramUsedPct: 95);
        _rig.Advance(TimeSpan.FromMinutes(1)); // tick sees ≥ warn → escalation
        Assert.True(_pressure.Escalated);
        for (var i = 0; i < 12; i++)
        {
            Procs(1000 + (i * 60)); // +600 MB over a minute
            Step(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }

        var t = Assert.Single(RamToasts());
        Assert.StartsWith("Memory is low: Browser is using", t.Title, StringComparison.Ordinal);
        Assert.Contains("MB in 1 min", t.Title, StringComparison.Ordinal);
        Assert.Equal("app.close", t.Buttons[0].Request.Action);
        Assert.Equal("browser.exe", t.Buttons[0].Request.Target!["app"]);
        Assert.DoesNotContain(t.Buttons, b => b.Request.Action is "app.end" or "proc.end"); // no End in a toast

        // A6 acceptance 3: no repeat for 30 min.
        Step(TimeSpan.FromMinutes(20), TimeSpan.FromSeconds(30));
        Assert.Single(RamToasts());
    }

    [Fact]
    public void Escalated_sampling_stops_within_2_minutes_of_the_condition_clearing()
    {
        // A6 acceptance 4.
        _src.Machine(ramUsedPct: 95);
        _rig.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, _rig.Scheduler.LeaseCount(AppsFeature.SamplerName));
        _src.Machine(ramUsedPct: 40);
        _rig.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));
        Assert.Equal(0, _rig.Scheduler.LeaseCount(AppsFeature.SamplerName));
        Assert.Empty(_apps.Table); // buffers freed
    }

    [Fact]
    public void Learned_growth_changes_only_the_wording()
    {
        // A6 acceptance 6: a +800 MB/min normal is not "faster" at +700; a +50 MB/min normal is at +200.
        var big = new Learning.KindStats();
        var small = new Learning.KindStats();
        for (var i = 0; i < 20; i++)
        {
            big.RecordRun(new Learning.RunOutcome { Good = true, DurationS = 800 });
            small.RecordRun(new Learning.RunOutcome { Good = true, DurationS = 50 });
        }

        Assert.False(700 > Pressure.Growth.Current(big).Value);
        Assert.True(200 > Pressure.Growth.Current(small).Value);
        Assert.Equal(500, Pressure.Growth.Current(null).Value); // fallback until learned
    }

    [Fact]
    public void The_low_memory_event_escalates_without_polling()
    {
        _pressure.OnLowMemory(true);
        Assert.True(_pressure.Escalated);
        var inputs = new Rules.RuleInputs();
        _pressure.Contribute(inputs, _rig.Clock.UtcNow);
        Assert.Equal("true", inputs.States["low_memory"]);
    }
}

public sealed class PortsTests
{
    private static ListenerEntry L(int port, string addr, uint pid, string exe, bool own) => new() { Port = port, Address = addr, Pid = pid, Exe = exe, OwnUser = own };

    /// <summary>A shape of the reference-PC probe: Windows listeners plus a few user ones.</summary>
    private static readonly ListenerEntry[] Probe =
    [
        L(135, "0.0.0.0", 1100, "svchost.exe", false), L(445, "0.0.0.0", 4, "System", false), L(5040, "0.0.0.0", 3300, "svchost.exe", false),
        L(49664, "0.0.0.0", 900, "lsass.exe", false), L(49665, "0.0.0.0", 800, "wininit.exe", false),
        L(11434, "127.0.0.1", 7001, "ollama.exe", true), L(7535, "127.0.0.1", 7002, "pythonw.exe", true),
        L(13469, "127.0.0.1", 7003, "studio.exe", true), L(49674, "0.0.0.0", 7004, "launcher.exe", true),
        L(3000, "127.0.0.1", 7005, "node.exe", true), L(3000, "::1", 7005, "node.exe", true),
    ];

    [Fact]
    public void Default_filter_keeps_exactly_the_user_owned_listeners()
    {
        // A7 acceptance 2.
        var rows = Ports.Filter(Ports.Merge(Probe), showWindows: false, showEphemeral: false);
        Assert.Equal([3000, 7535, 11434, 13469, 49674], rows.Select(r => r.Port));
    }

    [Fact]
    public void Ipv4_and_ipv6_on_one_port_merge_and_all_interfaces_is_exposed()
    {
        // A7 acceptance 3 + A7.6.
        var rows = Ports.Merge(Probe);
        var node = Assert.Single(rows, r => r.Port == 3000);
        Assert.Equal(["127.0.0.1", "::1"], node.Addresses);
        Assert.False(node.Exposed);
        Assert.True(rows.Single(r => r.Port == 49674).Exposed);
    }

    [Fact]
    public void Port_conflict_lookup()
    {
        // A7 acceptance 5.
        using var rig = new FeatureRig();
        var ports = rig.Add(new Ports(rig.Context, new FakeTable<string, ListenerEntry>()));
        ports.Ingest(Probe);
        Assert.Equal("3000 is taken by node.exe (PID 7005)", ports.Lookup(3000));
        Assert.Equal("8765 is free", ports.Lookup(8765));
    }
}

public sealed class BatteryTests : IDisposable
{
    private readonly FeatureRig _rig = new();
    private readonly FakeMetricSource _power = new();
    private readonly FakeMetricSource _src = new();
    private readonly SystemTotals _totals;

    public BatteryTests()
    {
        _src.Machine(cpu: 80);
        _totals = _rig.Add(new SystemTotals(_rig.Context, _src));
        _rig.LoadRules();
    }

    public void Dispose() => _rig.Dispose();

    [Fact]
    public void A_desktop_shows_no_battery_ui_and_no_rule_inputs()
    {
        // A8 acceptance 1 (BatteryFlag 128).
        _power.Set(Battery.FlagNoBattery, 128);
        var b = _rig.Add(new Battery(_rig.Context, _power, null, _totals));
        Assert.False(b.HasBattery);
        Assert.Empty(b.Cards());
        Assert.False(_rig.State.Current.Power!.HasBattery);
        var inputs = new Rules.RuleInputs();
        b.Contribute(inputs, _rig.Clock.UtcNow);
        Assert.Equal("false", inputs.States["on_battery"]);
    }

    [Fact]
    public void Unknown_sentinels_never_render_as_raw_numbers()
    {
        // A8 acceptance 4: 255 % and −1 s.
        _power.Set(Battery.FlagNoBattery, 1).Set("battery.pct", 255).Set("battery.ac", 0).Set("battery.time_left_s", -1);
        var b = _rig.Add(new Battery(_rig.Context, _power, null, _totals));
        Assert.Null(b.Current!.Pct);
        Assert.Null(b.Current.TimeLeftS);
        Assert.Equal("—", ((Contracts.Cards.MetricBlock)b.Cards()[0].Blocks[2]).Value.AsElement()!.Value.GetString());
    }

    [Fact]
    public void Unplugging_under_load_raises_one_toast_and_plugging_back_in_withdraws_it()
    {
        _power.Set(Battery.FlagNoBattery, 8).Set("battery.pct", 80).Set("battery.ac", 1).Set("battery.time_left_s", -1);
        var b = _rig.Add(new Battery(_rig.Context, _power, null, _totals) { BusyName = () => "python" });
        _rig.Advance(TimeSpan.FromMinutes(2));
        _rig.Rules.Evaluate();
        _power.Set("battery.ac", 0).Set("battery.time_left_s", 4200);
        b.Read();
        _rig.Rules.Evaluate();
        var t = Assert.Single(_rig.Notifier.Shown);
        Assert.Equal("Unplugged while python is busy: ~1 h 10 m left at this rate", t.Title);
        Assert.Equal(["efficiency.set", "battery.settings"], t.Buttons.Select(x => x.Request.Action));
        _rig.Rules.Evaluate();
        Assert.Single(_rig.Notifier.Shown); // one toast, not one per evaluation
        _power.Set("battery.ac", 1);
        b.Read();
        Assert.Contains(t.Tag, _rig.Notifier.Withdrawn);
    }

    [Fact]
    public void Unplugging_at_idle_raises_none()
    {
        _src.Machine(cpu: 3);
        _power.Set(Battery.FlagNoBattery, 8).Set("battery.pct", 80).Set("battery.ac", 1);
        var b = _rig.Add(new Battery(_rig.Context, _power, null, _totals));
        _rig.Advance(TimeSpan.FromMinutes(2));
        _power.Set("battery.ac", 0);
        b.Read();
        _rig.Rules.Evaluate();
        Assert.Empty(_rig.Notifier.Shown);
    }
}
