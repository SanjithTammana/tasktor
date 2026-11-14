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
using Tasktor.Core.Features.Projects;
using Tasktor.Core.Features.Space;
using Tasktor.Core.Features.SystemMonitor;
using Tasktor.Core.Plugins;
using Tasktor.Core.Storage;
using Tasktor.Windows.Sources;

namespace Tasktor.Agent.Host;

/// <summary>Builds the real agent: the host plus the Windows adapters and every feature. Grows milestone by milestone.</summary>
internal static class Composition
{
    /// <param name="tray">Show the tray icon and the edge (the real agent only; tests and CI rigs leave it off).</param>
    /// <param name="dataDir">The <c>--data-dir</c> given, if any: the start-at-logon entry passes it on.</param>
    /// <param name="verifyPipeClients">Grant viewer origins only to Tasktor's own viewer exes (F-PIPE1). Default: on for
    /// the real agent (Windows, tray); tests that connect as a viewer from the test process leave it off.</param>
    public static AgentHost Create(StoreLayout store, bool tray = false, string? pipeName = null, IReadOnlyList<uint>? switcherPids = null, string? dataDir = null, bool? verifyPipeClients = null)
    {
        var notifier = new SwitchableNotifier(); // the tray's balloons once it is up (U4 D-G7 fallback)
        var windows = OperatingSystem.IsWindows();
        var runner = new ChildProcessRunner(AppContext.BaseDirectory);
        if (windows)
        {
            runner.Launch = Windows.Processes.JobChild.Start;
        }

        // Check mode (--switcher-pid): only the check's own processes and their descendants are sampled or read.
        var processTable = new Win32ProcessTable { CheckPids = switcherPids is { Count: > 0 } fixture ? fixture.ToHashSet() : null };
        var logon = windows && tray && Environment.ProcessPath is not null
            && Core.Platform.LogonStart.AllowedFor(pipeName, switcherPids ?? [], Environment.GetEnvironmentVariable("TASKTOR_PIPE"));
        var frozen = new Core.Features.Jobs.FrozenRegistry(store.Frozen);
        IProcessControl control = windows
            ? new Windows.Processes.Win32ProcessControl(runner, frozen, () => DateTimeOffset.UtcNow, store.Caps)
            : new PortableProcessControl(runner);
        var host = new AgentHost(new AgentOptions
        {
            Store = store,
            PipeName = pipeName,
            SwitcherPids = switcherPids ?? [],
            KeyProtection = windows ? Windows.Security.Dpapi.Protection : KeyProtection.None,
            ProcessControl = control,
            ReadProcess = windows ? processTable.ReadOne : PortableProcessTable.ReadOne,
            Notifier = notifier,
            // H8: only the real agent (tray on) of the owner's own instance, never a check instance or a test rig.
            LogonStart = logon ? Windows.Platform.RunKeyLogonStart.Create() : null,
            LogonCommand = logon ? Core.Platform.LogonStart.Command(Environment.ProcessPath!, dataDir is null ? null : Path.GetFullPath(dataDir)) : null,
        });
        if (verifyPipeClients ?? (windows && tray))
        {
            host.Pipe.OriginsFor = PipeServer.VerifiedOrigins(AppContext.BaseDirectory);
        }

        host.Loop.Post(() =>
        {
            var (apps, pdh, totals) = AddSystemMonitor(host, windows, processTable);
            if (tray && windows)
            {
                AddTray(host, totals, notifier);
            }

            AddJobWatch(host, windows, processTable, apps, pdh);
            AddSourcesAndGuards(host, windows, processTable, apps);
        });
        return host;
    }

