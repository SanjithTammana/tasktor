using Tasktor.Contracts.Platform;
using Tasktor.Core.Apps;
using Tasktor.Core.Features;
using Tasktor.Core.Features.SystemMonitor;
using Tasktor.Core.Plugins;
using Tasktor.Core.Storage;
using Tasktor.Windows.Sources;

namespace Tasktor.Agent.Host;

/// <summary>Builds the real agent: the host plus the Windows adapters and every feature. Grows milestone by milestone.</summary>
internal static class Composition
{
    public static AgentHost Create(StoreLayout store)
    {
        var windows = OperatingSystem.IsWindows();
        var runner = new ChildProcessRunner(AppContext.BaseDirectory);
        if (windows)
        {
            runner.AfterStart = Windows.Processes.JobObject.ForWorker;
        }

        var processTable = new Win32ProcessTable();
        var frozen = new Core.Features.Jobs.FrozenRegistry(store.Frozen);
        IProcessControl control = windows
            ? new Windows.Processes.Win32ProcessControl(runner, frozen, () => DateTimeOffset.UtcNow)
            : new PortableProcessControl(runner);
        var host = new AgentHost(new AgentOptions
        {
            Store = store,
            KeyProtection = windows ? Windows.Security.Dpapi.Protection : KeyProtection.None,
            ProcessControl = control,
            ReadProcess = windows ? processTable.ReadOne : PortableProcessTable.ReadOne,
        });
        host.Loop.Post(() =>
        {
            var (apps, pdh) = AddSystemMonitor(host, windows, processTable);
            AddJobWatch(host, windows, processTable, apps, pdh);
        });
        return host;
    }

    /// <summary>Section B: the long-job watch over the same process table.</summary>
    private static void AddJobWatch(AgentHost host, bool windows, Win32ProcessTable processTable, Apps apps, PdhMetricSource? pdh)
    {
        var exits = windows ? new ProcessExitEvents() : null;
        var progress = new Core.Features.Jobs.JobProgressSource(host.Store.JobsDir, () => host.Loop.Clock.UtcNow);
        var jobs = new Core.Features.Jobs.JobWatch(
            host.NewFeatureContext(),
            apps,
            windows ? processTable.ReadOne : PortableProcessTable.ReadOne,
            windows ? ProcessInfo.CommandLine : _ => null,
            exits,
            exits is null ? null : exits.Watch,
            progress,
            host.InternalTasks)
        {
            GpuOf = pdh is null ? null : pid => pdh.GpuByPid.TryGetValue(pid, out var v) ? v : null,
            WindowInFront = windows ? chain => chain.Contains(ProcessInfo.ForegroundPid()) : null,
        };
        host.AddFeature(jobs);
        host.QuitRequested += jobs.ResumeAll; // quitting resumes every paused job first
        host.Own(host.Loop.Every(TimeSpan.FromHours(1), () => jobs.CleanStaleFiles(host.Loop.Clock.UtcNow)));
    }

    /// <summary>Section A: totals, apps, disk, pressure, battery, and the ports Module when it is on.</summary>
    private static (Apps Apps, PdhMetricSource? Pdh) AddSystemMonitor(AgentHost host, bool windows, Win32ProcessTable processTable)
    {
        var ctx = host.NewFeatureContext();
        var pdh = windows ? new PdhMetricSource() : null;
        IMetricSource metrics = pdh is null ? new DriveVolumeSource() : new CompositeMetricSource(pdh, new DriveVolumeSource());
        if (pdh is not null)
        {
            processTable.GpuByPid = pid => pdh.GpuByPid.TryGetValue(pid, out var v) ? v : null;
        }

        var totals = new SystemTotals(ctx, metrics);
        host.AddFeature(totals);
        var identity = new AppIdentity(host.Rules.Engine.Rules.Grouping);
        ITableSource<ProcKey, ProcessRow> table = windows ? processTable : new PortableProcessTable();
        var apps = new Apps(ctx, table, identity, name => host.Rules.Engine.Rules.Groups.GetValueOrDefault(name));
        host.AddFeature(apps);
        totals.Verdict = apps.Verdict;
        host.RulesReloaded += set => identity.SetRules(set.Grouping);
        var disk = new DiskGuard(ctx, totals, host.InternalTasks);
        host.AddFeature(disk);
        host.Rules.SnoozeHooks[DiskGuard.LowRuleId] = disk.Snooze;
        host.AddFeature(new Pressure(ctx, totals, apps, windows ? new LowMemoryEvents() : null));
        var battery = new Battery(ctx, windows ? new PowerSource() : new NoBattery(), windows ? new PowerEvents() : null, totals)
        {
            BusyName = () => apps.TopGroups(1, "cpu").FirstOrDefault()?.Name,
        };
        host.AddFeature(battery);
        if (host.Settings.ModuleOn("ports"))
        {
            host.AddFeature(new Ports(ctx, windows ? new ListenerSource(processTable) : new NoListeners()));
        }

        return (apps, pdh);
    }

    private sealed class NoBattery : IMetricSource
    {
        public string Name => "no-battery";

        public void Read(MetricFrame frame) => frame.Set(Battery.FlagNoBattery, 128);
    }

    private sealed class NoListeners : ITableSource<string, ListenerEntry>
    {
        public string Name => "no-listeners";

        public void Read(ICollection<KeyValuePair<string, ListenerEntry>> into, ColumnSet columns)
        {
        }
    }
}
