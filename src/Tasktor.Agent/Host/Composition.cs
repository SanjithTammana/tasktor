using Tasktor.Contracts.Platform;
using Tasktor.Core.Apps;
using Tasktor.Core.Calendar;
using Tasktor.Core.Features;
using Tasktor.Core.Features.Agents;
using Tasktor.Core.Features.Calendar;
using Tasktor.Core.Features.Devices;
using Tasktor.Core.Features.Focus;
using Tasktor.Core.Features.Folders;
using Tasktor.Core.Features.Guards;
using Tasktor.Core.Features.Network;
using Tasktor.Core.Features.Space;
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
            AddSourcesAndGuards(host, windows, processTable, apps);
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

    /// <summary>Section U2: calendar, guards, agents, folders, connectivity, space, focus and device batteries.</summary>
    private static void AddSourcesAndGuards(AgentHost host, bool windows, Win32ProcessTable processTable, Apps apps)
    {
        var ctx = host.NewFeatureContext();
        var settings = host.Settings;
        IReadOnlyList<string> Group(string name) => host.Rules.Engine.Rules.Groups.GetValueOrDefault(name) ?? [];
        apps.RuleGroups = () => host.Rules.Engine.Rules.Groups;
        host.Rules.RunCommand = ToolProcess.Start;

        var calendarOn = new[] { "next_event", "deadlines", "event_mode", "meeting_guard" }.Any(settings.ModuleOn);
        var feed = calendarOn ? new CalendarFeed(ctx, new CalendarSource(() => settings.Current.Calendar, host.Store, host.Loop.Clock)) : null;
        NextEvent? next = null;
        if (feed is not null && settings.ModuleOn("next_event"))
        {
            host.AddFeature(next = new NextEvent(ctx, feed));
        }

        if (feed is not null && settings.ModuleOn("deadlines"))
        {
            host.AddFeature(new Deadlines(ctx, feed));
        }

        // Event mode is plumbing for E11 too: always on; calendar windows only with its Module.
        var eventMode = new EventMode(ctx, settings.ModuleOn("event_mode") ? feed : null);
        host.AddFeature(eventMode);

        var power = new PowerGuard(ctx, apps, eventMode);
        host.AddFeature(power);
        host.Rules.ExtraGuards.Add(power.ManualGuards);

        if (settings.ModuleOn("meeting_guard"))
        {
            var meeting = new MeetingGuard(ctx, windows ? new CallStateSource() : null, () => next?.MeetingNow is not null) { CallApps = () => Group("call_apps") };
            host.AddFeature(meeting);
            power.Notices.Add(() => meeting.Notice);
        }

        if (settings.ModuleOn("agents"))
        {
            var profile = AgentHost.KnownFolder("UserProfile");
            host.AddFeature(new AgentWatch(ctx, apps, new AgentHookSource(host.Store.AgentsDir), windows ? processTable.ReadOne : PortableProcessTable.ReadOne, windows ? ProcessInfo.CommandLine : _ => null)
            {
                AgentGroup = () => Group("agents"),
                WindowInFront = windows ? chain => chain.Contains(ProcessInfo.ForegroundPid()) : null,
                ShowWindow = windows ? Windows.Shell.WindowsShell.ShowWindowOf : null,
                ClaudeSettingsPath = () => profile is null ? null : Path.Combine(profile, ".claude", "settings.json"),
            });
        }

        if (settings.ModuleOn("folders"))
        {
            var folders = new FolderAction(ctx, list => new FolderWatchSource(list, () => host.Loop.Clock.UtcNow))
            {
                Folders = () => FolderAction.FoldersOf(host.Rules.Engine.Rules, AgentHost.KnownFolder),
            };
            host.AddFeature(folders);
            host.RulesReloaded += _ => folders.Rewatch();
        }

        if (settings.ModuleOn("connectivity"))
        {
            var net = windows ? new ConnectivitySource() : null;
            var listeners = new ListenerSource(processTable);
            host.AddFeature(new Connectivity(ctx, net, net) { TopConnections = windows ? listeners.TopConnections : null });
        }

        host.AddFeature(new SpaceReport(ctx, new CacheLocator(Environment.GetEnvironmentVariable, AgentHost.KnownFolder, Directory.Exists, ToolProcess.FirstLine))
        {
            Folder = AgentHost.KnownFolder,
            RecycleBinBytes = windows ? Windows.Shell.WindowsShell.RecycleBinBytes : () => null,
        });

        if (settings.ModuleOn("focus"))
        {
            host.AddFeature(new FocusTimer(ctx, eventMode, new NotesStore(host.Store.NotesDir)) { TrackingOn = () => settings.ModuleOn("tracking") });
        }

        if (settings.ModuleOn("batteries"))
        {
            host.AddFeature(new DeviceBatteries(ctx, windows ? new BluetoothBatterySource() : new NoDevices()));
        }
    }

    private sealed class NoDevices : ITableSource<string, DeviceBattery>
    {
        public string Name => "no-devices";

        public void Read(ICollection<KeyValuePair<string, DeviceBattery>> into, ColumnSet columns)
        {
        }
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