    /// <summary>
    /// U4 D-G4 / amended D-G10, D-G15: the tray icon and the edge on their own UI thread. Their events are posted to the
    /// loop (menu rows go through the router with origin tray); hover and click open the shelf.
    /// </summary>
    private static void AddTray(AgentHost host, SystemTotals totals, SwitchableNotifier notifier)
    {
        var tray = new Windows.Shell.TrayHost();
        var presenter = new TrayPresenter();
        var launcher = new FlyoutLauncher(host.Options.InstallDir);
        Windows.Shell.TrayDisplay? display = null;
        Windows.Shell.TrayEdgeImage? edge = null;

        void Show()
        {
            if (display is null || host.Publisher.Latest is not { } snap)
            {
                return;
            }

            var state = host.State.ToJson();
            var cards = snap.Cards ?? [];
            var now = host.Loop.Clock.UtcNow;
            var worst = Core.State.GlanceText.WorstSeverity(cards, state);
            var recording = TrayPresenter.Recording(snap.State.Capture, now);
            var sentence = TrayPresenter.Sentence(cards, state);
            tray.Update(new Windows.Shell.TrayView(
                presenter.Icon(TrayPresenter.StateOf(snap.State, snap.Agent, worst, now), display),
                display.IconSize,
                TrayPresenter.Tooltip(Core.State.GlanceText.Compose(cards, state), recording),
                TrayPresenter.Menu(sentence, snap.State, snap.Agent, now),
                edge));
        }

        void Repaint()
        {
            if (display is not null)
            {
                edge = TrayPresenter.Edge(display, totals.HourRing["system.cpu_pct"].ToArray(), totals.HourSeverity.ToArray());
            }

            Show();
        }

        tray.DisplayChanged += d => host.Loop.Post(() =>
        {
            display = d;
            Repaint();
        });
        tray.OpenRequested += () => launcher.ShowShelf();
        tray.CommandRequested += () => launcher.ShowCommand();
        tray.BalloonClicked += () => launcher.ShowShelf();
        tray.MenuChosen += item => host.Loop.Post(() => _ = host.Router.Submit(item.Action! with { Origin = Contracts.Actions.Origin.Tray }));
        host.Publisher.Published += _ => Show();
        totals.Sampled += (_, isTick) =>
        {
            if (isTick)
            {
                Repaint();
            }
        };
        if (tray.Start())
        {
            host.Own(tray);
            host.ApplyHotkey = h => tray.SetHotKey(h.Modifiers, h.VirtualKey); // the configured one replaces Win+Alt+T at start
            notifier.Target = new BalloonNotifier(v => tray.ShowBalloon(v is null ? null : new Windows.Shell.TrayBalloon(v.Title, v.Body, (uint)v.Icon)));
        }
        else
        {
            tray.Dispose();
        }
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
            windows ? key => processTable.Allows(key.Pid) ? ProcessInfo.CommandLine(key) : null : _ => null,
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
        host.QuitRequested += () => host.Processes.ReleaseOnQuit(); // then every other freeze and every CPU cap
        host.Own(host.Loop.Every(TimeSpan.FromHours(1), () => jobs.CleanStaleFiles(host.Loop.Clock.UtcNow)));
    }

    /// <summary>Section A: totals, apps, disk, pressure, battery, and the ports Module when it is on.</summary>
    private static (Apps Apps, PdhMetricSource? Pdh, SystemTotals Totals) AddSystemMonitor(AgentHost host, bool windows, Win32ProcessTable processTable)
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
        host.Publisher.Published += p => totals.ObserveSeverity(Tasktor.Core.State.GlanceText.WorstSeverity(p.Snapshot.Cards ?? [], host.State.ToJson()));
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
        host.RegisterModule("ports", () => host.AddFeature(new Ports(ctx, windows ? new ListenerSource(processTable) : new NoListeners())));

