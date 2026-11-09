using System.Text.Json;
using Tasktor.Contracts;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Snapshot;
using Tasktor.Core.Actions;
using Tasktor.Core.Logging;
using Tasktor.Core.Features;
using Tasktor.Core.Plugins;
using Tasktor.Core.Rules;
using Tasktor.Core.Loop;
using Tasktor.Core.State;
using Tasktor.Core.Storage;
using Tasktor.Core.Time;
using Tasktor.Core.Windows;

namespace Tasktor.Agent.Host;

/// <summary>What the composition root needs from its caller: the store, the clock and the OS adapters (fakes in tests).</summary>
public sealed record AgentOptions
{
    public required StoreLayout Store { get; init; }

    public IClock Clock { get; init; } = SystemClock.Instance;

    public string? PipeName { get; init; }

    /// <summary>For checks: the only processes whose windows the switcher may list (empty = all).</summary>
    public IReadOnlyList<uint> SwitcherPids { get; init; } = [];

    public string Version { get; init; } = typeof(AgentHost).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    /// <summary>Fresh single-process read for the guards (Windows: <c>Win32ProcessTable</c>).</summary>
    public Func<ProcKey, ProcessRow?> ReadProcess { get; init; } = _ => null;

    public IProcessControl? ProcessControl { get; init; }

    /// <summary>The install dir (bundled Python, <c>plugins-builtin\</c>, worker exes). Defaults to the agent's folder.</summary>
    public string InstallDir { get; init; } = AppContext.BaseDirectory;

    public INotifier? Notifier { get; init; }

    public bool WriteSnapshotFile { get; init; } = true;

    public ILogWriter? LogWriter { get; init; }

    /// <summary>How the install key is protected at rest (DPAPI on Windows).</summary>
    public KeyProtection KeyProtection { get; init; } = KeyProtection.None;

    /// <summary>Opens URIs, files and folders (the shell); tests record calls.</summary>
    public Func<string, bool> Launch { get; init; } = ShellLauncher.Launch;

    /// <summary>
    /// H8 start at logon (the HKCU Run value) and the command it holds. Null in a check instance and off Windows: then
    /// <c>start_with_windows</c> touches nothing.
    /// </summary>
    public Core.Platform.LogonStart? LogonStart { get; init; }

    public string? LogonCommand { get; init; }

    /// <summary>For tests: run source reads inline instead of on the thread pool.</summary>
    public Action<Action>? OffLoopReads { get; init; }

    /// <summary>For tests: run handlers inline instead of on the thread pool.</summary>
    public Func<Func<ActionResult>, Task<ActionResult>>? OffLoop { get; init; }
}

/// <summary>
/// The agent's hand-written composition root (no reflection DI; Native AOT). Owns the one logic loop and wires state,
/// cards, the publisher, the router and the pipe. Features attach through <see cref="Attach"/>. Tests run it in-process.
/// </summary>
public sealed class AgentHost : IAsyncDisposable
{
    private readonly CoalescingWriter _writer = new();
    private readonly List<IDisposable> _owned = [];
    private PausedState _paused = new();
    private SelfStats? _self;
    private bool _cliAllowDestructive;

    public AgentHost(AgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
        Store = options.Store;
        Directory.CreateDirectory(Store.Root);
        Log = new LogSink(options.Clock, options.LogWriter ?? new FileLogWriter(Store.LogDir));
        Loop = new LogicLoop(options.Clock, ex => Log.Error("loop.error", LogField.Of("type", ex.GetType().Name), LogField.Of("at", ErrorWhere.Of(ex))));
        Scheduler = new Scheduler(options.Clock);
        _schedulerDriver = new SchedulerDriver(Loop, Scheduler); // without it no sampler ever runs
        State = new StateTracker();
        Cards = new CardTemplates();
        Publisher = new SnapshotPublisher(Loop, State, Cards, AgentInfo, options.WriteSnapshotFile ? bytes => _writer.Enqueue(Store.Snapshot, bytes) : null);
        Router = new ActionRouter(
            Loop,
            new OriginPolicy { CliAllowDestructive = () => _cliAllowDestructive },
            GuardChain.Default(),
            options.ReadProcess,
            Log,
            options.OffLoop);
        Pipe = new PipeServer(options.PipeName ?? Hosts.Common.Pipe.PipeName.ForCurrentUser(), Loop, Publisher, Router, Scheduler, options.Version, Log);
        Settings = new SettingsStore(Store, (path, bytes) => _writer.Enqueue(path, bytes));
        Settings.Load();
        if (Settings.Error is { } settingsError)
        {
            Log.Warn("settings.invalid", LogField.Of("reason", settingsError.Split(':')[0]));
        }

        _cliAllowDestructive = Settings.AllowDestructiveCli;
        _modulesAtStart = [.. Contracts.Config.ModuleCatalog.All.Where(m => Settings.ModuleOn(m.Id)).Select(m => m.Id)];
        Consents = new ConsentStore(Store, (path, bytes) => _writer.Enqueue(path, bytes));
        Consents.Load();
        SelfUse = new SelfUseCounter(Store, (path, bytes) => _writer.Enqueue(path, bytes));
        SelfUse.Load();
        Key = InstallKey.LoadOrCreate(Store, options.KeyProtection);
        Forgetter = new Forgetter(Store);
        Exporter = new DataExporter();
        Archive = new SettingsArchive(Store);
        LoadPause();
        History.Changed += () => UpdateState(s => s with { RecentActions = History.Items.Count == 0 ? null : [.. History.Items] });
        UpdateState(s => s with { Pinned = Settings.Current.Flyout?.PinnedCards, HiddenPages = Settings.Current.Flyout?.HiddenPages is { Count: > 0 } h ? h : null });
        Router.Finished += (req, result, summary) => History.Record(req, result, summary, Loop.Clock.UtcNow);
        _owned.Add(Loop.Every(TimeSpan.FromMinutes(1), () => History.Prune(Loop.Clock.UtcNow)));
        Router.Completed += (req, result) =>
        {
            if (result.Status is ActionStatus.Ok or ActionStatus.Partial)
            {
                SelfUse.Count("action." + req.Action, Loop.Clock.UtcNow);
            }
        };
        _owned.Add(Loop.Every(TimeSpan.FromMinutes(1), SelfUse.Flush));
        _owned.Add(_schedulerDriver);
        Processes = options.ProcessControl ?? new PortableProcessControl(new ChildProcessRunner(options.InstallDir));
        var bundledPython = Path.Combine(options.InstallDir, "worker", "python", OperatingSystem.IsWindows() ? "python.exe" : "bin/python3");
        Plugins = new PluginHost(Store, Consents, Processes, Loop.Post, Log)
        {
            BuiltinDir = Path.Combine(options.InstallDir, "plugins-builtin"),
            PythonPath = File.Exists(bundledPython) ? bundledPython : null,
            SecretProtection = options.KeyProtection,
        };
        Plugins.Changed += OnPluginsChanged;
        Cards.Add(Plugins);
        Rules = new RuleRunner(Loop, new RuleEngine(KnownFolder), Router, options.Notifier ?? NullNotifier.Instance, Log, () => _features, change => UpdateState(change));
        RegisterCoreHandlers();
        RegisterStorageHandlers();
        RegisterPluginHandlers();
        RegisterModuleHandlers();
        RegisterExportTables();
    }

