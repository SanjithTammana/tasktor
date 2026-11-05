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

namespace Tasktor.Agent.Host;

/// <summary>What the composition root needs from its caller: the store, the clock and the OS adapters (fakes in tests).</summary>
public sealed record AgentOptions
{
    public required StoreLayout Store { get; init; }

    public IClock Clock { get; init; } = SystemClock.Instance;

    public string? PipeName { get; init; }

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
        Loop = new LogicLoop(options.Clock, ex => Log.Error("loop.error", LogField.Of("type", ex.GetType().Name)));
        Scheduler = new Scheduler(options.Clock);
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
        Consents = new ConsentStore(Store, (path, bytes) => _writer.Enqueue(path, bytes));
        Consents.Load();
        SelfUse = new SelfUseCounter(Store, (path, bytes) => _writer.Enqueue(path, bytes));
        SelfUse.Load();
        Key = InstallKey.LoadOrCreate(Store, options.KeyProtection);
        Forgetter = new Forgetter(Store);
        Exporter = new DataExporter();
        Archive = new SettingsArchive(Store);
        LoadPause();
        Router.Completed += (req, result) =>
        {
            if (result.Status is ActionStatus.Ok or ActionStatus.Partial)
            {
                SelfUse.Count("action." + req.Action, Loop.Clock.UtcNow);
            }
        };
        _owned.Add(Loop.Every(TimeSpan.FromMinutes(1), SelfUse.Flush));
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
        RegisterExportTables();
    }

    public AgentOptions Options { get; }

    public StoreLayout Store { get; }

    public LogSink Log { get; }

    public LogicLoop Loop { get; }

    public Scheduler Scheduler { get; }

    public StateTracker State { get; }

    public CardTemplates Cards { get; }

    public SnapshotPublisher Publisher { get; }

    public ActionRouter Router { get; }

    public PipeServer Pipe { get; }

    public PausedState Paused => _paused;

    public SettingsStore Settings { get; }

    public ConsentStore Consents { get; }

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
        OffLoop = Options.OffLoopReads ?? (work => Task.Run(work)),
        RegisterExport = Exporter.Register,
    };

    /// <summary>Attach and enable a feature (loop-only after start).</summary>
    public void AddFeature(Feature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
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
        var pack = Plugins.TickedPackRules((packId, ruleId) => Consents.Current.Modules.ContainsKey("pack:" + packId + ":" + ruleId));
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

    /// <summary>Start the loop thread and the pipe; publish the first snapshot (D-H10: start to first snapshot &lt; 1 s).</summary>
    public void Start()
    {
        Loop.Start();
        Loop.Post(() => Plugins.Discover(Loop.Clock.UtcNow));
        Loop.Post(() => ReloadRules());
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
            Done = _ => Settings.Update(c =>
            {
                var list = (c.Flyout?.PinnedCards ?? []).Where(x => x != card).ToList();
                if (pin)
                {
                    list.Add(card);
                }

                return c with { Flyout = (c.Flyout ?? new Contracts.Config.FlyoutSettings()) with { PinnedCards = list.Count == 0 ? null : list } };
            }),
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

        Router.Register("capture.forget", req =>
        {
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
                "Export my data to " + folder + (cloud ? " (a cloud-synced folder: files will leave this PC)" : string.Empty),
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
                return new ActionResult { Action = "uninstall", Status = ActionStatus.Ok, Message = removed.Count == 0 ? "Nothing to remove" : string.Join("; ", removed) };
            },
        });
    }

    /// <summary>Platform clean-ups for <c>tasktor uninstall</c> (portable: Run key, toast identity). Each returns what it removed, or null.</summary>
    public List<Func<string?>> Uninstallers { get; } = [];

    private void ReloadSettings()
    {
        Settings.Load();
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

    private void RegisterPluginHandlers()
    {
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