        return (apps, pdh, totals);
    }

    /// <summary>Section U2: calendar, guards, agents, folders, connectivity, space, focus and device batteries.</summary>
    private static void AddSourcesAndGuards(AgentHost host, bool windows, Win32ProcessTable processTable, Apps apps)
    {
        var ctx = host.NewFeatureContext();
        var settings = host.Settings;
        IReadOnlyList<string> Group(string name) => host.Rules.Engine.Rules.Groups.GetValueOrDefault(name) ?? [];
        apps.RuleGroups = () => host.Rules.Engine.Rules.Groups;
        host.Rules.RunCommand = ToolProcess.Start;

        // The feed reads and watches calendar files only while a feature holds it, so it is always built (Modules start live).
        var feed = new CalendarFeed(ctx, new CalendarSource(() => settings.Current.Calendar, host.Store, host.Loop.Clock));
        NextEvent? next = null;
        host.RegisterModule("next_event", () => host.AddFeature(next = new NextEvent(ctx, feed)));
        host.RegisterModule("deadlines", () => host.AddFeature(new Deadlines(ctx, feed)));

        // Event mode is plumbing for E11 too: always on; calendar windows only with its Module.
        var eventMode = new EventMode(ctx, settings.ModuleOn("event_mode") ? feed : null);
        host.AddFeature(eventMode);

        var power = new PowerGuard(ctx, apps, eventMode);
        host.AddFeature(power);
        host.Rules.ExtraGuards.Add(power.ManualGuards);

        MeetingGuard? meeting = null;
        power.Notices.Add(() => meeting is { Enabled: true } m ? m.Notice : null); // reads whichever guard runs now
        host.RegisterModule("meeting_guard", () =>
        {
            meeting = new MeetingGuard(ctx, windows ? new CallStateSource() : null, () => next is { Enabled: true } n && n.MeetingNow is not null) { CallApps = () => Group("call_apps") };
            host.AddFeature(meeting);
        });

        CaptureFeature? capture = null;
        host.RegisterModule("agents", () =>
        {
            var profile = AgentHost.KnownFolder("UserProfile");
            var watch = new AgentWatch(ctx, apps, new AgentHookSource(host.Store.AgentsDir), windows ? processTable.ReadOne : PortableProcessTable.ReadOne, windows ? key => processTable.Allows(key.Pid) ? ProcessInfo.CommandLine(key) : null : _ => null)
            {
                AgentGroup = () => Group("agents"),
                WindowInFront = windows ? chain => chain.Contains(ProcessInfo.ForegroundPid()) : null,
                ShowWindow = windows ? Windows.Shell.WindowsShell.ShowWindowOf : null,
                ClaudeSettingsPath = () => profile is null ? null : Path.Combine(profile, ".claude", "settings.json"),

                // C22.4: agents' child command lines are read only while T1 capture is on with its consent.
                RecordChildCommands = () => capture is { Enabled: true } && host.Consents.HasModule("capture"),
            };
            watch.ChildCommand += c => capture?.AgentCommand(c);
            host.AddFeature(watch);
        });

        FolderAction? folders = null;
        host.RulesReloaded += _ =>
        {
            if (folders is { Enabled: true } f)
            {
                f.Rewatch();
            }
        };
        host.RegisterModule("folders", () =>
        {
            folders = new FolderAction(ctx, list => new FolderWatchSource(list, () => host.Loop.Clock.UtcNow))
            {
                Folders = () => FolderAction.FoldersOf(host.Rules.Engine.Rules, AgentHost.KnownFolder),
            };
            host.AddFeature(folders);
        });

        host.RegisterModule("connectivity", () =>
        {
            var net = windows ? new ConnectivitySource() : null;
            var listeners = new ListenerSource(processTable);
            host.AddFeature(new Connectivity(ctx, net, net) { TopConnections = windows ? listeners.TopConnections : null });
        });

        host.AddFeature(new SpaceReport(ctx, new CacheLocator(Environment.GetEnvironmentVariable, AgentHost.KnownFolder, Directory.Exists, ToolProcess.FirstLine))
        {
            Folder = AgentHost.KnownFolder,
            RecycleBinBytes = windows ? Windows.Shell.WindowsShell.RecycleBinBytes : () => null,
        });

        // One WinEventSource for C9 and I11 (it keeps one hook thread): started while either subscriber needs it.
        // Check mode (--switcher-pid): window events, titles included, only for the check's own processes.
        Func<uint, bool>? checkPids = host.Options.SwitcherPids is { Count: > 0 } fixture ? pid => fixture.Contains(pid) : null;
        var winEvents = windows ? new WinEventSource(pid => processTable.ReadOne(new ProcKey(pid, 0))) { IncludePid = checkPids } : null;
        var windowEvents = winEvents is null ? null : new SharedSource<Contracts.Workflow.WindowEvent>(winEvents);

        WorkflowTracking? tracking = null;
        // Forget tracking: stop and close both stores before track\ goes, reopen fresh ones after (only if it was running).
        var trackingWasOn = false;
        host.Forgetter.Before[ForgetScope.Tracking].Add(() => host.Loop.InvokeAsync(() =>
        {
            trackingWasOn = tracking is { Enabled: true };
            tracking?.Disable();
            return true;
        }));
        host.Forgetter.After[ForgetScope.Tracking].Add(() => host.Loop.InvokeAsync(() =>
        {
            if (trackingWasOn && tracking is { } t && host.ModuleRunning("tracking"))
            {
                t.Model = null;
                t.Enable();
            }

            return true;
        }));
        host.RegisterModule("tracking", () =>
        {
            // C9 (T0 tracking): Windows hooks, idle and session sources; none elsewhere (the feature still runs, empty).
            IEventSource<Contracts.Workflow.PresenceEvent>? presence = windows ? new BothPresence(new IdleSource(), new SessionSource()) : null;
            var store = host.Store;
            tracking = new WorkflowTracking(ctx, windowEvents?.Subscriber(), presence, () => new Core.Workflow.StoreSet(new Core.Workflow.EventStore(store.EventsDb), new Core.Workflow.EpisodeStore(store.EpisodesDb)), host.Key.Hasher("tok"))
            {
                PausedNow = () => host.Paused.Tracking,
                OwnNames = [Environment.UserName],
                OnAc = () => host.State.Current.Power is not { OnBattery: true },
                Busy = () => eventMode.Active is not null || (meeting is { Enabled: true } m && m.InCall()),
                Tasks = host.InternalTasks,
                PreregPath = Path.Combine(host.Options.InstallDir, "worker", "prereg.json"),
                PythonExe = Path.Combine(host.Options.InstallDir, "worker", "python", windows ? "python.exe" : "bin/python3"),
                WorkerDir = Path.Combine(host.Options.InstallDir, "worker"),
            };
            host.AddFeature(tracking);
        });

        // Focus reads whichever tracking runs at the moment it asks, so turning tracking on or off never rebuilds it (a block
        // in progress survives). Projects subscribes to one tracking instance's events, so it is rebuilt when tracking toggles.
        WorkflowTracking? Tracking() => host.ModuleRunning("tracking") && tracking is { Enabled: true } t ? t : null;
        host.RegisterModule("focus", () => host.AddFeature(new FocusTimer(ctx, eventMode, new NotesStore(host.Store.NotesDir))
        {
            TrackingOn = () => settings.ModuleOn("tracking") && Tracking() is not null,
            Measure = (from, to, intended) => Tracking()?.Measure(from, to, intended),
            CurrentWorkflow = () => Tracking()?.Current?.Shown?.Id,
        }));

        host.RegisterModule("projects", ["tracking"], () =>
        {
            var projects = new ProjectTime(ctx, new ProjectMap(host.Store.Projects));
            host.AddFeature(projects);
            if (Tracking() is { } t)
            {
                t.ActivityAssigned += (a, label, _, _) =>
                {
                    if (projects.Enabled)
                    {
                        projects.Add(label, a.ActiveMs, DateTimeOffset.FromUnixTimeMilliseconds(a.StartUtcMs));
                    }
                };
            }
        });

        // I11 watches are user-authored (app + title pattern); none until the flyout's picker writes them (M14).
        host.RegisterModule("windows_watch", () => host.AddFeature(new WindowWatch(ctx, windowEvents?.Subscriber())));

        // F2 (U3 D-W2): the agent's half of the switcher; tasktor-shell draws it (started by its supervisor, below).
        host.RegisterModule("switcher", () => host.AddFeature(new Core.Features.Windows.SwitcherFeedFeature(ctx, windowEvents?.Subscriber())
        {
            Jobs = () => [.. host.Features.OfType<Core.Features.Jobs.JobWatch>().SelectMany(w => w.Jobs).Where(j => j.Live).Select(j => new Contracts.Windows.SwitcherJob
            {
                Id = j.Id,
                Label = j.Label,
                Frozen = j.Pause == Contracts.Snapshot.PauseMode.Frozen,
                Pids = [j.Root.Pid, .. j.Workers.Select(w => w.Pid)],
            })],
            Supervisor = windows ? new Core.Features.HostSupervisor(ctx, Core.Features.HostSupervisor.ShellSpec(AppContext.BaseDirectory, host.Options.PipeName, host.Options.SwitcherPids), () => host.Consents.HasModule("switcher")) : null,
        }));

        host.RegisterModule("capture", () =>
        {
            // T1 agent side: the card and capture.* actions over tasktor-capture's control pipe. While that host is not
            // running the link fails fast and the card says capture is not running.
            // D-H9: the agent starts tasktor-capture in a kill-on-close Job Object only while this Module is on and its consent
            // (D-C22) is on record, and restarts a crash at most 3 times an hour.
            var supervisor = new HostSupervisor(ctx, HostSupervisor.CaptureSpec(AppContext.BaseDirectory, host.Store.Root), () => host.Consents.HasModule("capture"));
            host.AddFeature(capture = new CaptureFeature(ctx, (cmd, ct) => Hosts.Common.Pipe.CaptureLink.SendAsync(cmd, ct))
            {
                Supervisor = supervisor,
                ForegroundApp = windows ? Windows.Shell.Win32Windows.ForegroundExe : () => null,
                SetupExtension = windows ? browser => SetupExtension(browser, AppContext.BaseDirectory) : null,
                TerminalSettings = windows ? () => TerminalSettingsFile(AgentHost.KnownFolder("LocalAppData")) : () => null,
                OpenFile = windows ? OpenInEditor : null,
            });
        });

        host.RegisterModule("batteries", () => host.AddFeature(new DeviceBatteries(ctx, windows ? new BluetoothBatterySource() : new NoDevices())));
        AddWindowsFeatures(host, windows, windowEvents, winEvents);
    }

    /// <summary>C22.3: Windows Terminal's settings file (store, preview or unpackaged install) and its text, or null.</summary>
    private static (string Path, string? Text)? TerminalSettingsFile(string? localAppData)
    {
        if (localAppData is null)
        {
            return null;
        }

        string[] candidates =
        [
            Path.Combine(localAppData, "Packages", "Microsoft.WindowsTerminal_8wekyb3d8bbwe", "LocalState", "settings.json"),
            Path.Combine(localAppData, "Packages", "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe", "LocalState", "settings.json"),
            Path.Combine(localAppData, "Microsoft", "Windows Terminal", "settings.json"),
        ];
        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                return (path, info.Length <= 1024 * 1024 ? File.ReadAllText(path) : null);
            }
        }

        return null;
    }

    /// <summary>Opens a settings file in Notepad: the user edits it, Tasktor never does.</summary>
    private static bool OpenInEditor(string path)
    {
        try
        {
            using var editor = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe") { ArgumentList = { path }, UseShellExecute = false });
            return editor is not null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// C §5.5 setup: register <c>tasktor-nmh</c> for this user (HKCU + a manifest in the data folder, allowing only the
    /// companion's fixed id), then open the shipped <c>extension</c> folder and the browser's extensions page, where the user
    /// loads it unpacked. Launches local programs only; no network.
    /// </summary>
    private static bool SetupExtension(string browser, string baseDir)
    {
        var folder = Path.Combine(baseDir, "extension");
        var nmh = Path.Combine(baseDir, "tasktor-nmh.exe");
        if (!Directory.Exists(folder) || !File.Exists(nmh))
        {
            return false;
        }

        try
        {
            using (var register = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(nmh) { ArgumentList = { "--register" }, UseShellExecute = false, CreateNoWindow = true }))
            {
                if (register is null || !register.WaitForExit(10_000) || register.ExitCode != 0)
                {
                    return false;
                }
            }

            using var explorer = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false });
            var (exe, page) = browser == "edge" ? ("msedge.exe", "edge://extensions") : ("chrome.exe", "chrome://extensions");
            using var open = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { ArgumentList = { page }, UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    private static void AddWindowsFeatures(AgentHost host, bool windows, SharedSource<Contracts.Workflow.WindowEvent>? events, WinEventSource? source)
    {
        // D-W9: its own subscription to the shared WinEventSource (foreground events only), whatever the Modules.
        var fullscreen = new Core.Features.Windows.FullscreenSource(host.NewFeatureContext(), events?.Subscriber())
        {
            Window = windows ? Windows.Shell.Win32Windows.FullscreenWindow : _ => null,
            Monitors = windows ? Windows.Shell.Win32Windows.Monitors : () => [],
            Notification = windows ? Windows.Shell.Win32Windows.QueryNotificationState : () => Core.Windows.NotificationState.AcceptsNotifications,
        };
        host.AddFeature(fullscreen);
        if (!host.Settings.ModuleOn("presets")) return;
        bool Frozen(uint pid) => Core.Features.Windows.FrozenWindowGuard.IsFrozen(host.Store.Frozen, pid);
        bool Launch(Contracts.Presets.PresetLaunch app)
        {
            try
            {
                var info = new System.Diagnostics.ProcessStartInfo(app.Exe) { UseShellExecute = true };
                foreach (var arg in app.Args ?? []) info.ArgumentList.Add(arg);
                return System.Diagnostics.Process.Start(info) is not null;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return false; }
        }
        // U3: one hook thread. Interventions and launched windows come from WinEventSource's lifecycle lease, held only
        // while a restore waits (no second hook, no polling).
        var restorer = new Core.Features.Windows.PresetRestorer(host.NewFeatureContext())
        {
            Events = events?.Subscriber(),
            LeaseLifecycle = source is null ? null : source.LeaseLifecycle,
            Windows = windows ? () => Windows.Shell.Win32Windows.Enumerate(Frozen, include: host.Options.SwitcherPids is { Count: > 0 } fixture ? pid => fixture.Contains(pid) : null) : () => [],
            Monitors = windows ? Windows.Shell.Win32Windows.Monitors : () => [],
            IsFrozen = windows ? Frozen : _ => false,
            CommandLine = windows ? pid => (host.Options.SwitcherPids is not { Count: > 0 } fixture || fixture.Contains(pid)) ? ProcessInfo.CommandLine(new ProcKey(pid, 0)) : null : _ => null, // a slot's launch, shown before saving
            Move = windows ? move => Windows.Shell.Win32Windows.Place(move.Hwnd, move.Rect, move.Show, Frozen) : _ => false,
            Launch = windows ? exe => Launch(new Contracts.Presets.PresetLaunch { Exe = exe }) : _ => false,
            LaunchWithArgs = windows ? Launch : null,
            ApplyLayout = windows ? layout => Windows.Shell.FancyZonesBridge.Find() is { } exe ? Windows.Shell.FancyZonesBridge.Apply(exe, layout.Monitor, layout.Layout) ? null : "FancyZones failed" : "FancyZones unavailable" : null,
        };
        host.AddFeature(restorer);
    }

    /// <summary>
    /// One underlying event source shared by several features: started on the first subscriber's Start, stopped when the
    /// last one stops; each event goes to every started subscriber.
    /// </summary>
    private sealed class SharedSource<T>(IEventSource<T> inner)
    {
        private readonly Lock _gate = new();
        private readonly List<Action<T>> _posts = [];

        public IEventSource<T> Subscriber() => new Sub(this);

        private void Fan(T item)
        {
            Action<T>[] posts;
            lock (_gate)
            {
                posts = [.. _posts];
            }

            foreach (var p in posts)
            {
                p(item);
            }
        }

        private sealed class Sub(SharedSource<T> owner) : IEventSource<T>
        {
            private Action<T>? _post;

            public string Name => owner.Name;

            public void Start(Action<T> post)
            {
                bool first;
                lock (owner._gate)
                {
                    _post = post;
                    owner._posts.Add(post);
                    first = owner._posts.Count == 1;
                }

                if (first)
                {
                    owner.Inner.Start(owner.Fan);
                }
            }

            public void Stop()
            {
                bool last;
                lock (owner._gate)
                {
                    if (_post is null || !owner._posts.Remove(_post))
                    {
                        return;
                    }

                    _post = null;
                    last = owner._posts.Count == 0;
                }

                if (last)
                {
                    owner.Inner.Stop();
                }
            }
        }

        private string Name => inner.Name;

        private IEventSource<T> Inner => inner;
    }

    /// <summary>Idle and session presence as one source (the feature takes one).</summary>
    private sealed class BothPresence(IEventSource<Contracts.Workflow.PresenceEvent> a, IEventSource<Contracts.Workflow.PresenceEvent> b) : IEventSource<Contracts.Workflow.PresenceEvent>
    {
        public string Name => "presence";

        public void Start(Action<Contracts.Workflow.PresenceEvent> post)
        {
            a.Start(post);
            b.Start(post);
        }

        public void Stop()
        {
            a.Stop();
            b.Stop();
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