    public AgentOptions Options { get; }

    public StoreLayout Store { get; }

    public LogSink Log { get; }

    public LogicLoop Loop { get; }

    public Scheduler Scheduler { get; }

    /// <summary>Undoable actions of the last 30 minutes (snapshot <c>recent_actions</c>).</summary>
    public ActionHistory History { get; } = new();

    private readonly SchedulerDriver _schedulerDriver;

    public StateTracker State { get; }

    public CardTemplates Cards { get; }

    public SnapshotPublisher Publisher { get; }

    public ActionRouter Router { get; }

    public PipeServer Pipe { get; }

    public PausedState Paused => _paused;

    public SettingsStore Settings { get; }

    public ConsentStore Consents { get; }

    /// <summary>Wait until every queued state file (config, consents, snapshot) is on disk.</summary>
    public Task FlushWritesAsync() => _writer.FlushAsync();

    public SelfUseCounter SelfUse { get; }

    public InstallKey Key { get; }

    public Forgetter Forgetter { get; }

    public DataExporter Exporter { get; }

    public SettingsArchive Archive { get; }

    public IProcessControl Processes { get; }

    public PluginHost Plugins { get; }

    public RuleRunner Rules { get; }

    public InternalTasks InternalTasks { get; } = new();

    public IReadOnlyList<Feature> Features => _features;

    private readonly List<Feature> _features = [];

    /// <summary>A context for a feature: the loop, scheduler, router, state and the platform services of this host.</summary>
    public FeatureContext NewFeatureContext() => new()
    {
        Loop = Loop,
        Scheduler = Scheduler,
        Router = Router,
        Log = Log,
        Settings = Settings,
        Store = Store,
        UpdateState = UpdateState,
        State = () => State.Current,
        RegisterPull = Pipe.RegisterPull,
        Notifier = Options.Notifier ?? NullNotifier.Instance,
        Processes = Processes,
        Key = Key,
        Launch = Options.Launch,
        Persist = (path, bytes) => _writer.Enqueue(path, bytes),
        RulesDirty = Rules.MarkDirty,
        RuleEnabled = id => Rules.Engine.Rules.IsOn(id),
        // Off-loop reads used to lose their exceptions inside Task.Run; now they are logged with where they came from.
        OffLoop = Options.OffLoopReads ?? (work => Task.Run(() =>
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                Log.Error("offloop.error", LogField.Of("type", ex.GetType().Name), LogField.Of("at", ErrorWhere.Of(ex)));
            }
        })),
        RegisterExport = Exporter.Register,
    };

    /// <summary>Attach and enable a feature (loop-only after start).</summary>
    public void AddFeature(Feature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        if (_building is { } module)
        {
            _moduleFeatures[module].Add(feature); // built for that Module: removed when it is turned off
        }

        _features.Add(feature);
        Cards.Add(feature);
        feature.Enable();
        Publisher.MarkChanged();
    }

    /// <summary>Load <c>rules.default.json</c> (install dir) + ticked pack rules + the user's <c>rules.json</c>; swap the set.</summary>
    public RuleSet ReloadRules()
    {
        var defaultsPath = Path.Combine(Options.InstallDir, "rules.default.json");
        var defaults = File.Exists(defaultsPath) ? File.ReadAllText(defaultsPath) : null;
        var user = File.Exists(Store.Rules) ? File.ReadAllText(Store.Rules) : null;
        var pack = Plugins.TickedPackRules((packId, ruleId) => Consents.Current.Modules.ContainsKey(PackRuleKey(packId, ruleId)));
        var set = RulesLoader.Load(defaults, user, Consents.HasArrangeConsent, pack is null ? null : [pack]);
        Rules.SetRules(set);
        RulesReloaded?.Invoke(set);
        Rules.MarkDirty();
        return set;
    }

    /// <summary>Raised on the loop after the rule set changed (features re-read groups, the error card updates).</summary>
    public event Action<RuleSet>? RulesReloaded;

    /// <summary>Known Folders by name for rules and features (never literal user paths in shipped files).</summary>
    public static string? KnownFolder(string name) => name switch
    {
        "OneDrive" => Environment.GetEnvironmentVariable("OneDrive") is { Length: > 0 } od ? od : null,
        "Downloads" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        "Documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Desktop" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        "UserProfile" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "LocalAppData" => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Temp" => Path.GetTempPath(),
        "Pictures" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        "Music" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        "Videos" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        _ => null,
    };

    /// <summary>Raised on the loop when the user asks to quit (tray, flyout).</summary>
    public event Action? QuitRequested;

    /// <summary><c>cli.allow_destructive</c> (off by default). Settings set it on the loop.</summary>
    public bool CliAllowDestructive
    {
        get => _cliAllowDestructive;
        set => _cliAllowDestructive = value;
    }

    /// <summary>Attach a card provider (a feature). Loop-only after start.</summary>
    public void Attach(ICardProvider provider)
    {
        Cards.Add(provider);
        Publisher.MarkChanged();
    }

    /// <summary>Change state on the loop and schedule a publish.</summary>
    public void UpdateState(Func<SnapshotState, SnapshotState> change)
    {
        State.Update(change);
        Publisher.MarkChanged();
    }

    public void UpdateSelf(SelfStats self)
    {
        _self = self;
        Publisher.MarkChanged();
    }

    /// <summary>
    /// Registers the command line's hotkey (the tray's thread owns it), replacing the previous one; false when another
    /// program holds the combination. Null when there is no tray (tests, headless): the setting is still kept.
    /// </summary>
    public Func<Hotkey, bool>? ApplyHotkey { get; set; }

    /// <summary>The configured hotkey at start (after Composition set <see cref="ApplyHotkey"/>).</summary>
    private void InitHotkey()
    {
        if (ApplyHotkey is not { } apply)
        {
            return;
        }

        var h = Hotkey.FromSetting(Settings.Current.Flyout?.Hotkey);
        var ok = apply(h);
        UpdateState(s => s with { Hotkey = new Contracts.Snapshot.HotkeyState { Keys = h.ToString(), Registered = ok } });
    }

    /// <summary><c>settings.hotkey keys=…</c> (people only): register it now and keep it; a taken one keeps the old.</summary>
    private PreparedAction SetHotkey(ActionRequest req)
    {
        if (Hotkey.Parse(req.Args?.GetValueOrDefault("keys"), out var why) is not { } h)
        {
            return new PreparedAction { Summary = "Set the hotkey", Refusal = why };
        }

        var old = Hotkey.FromSetting(Settings.Current.Flyout?.Hotkey);
        var apply = ApplyHotkey;
        bool ok = true, oldOk = true;
        return new PreparedAction
        {
            Summary = "Command line hotkey " + h,
            Run = _ =>
            {
                if (apply is not null && !(ok = apply(h)))
                {
                    oldOk = apply(old); // put the old one back
                }

                return ok
                    ? new ActionResult { Action = req.Action, Status = ActionStatus.Ok, Message = "Command line: " + h }
                    : new ActionResult { Action = req.Action, Status = ActionStatus.Failed, Message = h + " is used by another program; kept " + old };
            },
            Done = _ =>
            {
                if (ok)
                {
                    Settings.Update(c => c with { Flyout = (c.Flyout ?? new Contracts.Config.FlyoutSettings()) with { Hotkey = h.ToString() } });
                }

                if (apply is not null)
                {
                    UpdateState(s => s with { Hotkey = new Contracts.Snapshot.HotkeyState { Keys = (ok ? h : old).ToString(), Registered = ok || oldOk } });
                }
            },
        };
    }

    private IDisposable? _rulesReload;

    /// <summary>
    /// U1 hot reload: rules.json is re-read and validated on every change (an editor's save, a switch, a restore), 300 ms
    /// after the last write so a save in several steps loads once. Errors go to the error card as at start.
    /// </summary>
    private void WatchRules()
    {
        _owned.Add(new FileWatch([Store.Rules], () => Loop.Post(() =>
        {
            _rulesReload?.Dispose();
            _rulesReload = Loop.At(Loop.Clock.UtcNow + TimeSpan.FromMilliseconds(300), () =>
            {
                _rulesReload = null;
                ReloadRules();
            });
        })));
    }

    /// <summary>Where a pack rule's tick is kept: the consent store, as <c>pack:&lt;pack&gt;:&lt;rule&gt;</c>.</summary>
    private static string PackRuleKey(string pack, string rule) => "pack:" + pack + ":" + rule;

    /// <summary>U4 D-G3 Settings → Rules: every rule (on and off), its source and last firing, and the load errors. Loop only.</summary>
    public Contracts.Rules.RulesReport RulesReport()
    {
        var set = Rules.Engine.Rules;
        static string Source(RuleOrigin o) => o switch { RuleOrigin.Default => "default", RuleOrigin.Pack => "pack", _ => "user" };
        Contracts.Rules.RuleInfo Info(CompiledRule r, bool on) => new()
        {
            Id = r.Id,
            Description = r.Rule.Description,
            Source = Source(r.Origin),
            On = on,
            LastFired = on ? Rules.Engine.LastFired(r.Id) : null,
        };
        return new Contracts.Rules.RulesReport
        {
            Rules = [.. set.Rules.Select(r => Info(r, true)), .. set.OffRules.Select(r => Info(r, false))],
            Errors = [.. set.Errors.Select(e => e.ToString())],
            AwaitingArrange = [.. set.AwaitingArrangeConsent.Select(r => new Contracts.Rules.ArrangeRuleInfo
            {
                Id = r.Id,
                Description = r.Rule.Description,
                Presets = [.. r.Rule.Then.Select(t => t.Arrange).OfType<string>().Distinct(StringComparer.Ordinal)],
            })],
            PackRules = [.. Plugins.OfferedPackRules().Select(x => new Contracts.Rules.PackRuleInfo
            {
                Pack = x.Pack.Id,
                PackName = x.Pack.Manifest.Name,
                Id = x.Rule.Id,
                Description = x.Rule.Description,
                Ticked = Consents.HasModule(PackRuleKey(x.Pack.Id, x.Rule.Id)),
            })],
        };
    }

    /// <summary>
    /// <c>rule.enable</c> / <c>rule.disable</c> (people only): a toggle written into rules.json as text (the user's comments
    /// stay; <see cref="RulesToggle"/>), then the rules reload. A rules.json that is not valid JSON is never overwritten.
    /// </summary>
    private PreparedAction SwitchRule(ActionRequest req, bool on)
    {
        var id = req.Target?.GetValueOrDefault("rule");
        var set = Rules.Engine.Rules;
        if (id is null || (set.Find(id) is null && !set.OffRules.Any(r => r.Id == id)))
        {
            return new PreparedAction { Summary = on ? "Turn rule on" : "Turn rule off", Refusal = "No such rule" };
        }

        var path = Store.Rules;
        var ok = false;
        return new PreparedAction
        {
            Summary = (on ? "Turn on " : "Turn off ") + id,
            Run = _ =>
            {
                try
                {
                    var text = RulesToggle.Apply(File.Exists(path) ? File.ReadAllText(path) : null, id, on);
                    var tmp = path + ".tmp";
                    File.WriteAllText(tmp, text);
                    File.Move(tmp, path, overwrite: true);
                    ok = true;
                    return new ActionResult { Action = req.Action, Status = ActionStatus.Ok, Message = (on ? "Turned on " : "Turned off ") + id };
                }
                catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
                {
                    return new ActionResult { Action = req.Action, Status = ActionStatus.Failed, Message = ex is FormatException f ? f.Message : "Could not write rules.json" };
                }
            },
            Done = _ =>
            {
                if (ok)
                {
                    ReloadRules();
                }
            },
        };
    }

    /// <summary>Start the loop thread and the pipe; publish the first snapshot (D-H10: start to first snapshot &lt; 1 s).</summary>
    public void Start()
    {
        Loop.Start();
        Loop.Post(() => ApplyLogonStart(Settings.Current.StartWithWindows == true));
        Loop.Post(InitHotkey);
        Loop.Post(() => Plugins.Discover(Loop.Clock.UtcNow));
        Loop.Post(() => ReloadRules());
        Loop.Post(WatchRules);
        Loop.Post(() => Publisher.Publish(force: true));
        Pipe.Start();
    }

    /// <summary>Run <paramref name="work"/> on the loop and wait for it (tests, startup wiring).</summary>
    public Task<T> OnLoop<T>(Func<T> work) => Loop.InvokeAsync(work);

    public Task OnLoop(Action work) => Loop.InvokeAsync(() =>
    {
        work();
        return true;
    });

    public void Own(IDisposable disposable) => _owned.Add(disposable);

    /// <summary>The last "Your data" scan (loop-only); null until the first scan finishes.</summary>
    internal Contracts.Export.DataReport? DataReport => _dataReport;

    private Contracts.Export.DataReport? _dataReport;
    private bool _scanningData;

    /// <summary>Start a "Your data" scan off the loop unless one is running; its result is posted back to the loop.</summary>
    internal void RescanData()
    {
        if (_scanningData)
        {
            return;
        }

        _scanningData = true;
        _ = Task.Run(() =>
        {
            Contracts.Export.DataReport? report = null;
            try
            {
                report = DataInventory.Scan(Store, Forgetter);
            }
            finally
            {
                Loop.Post(() =>
                {
                    _dataReport = report ?? _dataReport;
                    _scanningData = false;
                });
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        await Pipe.DisposeAsync().ConfigureAwait(false);
        Loop.Stop();
        foreach (var d in _owned)
        {
            d.Dispose();
        }

        SelfUse.Flush();
        await _writer.FlushAsync().ConfigureAwait(false);
        Loop.Dispose();
    }

    private AgentInfo AgentInfo() => new() { Version = Options.Version, Running = true, Paused = _paused, Self = _self };

    private void SetPaused(Func<PausedState, PausedState> change)
    {
        _paused = change(_paused);
        var file = new PauseFile { Tracking = _paused.Tracking, Capture = _paused.Capture, Refits = _paused.Refits };
        _writer.Enqueue(Store.Pause, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(file, CoreJsonContext.Default.PauseFile));
        Publisher.MarkChanged();
    }

    /// <summary>Pause persists across restarts (privacy rule 3).</summary>
    private void LoadPause()
    {
        try
        {
            if (File.Exists(Store.Pause) && System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(Store.Pause), CoreJsonContext.Default.PauseFile) is { } p)
            {
                _paused = new PausedState { Tracking = p.Tracking, Capture = p.Capture, Refits = p.Refits };
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }
    }

    /// <summary><c>open</c>: show the flyout, at a card when one is named (the flyout handles <c>tasktor://open</c>).</summary>
    private PreparedAction OpenCard(ActionRequest req)
    {
        var card = req.Target?.GetValueOrDefault("card");
        if (card is not null && !System.Text.RegularExpressions.Regex.IsMatch(card, "^[a-z0-9_.]{1,80}$"))
        {
            return new PreparedAction { Summary = "Open", Refusal = "Not a card id" };
        }

        var uri = card is null ? "tasktor://open" : "tasktor://open?card=" + card;
        return new PreparedAction
        {
            Summary = card is null ? "Open Tasktor" : "Open " + card,
            Run = _ => new ActionResult { Action = "open", Status = Options.Launch(uri) ? ActionStatus.Ok : ActionStatus.Failed, Message = card },
        };
    }

    /// <summary><c>card.pin</c> / <c>card.unpin</c>: Home's pinned cards, kept in <c>config.json</c> (Flyout settings).</summary>
    private PreparedAction Pin(ActionRequest req, bool pin)
    {
        var card = req.Target?.GetValueOrDefault("card");
        if (card is null || Cards.Find(card) is null && pin)
        {
            return new PreparedAction { Summary = pin ? "Pin" : "Unpin", Refusal = "No such card" };
        }

        return new PreparedAction
        {
            Summary = (pin ? "Pin " : "Unpin ") + card,
            Immediate = new ActionResult { Action = req.Action, Status = ActionStatus.Ok },
            Done = _ =>
            {
                Settings.Update(c =>
                {
                    // From the defaults while none were chosen, so the first pin adds to them and unpinning one keeps the rest.
                    var list = (c.Flyout?.PinnedCards ?? Hosts.Common.Deep.DeepPages.DefaultPinned).Where(x => x != card).ToList();
                    if (pin)
                    {
                        list.Add(card);
                    }

                    return c with { Flyout = (c.Flyout ?? new Contracts.Config.FlyoutSettings()) with { PinnedCards = list } }; // empty = nothing pinned
                });
                UpdateState(s => s with { Pinned = Settings.Current.Flyout?.PinnedCards }); // the viewer's Home reads it
            },
        };
    }

    private void RegisterCoreHandlers()
    {
        PreparedAction Toggle(string summary, Func<PausedState, PausedState> change) => new()
        {
            Summary = summary,
            Immediate = new ActionResult { Action = "pause", Status = ActionStatus.Ok, Message = summary },
            Done = r =>
            {
                if (r.Status == ActionStatus.Ok)
                {
                    SetPaused(change);
                }
            },
        };

        Router.Register("tracking.pause", _ => Toggle("Tracking paused", p => p with { Tracking = true }));
        Router.Register("tracking.resume", _ => Toggle("Tracking resumed", p => p with { Tracking = false }));
        Router.Register("refits.pause", _ => Toggle("Refits paused", p => p with { Refits = true }));
        Router.Register("refits.resume", _ => Toggle("Refits resumed", p => p with { Refits = false }));
        Router.Register("open", OpenCard);
        Router.Register("card.pin", req => Pin(req, pin: true));
        Router.Register("settings.hotkey", SetHotkey);
        Router.Register("settings.start_with_windows", req =>
        {
            // H8 portable build: the Settings → General switch and `tasktor settings start-with-windows on|off`.
            if (req.Args?.GetValueOrDefault("on") is not ("true" or "false") || Options.LogonStart is null)
            {
                return new PreparedAction { Summary = "Start with Windows", Refusal = Options.LogonStart is null ? "Start with Windows is not available in this instance" : "Say on or off" };
            }

            var wanted = req.Args["on"] == "true";
            return new PreparedAction
            {
                Summary = wanted ? "Start Tasktor with Windows" : "Stop starting Tasktor with Windows",
                Run = _ => ApplyLogonStart(wanted) is null
                    ? new ActionResult { Action = req.Action, Status = ActionStatus.Failed, Message = "Windows did not accept the change" }
                    : new ActionResult { Action = req.Action, Status = ActionStatus.Ok, Message = wanted ? "Tasktor starts when you sign in to Windows" : "Tasktor no longer starts with Windows" },
                Done = r =>
                {
                    if (r.Status == ActionStatus.Ok)
                    {
                        Settings.Update(c => c with { StartWithWindows = wanted });
                    }
                },
            };
        });
        Router.Register("settings.page", req =>
        {
            // Settings → Appearance: hide or show a deep-window page. Home, Your data and Settings cannot be hidden.
            var page = req.Args?.GetValueOrDefault("page");
            var shown = req.Args?.GetValueOrDefault("shown");
            if (Hosts.Common.Deep.DeepPages.Hideable.FirstOrDefault(p => p.Id == page) is not { } info || shown is not ("true" or "false"))
            {
                return new PreparedAction { Summary = "Show or hide a page", Refusal = "That page cannot be hidden" };
            }

            var show = shown == "true";
            return new PreparedAction
            {
                Summary = (show ? "Show " : "Hide ") + info.Name,
                Immediate = new ActionResult { Action = req.Action, Status = ActionStatus.Ok, Message = (show ? "Showing " : "Hid ") + info.Name },
                Done = _ =>
                {
                    Settings.Update(c =>
                    {
                        var list = (c.Flyout?.HiddenPages ?? []).Where(p => p != info.Id).ToList();
                        if (!show)
                        {
                            list.Add(info.Id);
                        }

                        return c with { Flyout = (c.Flyout ?? new Contracts.Config.FlyoutSettings()) with { HiddenPages = list.Count == 0 ? null : list } };
                    });
                    UpdateState(s => s with { HiddenPages = Settings.Current.Flyout?.HiddenPages });
                },
            };
        });
        Router.Register("rules.edit", _ =>
        {
            var path = Store.Rules;
            return new PreparedAction
            {
                Summary = "Edit rules.json",
                Run = _ =>
                {
                    try
                    {
                        if (!File.Exists(path))
                        {
                            File.WriteAllText(path, RulesToggle.Starter); // only when missing: the user's file is never rewritten here
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        return new ActionResult { Action = "rules.edit", Status = ActionStatus.Failed, Message = "Could not create rules.json" };
                    }

                    return Options.Launch("edit:" + path)
                        ? new ActionResult { Action = "rules.edit", Status = ActionStatus.Ok, Message = "Opened rules.json; changes apply when you save" }
                        : new ActionResult { Action = "rules.edit", Status = ActionStatus.Failed, Message = "No editor opened " + path };
                },
            };
        });
        Router.Register("rule.allow_arrange", req =>
        {
            // U3 OQ-W2: the one-time consent that lets this rule call arrange (reversible; it never launches anything).
            var id = req.Target?.GetValueOrDefault("rule");
            if (id is null || !Rules.Engine.Rules.AwaitingArrangeConsent.Any(r => r.Id == id))
            {
                return new PreparedAction { Summary = "Let a rule arrange windows", Refusal = "No rule is waiting for that consent" };
            }

            return new PreparedAction
            {
                Summary = "Let " + id + " arrange windows",
                Immediate = new ActionResult { Action = req.Action, Status = ActionStatus.Ok, Message = id + " may now arrange windows" },
                Done = _ =>
                {
                    Consents.GiveArrange(id, Loop.Clock.UtcNow);
                    ReloadRules();
                },
            };
        });
        Router.Register("pack.rule", req =>
        {
            // U5: a pack's rules all start off; ticking one is the user's consent to it, and loads it.
            var a = req.Args ?? new Dictionary<string, string>();
            var (pack, rule, on) = (a.GetValueOrDefault("pack"), a.GetValueOrDefault("rule"), a.GetValueOrDefault("on"));
            if (on is not ("true" or "false") || !Plugins.OfferedPackRules().Any(x => x.Pack.Id == pack && x.Rule.Id == rule))
            {
                return new PreparedAction { Summary = "Tick a pack rule", Refusal = "No such rule in a pack that is on" };
            }

            var tick = on == "true";
            return new PreparedAction
            {
                Summary = (tick ? "Turn on " : "Turn off ") + rule,
                Immediate = new ActionResult { Action = req.Action, Status = ActionStatus.Ok, Message = (tick ? "Turned on " : "Turned off ") + rule },
                Done = _ =>
                {
                    if (tick)
                    {
                        Consents.GiveModule(PackRuleKey(pack!, rule!), Loop.Clock.UtcNow);
                    }
                    else
                    {
                        Consents.RevokeModule(PackRuleKey(pack!, rule!));
                    }

                    ReloadRules();
                },
            };
        });
        Router.Register("rule.enable", req => SwitchRule(req, on: true));
        Router.Register("rule.disable", req => SwitchRule(req, on: false));
        Pipe.RegisterPull("rules", _ => JsonSerializer.SerializeToNode(RulesReport(), ContractsJsonContext.Default.RulesReport));
        Router.Register("card.unpin", req => Pin(req, pin: false));
        Router.Register("status", _ => new PreparedAction { Summary = "status", Immediate = new ActionResult { Action = "status", Status = ActionStatus.Ok } });
        Router.Register("quit", _ => new PreparedAction
        {
            Summary = "Quit Tasktor",
            Immediate = new ActionResult { Action = "quit", Status = ActionStatus.Ok, Message = "Quitting" },
            Done = _ => QuitRequested?.Invoke(),
        });
        Router.Register("log.verbose", req => new PreparedAction
        {
            Summary = "Verbose log",
            Immediate = new ActionResult { Action = "log.verbose", Status = ActionStatus.Ok, Message = req.Args?.GetValueOrDefault("on") == "false" ? "Verbose log off" : "Verbose log on for 24 h" },
            Done = _ => Log.SetVerbose(req.Args?.GetValueOrDefault("on") != "false"),
        });
    }

    private PreparedAction OffLoop(string summary, Func<string> work, Action? done = null) => new()
    {
        Summary = summary,
        Run = _ =>
        {
            try
            {
                return new ActionResult { Action = summary, Status = ActionStatus.Ok, Message = work() };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or System.Text.Json.JsonException)
            {
                return new ActionResult { Action = summary, Status = ActionStatus.Failed, Message = summary + " failed: " + ex.Message };
            }
        },
        Done = r =>
        {
            if (r.Status == ActionStatus.Ok)
            {
                done?.Invoke();
            }
        },
    };

    private void RegisterStorageHandlers()
    {
        // "Your data": the last scan, and a fresh one off the loop (a directory walk is I/O; the loop only takes the result).
        Pipe.RegisterPull("data", _ =>
        {
            RescanData();
            return _dataReport is null ? null : JsonSerializer.SerializeToNode(_dataReport, ContractsJsonContext.Default.DataReport);
        });
        Router.Register("forget", req =>
        {
            var what = req.Target?.GetValueOrDefault("what");
            if (!Forgetter.TryParse(what, out var scope))
            {
                return new PreparedAction { Summary = "forget", Refusal = "forget: learned, tracking, capture or all" };
            }

            var paths = Forgetter.PathsFor(scope);
            return OffLoop(
                "Forget " + what + " (" + paths.Count + " stores)",
                () =>
                {
                    var r = Forgetter.ForgetAsync(scope).GetAwaiter().GetResult();
                    return "Forgot " + what + ": removed " + r.Removed.Count + " item(s)";
                },
                () =>
                {
                    if (scope == ForgetScope.All)
                    {
                        SelfUse.Clear();
                    }

                    Log.Info("forget", LogField.Of("scope", what));
                });
        });

        // While the capture Module runs, its feature takes this action over (the host owns the vault's key). Without it
        // nothing here can open the vault, so say so rather than report "forgot 0" with records still on disk.
        Router.Register("capture.forget", req =>
        {
            if (Forgetter.ForgetCaptureSince is null)
            {
                return new PreparedAction { Summary = "capture.forget", Refusal = "Capture is not running: turn it on to forget a time, app or site, or forget all capture data" };
            }

            var since = ParseSince(req.Args?.GetValueOrDefault("since") ?? "1h", Loop.Clock.UtcNow);
            if (since is null)
            {
                return new PreparedAction { Summary = "capture.forget", Refusal = "--since takes 15m, 1h, today or a duration" };
            }

            return OffLoop("Forget capture since " + since.Value.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), () =>
            {
                var r = Forgetter.ForgetAsync(ForgetScope.Capture, since).GetAwaiter().GetResult();
                return "Forgot " + r.CaptureRecords + " capture record(s)";
            });
        });

        Router.Register("export.data", req =>
        {
            var a = req.Args ?? new Dictionary<string, string>();
            var now = Loop.Clock.UtcNow;
            var folder = a.GetValueOrDefault("to") ?? DataExporter.DefaultFolder(now);
            var sinceText = a.GetValueOrDefault("since");
            var since = sinceText is null ? null : ParseSince(sinceText, now);
            var options = new ExportOptions
            {
                Folder = folder,
                Now = now,
                Version = Options.Version,
                Since = sinceText,
                Format = a.GetValueOrDefault("format") ?? "both",
                IncludeRaw = a.GetValueOrDefault("include_raw") == "true",
                IncludeCapture = a.GetValueOrDefault("include_capture") == "true",
            };
            var cloud = DataExporter.IsCloudSynced(folder);
            return OffLoop(
                // The confirm dialog shows this line: it names what the include flags add (D-S7: explicit flags + confirmation).
                "Export my data to " + folder
                    + (options.IncludeRaw && options.IncludeCapture ? ", including window titles and decrypted capture records"
                        : options.IncludeRaw ? ", including window titles"
                        : options.IncludeCapture ? ", including decrypted capture records" : string.Empty)
                    + (cloud ? " (a cloud-synced folder: files will leave this PC)" : string.Empty),
                () =>
                {
                    var r = Exporter.Export(options, since);
                    var rows = r.Manifest.Tables.Sum(t => t.Rows);
                    return "Exported " + r.Manifest.Tables.Count + " tables (" + rows + " rows) to " + r.Folder + " — plain text on disk"
                        + (r.CloudFolder ? "; this folder is cloud-synced" : string.Empty);
                },
                () => Log.Info("export", LogField.Of("tables", Exporter.Tables.Count)));
        });

        Router.Register("settings.export", req =>
        {
            var path = req.Args?.GetValueOrDefault("path");
            if (string.IsNullOrEmpty(path))
            {
                return new PreparedAction { Summary = "settings.export", Refusal = "settings export needs a zip path" };
            }

            var args = req.Args!;
            var learned = args.GetValueOrDefault("include_learned") == "true";
            var notes = args.GetValueOrDefault("include_notes") == "true";
            return OffLoop("Export settings", () => "Wrote " + string.Join(", ", Archive.Export(path, learned, notes)) + " to " + path);
        });

        Router.Register("settings.import", req =>
        {
            var path = req.Args?.GetValueOrDefault("path");
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return new PreparedAction { Summary = "settings.import", Refusal = "settings import needs an existing zip" };
            }

            return OffLoop("Import settings from " + Path.GetFileName(path), () => "Imported " + Archive.Import(path).Count + " file(s)", ReloadSettings);
        });

        Router.Register("settings.reset", _ => OffLoop("Reset settings and rules to defaults (data is kept)", () =>
        {
            Archive.Reset();
            return "Settings and rules reset; your data is kept";
        }, ReloadSettings));

        Router.Register("log.bundle", req =>
        {
            var path = req.Args?.GetValueOrDefault("to")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Tasktor exports", "tasktor-logs-" + Loop.Clock.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".zip");
            return OffLoop("Log bundle", () =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var files = Archive.LogBundle(path);
                return "Wrote " + path + ": " + string.Join(", ", files);
            });
        });

        Router.Register("uninstall", _ => new PreparedAction
        {
            Summary = "Remove Tasktor's logon entry and notification identity (your data stays)",
            Run = _ =>
            {
                var removed = Uninstallers.Select(u => u()).Where(m => m is not null).ToList();
                if (ApplyLogonStart(false) == Core.Platform.LogonStartChange.Removed)
                {
                    removed.Add("Removed the start-with-Windows entry");
                }

                return new ActionResult { Action = "uninstall", Status = ActionStatus.Ok, Message = removed.Count == 0 ? "Nothing to remove" : string.Join("; ", removed) };
            },
            Done = _ =>
            {
                if (Settings.Current.StartWithWindows == true)
                {
                    Settings.Update(c => c with { StartWithWindows = false }); // a later start must not put the entry back
                }
            },
        });
    }

    /// <summary>Platform clean-ups for <c>tasktor uninstall</c> (portable: Run key, toast identity). Each returns what it removed, or null.</summary>
    public List<Func<string?>> Uninstallers { get; } = [];

    /// <summary>
    /// H8: keep the HKCU Run value in line with <c>start_with_windows</c> (at start, on a settings change, from the switch).
    /// Null when Windows refused the change; nothing happens in a check instance (no <see cref="AgentOptions.LogonStart"/>).
    /// </summary>
    private Core.Platform.LogonStartChange? ApplyLogonStart(bool wanted)
    {
        if (Options.LogonStart is not { } start || Options.LogonCommand is not { } command)
        {
            return Core.Platform.LogonStartChange.None;
        }

        try
        {
            var change = start.Apply(wanted, command);
            if (change != Core.Platform.LogonStartChange.None)
            {
                Log.Info("logon_start.changed", LogField.Of("change", change.ToString().ToLowerInvariant()));
            }

            return change;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Warn("logon_start.failed", LogField.Of("type", e.GetType().Name));
            return null;
        }
    }

    private void ReloadSettings()
    {
        Settings.Load();
        ApplyLogonStart(Settings.Current.StartWithWindows == true);
        _cliAllowDestructive = Settings.AllowDestructiveCli;
        SettingsReloaded?.Invoke();
    }

    /// <summary>Raised on the loop after settings change (features re-read their sections).</summary>
    public event Action? SettingsReloaded;

    /// <summary><c>15m</c>, <c>1h</c>, <c>30d</c> → now minus that; <c>today</c> → local midnight.</summary>
    public static DateTimeOffset? ParseSince(string text, DateTimeOffset now)
    {
        if (text == "today")
        {
            var local = now.ToLocalTime();
            return new DateTimeOffset(local.Date, local.Offset).ToUniversalTime();
        }

        return DurationText.TryParse(text, out var span) ? now - span : null;
    }

    private void RegisterExportTables()
    {
        Exporter.Register(new ExportTableSource("selfuse", ["feature", "count"], _ => SelfUse.Counts.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => new object?[] { k.Key, k.Value })));
        Exporter.Register(new ExportTableSource("actions_log", ["at", "action", "target", "origin", "result"], since =>
            LogScanner.Read(Store.LogDir, since).Where(e => e.Event == "action")
                .Select(e => new object?[] { e.At, e.Fields.GetValueOrDefault("action"), e.Fields.GetValueOrDefault("target"), e.Fields.GetValueOrDefault("origin"), e.Fields.GetValueOrDefault("result") })));
        Exporter.Register(new ExportTableSource("alerts_log", ["at", "rule", "severity"], since =>
            LogScanner.Read(Store.LogDir, since).Where(e => e.Event == "rule.fired")
                .Select(e => new object?[] { e.At, e.Fields.GetValueOrDefault("rule"), e.Fields.GetValueOrDefault("severity") })));
    }

    private IDisposable? _pluginTimer;

    private void OnPluginsChanged()
    {
        UpdateState(s => s with { Plugins = Plugins.Statuses() });
        _pluginTimer?.Dispose();
        _pluginTimer = null;
        if (Plugins.NextDue is { } due)
        {
            _pluginTimer = Loop.At(due, () =>
            {
                _pluginTimer = null;
                Plugins.Tick(Loop.Clock.UtcNow);
                OnPluginsChanged();
            });
        }
    }

    /// <summary>
    /// The Modules composed at start. A Module registered with <see cref="RegisterModule"/> starts and stops live; any
    /// other one (threaded through always-on plumbing) applies at the next start, and the <c>modules</c> pull says so.
    /// </summary>
    private readonly HashSet<string> _modulesAtStart;

    private readonly Dictionary<string, (Action Build, string[] DependsOn)> _moduleBuilders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Feature>> _moduleFeatures = new(StringComparer.Ordinal);
    private string? _building;

    /// <summary>
    /// Composition registers a Module: <paramref name="build"/> adds its features (through <see cref="AddFeature"/>) and
    /// runs now if the Module is on, and again whenever it is turned on. Modules it depends on (the other overload) are
    /// wired into it at build time, so it is rebuilt when one of them starts or stops. Loop only.
    /// </summary>
    public void RegisterModule(string id, Action build) => RegisterModule(id, [], build);

    /// <inheritdoc cref="RegisterModule(string, Action)"/>
    public void RegisterModule(string id, string[] dependsOn, Action build)
    {
        ArgumentNullException.ThrowIfNull(build);
        _moduleBuilders[id] = (build, dependsOn);
        if (Settings.ModuleOn(id))
        {
            StartModule(id);
        }
    }

    /// <summary>Whether a Module's features run now.</summary>
    public bool ModuleRunning(string id) =>
        _moduleBuilders.ContainsKey(id) ? _moduleFeatures.ContainsKey(id) : _modulesAtStart.Contains(id);

    private void StartModule(string id)
    {
        if (_moduleFeatures.ContainsKey(id) || !_moduleBuilders.TryGetValue(id, out var m))
        {
            return;
        }

        _moduleFeatures[id] = [];
        _building = id;
        try
        {
            m.Build();
        }
        catch (Exception ex)
        {
            Log.Error("module.start", LogField.Of("id", id), LogField.Of("type", ex.GetType().Name), LogField.Of("at", ErrorWhere.Of(ex)));
            StopModule(id); // a Module that failed to build runs nothing
        }
        finally
        {
            _building = null;
        }
    }

    private void StopModule(string id)
    {
        if (!_moduleFeatures.Remove(id, out var features))
        {
            return;
        }

        foreach (var f in Enumerable.Reverse(features))
        {
            f.Disable();
            _features.Remove(f);
            Cards.Remove(f);
        }

        Publisher.MarkChanged();
        Rules.MarkDirty();
    }

    /// <summary>Turn a registered Module on or off now, and rebuild the running Modules wired to it. False: needs a restart.</summary>
    private bool ApplyModule(string id, bool on)
    {
        if (!_moduleBuilders.ContainsKey(id))
        {
            return false;
        }

        if (on)
        {
            StartModule(id);
        }
        else
        {
            StopModule(id);
        }

        foreach (var dependent in _moduleBuilders.Where(kv => kv.Value.DependsOn.Contains(id) && _moduleFeatures.ContainsKey(kv.Key)).Select(kv => kv.Key).ToList())
        {
            StopModule(dependent);
            StartModule(dependent);
        }

        Publisher.MarkChanged();
        return true;
    }

    /// <summary>
    /// <c>module.enable</c> / <c>module.disable</c> (user only): turning a Module on records its consent (U1 H1) and sets
    /// config.json; the <c>modules</c> pull lists each Module with its state now and after a restart.
    /// </summary>
    private void RegisterModuleHandlers()
    {
        PreparedAction Set(ActionRequest req, bool on)
        {
            if (Contracts.Config.ModuleCatalog.Find(req.Target?.GetValueOrDefault("module")) is not { } info)
            {
                return new PreparedAction { Summary = "module", Refusal = "No such Module" };
            }

            // Capture asks through its own consent screen (D-C22), which sends screen=capture with the user's ticked
            // acknowledgement; the generic Module dialog cannot turn it on.
            if (on && info.Id == "capture" && (req.Args?.GetValueOrDefault("screen") != "capture" || req.Args?.GetValueOrDefault("acknowledged") != "true"))
            {
                return new PreparedAction { Summary = "Turn on Capture", Refusal = "Capture is turned on from its own consent screen" };
            }

            var restart = !_moduleBuilders.ContainsKey(info.Id) && on != _modulesAtStart.Contains(info.Id);
            var message = (on ? "Turned on " : "Turned off ") + info.Name + (restart ? ". Restart Tasktor to apply it." : ".");
            return new PreparedAction
            {
                Summary = (on ? "Turn on " : "Turn off ") + info.Name,
                Immediate = new ActionResult { Action = req.Action, Status = ActionStatus.Ok, Message = message },
                Done = _ =>
                {
                    if (on)
                    {
                        Consents.GiveModule(info.Id, Loop.Clock.UtcNow);
                    }

                    Settings.Update(c => c with { Modules = new Dictionary<string, bool>(c.Modules ?? new Dictionary<string, bool>(), StringComparer.Ordinal) { [info.Id] = on } });
                    ApplyModule(info.Id, on);
                    Log.Info("module", LogField.Of("id", info.Id), LogField.Of("on", on ? "true" : "false"));
                },
            };
        }

        Router.Register("module.enable", req => Set(req, on: true));
        Router.Register("module.disable", req => Set(req, on: false));
        Pipe.RegisterPull("modules", _ => JsonSerializer.SerializeToNode(
            new Contracts.Config.ModulesReport
            {
                Modules = [.. Contracts.Config.ModuleCatalog.All.Select(m => new Contracts.Config.ModuleState
                {
                    Id = m.Id,
                    On = Settings.ModuleOn(m.Id),
                    Running = ModuleRunning(m.Id),
                    Consented = Consents.HasModule(m.Id),
                })],
            },
            ContractsJsonContext.Default.ModulesReport));
    }

    /// <summary>U4 Settings → Plugins: every plugin folder, its state and its consent facts (U1 D-H7). Loop only.</summary>
    public Contracts.Plugins.PluginsReport PluginsReport() => new()
    {
        Plugins = [.. Plugins.Statuses().Select(st =>
        {
            var c = Plugins.ConsentView(st.Id);
            return new Contracts.Plugins.PluginInfo
            {
                Id = st.Id,
                Name = c?.Name ?? st.Id,
                Author = c?.Author,
                State = st.State,
                Error = st.Error,
                Reads = c?.Reads ?? [],
                Network = c?.Network ?? [],
                Actions = c?.Actions ?? [],
                CommandLine = c?.CommandLine,
                Pack = c?.Pack,
            };
        })],
        Packs = [.. Plugins.Packs.Where(p => p.Problem is null).OrderBy(p => p.Id, StringComparer.Ordinal).Select(p => new Contracts.Plugins.PackInfo { Id = p.Id, Name = p.Manifest.Name, Author = p.Manifest.Author })],
    };

    private void RegisterPluginHandlers()
    {
        Pipe.RegisterPull("plugins", _ => JsonSerializer.SerializeToNode(PluginsReport(), ContractsJsonContext.Default.PluginsReport));
        PreparedAction Plugin(ActionRequest req, Func<string, PreparedAction> make)
        {
            var id = req.Target?.GetValueOrDefault("plugin");
            return id is null || Plugins.Find(id) is null
                ? new PreparedAction { Summary = "plugin", Refusal = "No such plugin" }
                : make(id);
        }

        PreparedAction Now(string summary, Func<bool> act) => new()
        {
            Summary = summary,
            Immediate = new ActionResult { Action = "plugin", Status = ActionStatus.Ok, Message = summary },
            Done = _ => act(),
        };

        Router.Register("plugin.enable", req => Plugin(req, id => Now("Turned on " + id, () => Plugins.Enable(id, Loop.Clock.UtcNow))));
        Router.Register("plugin.enable_pack", req =>
        {
            // D-H7: one consent for every plugin in the pack (the dialog listed each one's facts).
            var id = req.Target?.GetValueOrDefault("pack");
            if (Plugins.Packs.FirstOrDefault(p => p.Id == id && p.Problem is null) is not { } pack)
            {
                return new PreparedAction { Summary = "plugin pack", Refusal = "No such pack" };
            }

            return Now("Turned on " + pack.Manifest.Name, () => Plugins.EnablePack(pack.Id, Loop.Clock.UtcNow) > 0);
        });
        Router.Register("plugin.disable", req => Plugin(req, id => Now("Turned off " + id, () =>
        {
            Plugins.Disable(id);
            return true;
        })));
        Router.Register("plugin.pause", req => Plugin(req, id => Now("Paused " + id, () => Plugins.Pause(id, true, Loop.Clock.UtcNow))));
        Router.Register("plugin.resume", req => Plugin(req, id => Now("Resumed " + id, () => Plugins.Pause(id, false, Loop.Clock.UtcNow))));
        Router.Register("plugin.action", req => Plugin(req, id =>
        {
            var action = req.Args?.GetValueOrDefault("action") ?? string.Empty;
            var decl = Plugins.Find(id)!.Manifest?.Actions?.FirstOrDefault(a => a.Id == action);
            if (decl is null)
            {
                return new PreparedAction { Summary = id, Refusal = "Not one of the plugin's declared actions" };
            }

            // The run itself is off-loop (a child process); the router waits for its result.
            var run = Plugins.Act(id, action, req.Args, Loop.Clock.UtcNow);
            return new PreparedAction
            {
                Summary = decl.Label + " (" + id + ")",
                TargetName = id,
                Run = _ =>
                {
                    var r = run.GetAwaiter().GetResult();
                    return new ActionResult { Action = "plugin.action", Status = r.Ok ? ActionStatus.Ok : ActionStatus.Failed, Message = r.Ok ? decl.Label + ": done" : r.Error };
                },
            };
        }));
    }
}
