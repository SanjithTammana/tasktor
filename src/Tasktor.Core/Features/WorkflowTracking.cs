using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json;
using Tasktor.Contracts;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Workflow;
using Tasktor.Core.Actions;
using Tasktor.Core.Features.Focus;
using Tasktor.Core.Learning;
using Tasktor.Core.Rules;
using Tasktor.Core.Storage;
using Tasktor.Core.Time;
using Tasktor.Core.Workflow;

namespace Tasktor.Core.Features;

/// <summary>
/// C9 Workflow tracking (Module <c>tracking</c>, card <c>core.workflow</c>): window and presence events → privacy gate →
/// episodes → stores; the open activity is assigned online (debounced 5 s, and on the 60 s tick) through the current
/// model and <see cref="CurrentWorkflow"/> (workflow → cluster → app); closed activities are assigned (next app known),
/// stored in <c>episodes.db</c> and feed today's totals, I6 and E11.2. C8 refits run when <see cref="RefitPlanner"/> says
/// so: extract on the store writer, the Python worker off the loop, <see cref="ModelLoader"/> validates and swaps.
/// Disable closes the stores and releases the sources (D-C7 acceptance 1).
/// </summary>
public sealed class WorkflowTracking : Feature
{
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan WorkerTimeout = TimeSpan.FromMinutes(20);
    public static readonly TimeSpan RecentKeep = TimeSpan.FromDays(2);
    public const string RefitTask = "refit";

    private readonly IEventSource<WindowEvent>? _windows;
    private readonly IEventSource<PresenceEvent>? _presence;
    private readonly Func<StoreSet> _openStores;
    private readonly KeyedHasher _hasher;
    private readonly PreregFile _prereg = new();
    private readonly Dictionary<(nint, ProcKey), int> _winIds = [];
    private readonly Dictionary<string, long> _today = new(StringComparer.Ordinal);
    private readonly SortedDictionary<int, Dictionary<string, long>> _daily = [];
    private OverridesFile _overrides = new();
    private readonly List<ClosedActivity> _recent = [];
    private TokenBuckets? _buckets;
    private StoreWriter? _writer;
    private TrackingPipeline? _pipe;
    private CurrentWorkflow? _current;
    private ModelLoader? _loader;
    private IDisposable? _tick;
    private IDisposable? _debounce;
    private CancellationTokenSource? _refitCancel;
    private bool _paused;
    private int _day = int.MinValue;
    private DateTimeOffset? _idleSince;
    private long _total;
    private long _newActivities;
    private int _failures;

    public WorkflowTracking(FeatureContext context, IEventSource<WindowEvent>? windows, IEventSource<PresenceEvent>? presence, Func<StoreSet> openStores, KeyedHasher hasher)
        : base(context)
    {
        _windows = windows;
        _presence = presence;
        _openStores = openStores;
        _hasher = hasher;
    }

    /// <summary>A closed activity as the totals and E11.2 see it: times, its label (cluster id or <c>app:&lt;exe&gt;</c>), confidence.</summary>
    public sealed record ClosedActivity(DateTimeOffset Start, DateTimeOffset End, long ActiveMs, long IdleMs, string Label, double Confidence);

    public override string Id => "tracking";

    public override FeatureTier Tier => FeatureTier.Module;

    /// <summary>The model in use (loaded at enable, replaced by a refit; null = app fallback only).</summary>
    public WorkflowModel? Model { get; set; }

    public RefitPlanner Planner { get; } = new();

    public ClusterFeedback Feedback { get; } = new();

    /// <summary>The host's persisted "pause tracking" flag (C3.1).</summary>
    public Func<bool> PausedNow { get; set; } = () => false;

    /// <summary>C8 refit conditions from elsewhere: on AC (A8) …</summary>
    public Func<bool> OnAc { get; set; } = () => true;

    /// <summary>… and not in a call or event mode (I3, I13).</summary>
    public Func<bool> Busy { get; set; } = () => false;

    /// <summary>The user's own names for redaction (Windows user name and display name).</summary>
    public IReadOnlyList<string> OwnNames { get; init; } = [];

    /// <summary>Tests: write on the loop thread instead of the store writer thread.</summary>
    public bool InlineStores { get; init; }

    /// <summary>B5's internal task rows (the refit shows there).</summary>
    public InternalTasks? Tasks { get; set; }

    /// <summary>The thresholds file the worker reads; its hash must match every model (D-C6).</summary>
    public string PreregPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "worker", "prereg.json");

    /// <summary>The bundled interpreter and the worker package folder (D-C11).</summary>
    public string PythonExe { get; set; } = Path.Combine(AppContext.BaseDirectory, "worker", "python", OperatingSystem.IsWindows() ? "python.exe" : "bin/python3");

    public string WorkerDir { get; set; } = Path.Combine(AppContext.BaseDirectory, "worker");

    /// <summary>Runs one refit job and returns its exit code; the default starts the worker through <c>IProcessControl</c>. Off the loop.</summary>
    public Func<WorkerJob, CancellationToken, Task<int>>? RunWorker { get; set; }

    public TrackingPipeline? Pipeline => _pipe;

    public IReadOnlyDictionary<string, long> TodayMs => _today;

    public IReadOnlyList<ClosedActivity> Recent => _recent;

    public CurrentWorkflow? Current => _current;

    public long Refits { get; private set; }

    /// <summary>Raised for each closed activity after assignment: the record, its label, the shown name, the confidence.</summary>
    public event Action<ActivityRecord, string, string, double>? ActivityAssigned;

    private string PlannerPath => Path.Combine(Context.Store.ModelsDir, "planner.json");

    protected override void OnEnable()
    {
        var stores = _openStores();
        _total = stores.Episodes.Activities;
        _writer = InlineStores ? StoreWriter.Inline(stores) : StoreWriter.Threaded(stores);
        _buckets = new TokenBuckets(_hasher, _prereg.Features.Buckets);
        var s = Context.Settings.Current.Tracking;
        _pipe = new TrackingPipeline(new PrivacyGate(new Redactor(OwnNames), s?.ExcludeApps, s?.ExcludeTitles), _buckets, _writer, _prereg);
        _pipe.ActivityClosed += OnClosed;
        _current = new CurrentWorkflow(_prereg.Assignment);
        var preregHash = ModelLoader.HashOf(File.Exists(PreregPath) ? File.ReadAllBytes(PreregPath) : JsonSerializer.SerializeToUtf8Bytes(_prereg, ContractsJsonContext.Default.PreregFile));
        _loader = new ModelLoader(Context.Store.ModelsDir, preregHash);
        Model ??= _loader.LoadCurrent();
        var planner = RefitPlannerStore.Load(PlannerPath);
        Planner.Restore(planner.LastFit, planner.IntervalDays);
        _failures = planner.FailuresInARow;
        _windows?.Start(e => Context.Loop.Post(() => OnWindow(e)));
        _presence?.Start(e => Context.Loop.Post(() => OnPresence(e)));
        _tick = Context.Loop.Every(TimeSpan.FromSeconds(60), OnTick);
        Raw(CaptureKind.AgentStart, Context.Clock.UtcNow, null, null, null);
        Context.Router.Register("workflow.wrong", Wrong);
        Context.Router.Register("workflow.rename", Rename);
        Context.Router.Register("workflow.merge", Merge);
        Context.Router.Register("summary", req => new PreparedAction { Summary = "Summary", Immediate = new ActionResult { Action = "summary", Status = ActionStatus.Ok, Message = SummaryNode(req.Args?.GetValueOrDefault("since")).ToJsonString() } });
        Context.RegisterPull("summary", pull => SummaryNode(pull.Args?.GetValueOrDefault("since")));
        _overrides = ReadOverrides();
        LoadHistory(stores.Base);
    }

    protected override void OnDisable()
    {
        _windows?.Stop();
        _presence?.Stop();
        _tick?.Dispose();
        _debounce?.Dispose();
        _refitCancel?.Cancel();
        var now = Context.Clock.UtcNow;
        Raw(CaptureKind.AgentStop, now, null, null, null);
        _pipe?.Flush(Mono(now));
        _writer?.Dispose(); // drains and closes both stores
        _writer = null;
        _pipe = null;
        Tasks?.Remove(RefitTask);
        Context.UpdateState(st => st with { Workflow = null });
    }

    private static long Mono(DateTimeOffset at) => at.ToUnixTimeMilliseconds();

    private void Raw(CaptureKind kind, DateTimeOffset at, string? app, int? win, string? title, IdleKind? idle = null)
    {
        _pipe?.On(new CaptureEvent
        {
            Kind = kind,
            TsUtcMs = at.ToUnixTimeMilliseconds(),
            TzMin = (int)TimeZoneInfo.Local.GetUtcOffset(at).TotalMinutes,
            MonoMs = Mono(at),
            App = app,
            Win = win,
            Title = title,
            Idle = idle,
        });
    }

    private void SyncPause(DateTimeOffset now)
    {
        var p = PausedNow();
        if (p == _paused || _pipe is null)
        {
            return;
        }

        _paused = p;
        if (p)
        {
            Raw(CaptureKind.Pause, now, null, null, null);
            _pipe.Gate.Paused = true;
        }
        else
        {
            _pipe.Gate.Paused = false;
            Raw(CaptureKind.Unpause, now, null, null, null);
        }
    }

    public void OnWindow(WindowEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        SyncPause(e.AtUtc);
        if (e.Exe is not { Length: > 0 } exe)
        {
            return;
        }

        var key = (e.Hwnd, e.Proc);
        if (!_winIds.TryGetValue(key, out var win))
        {
            _winIds[key] = win = _winIds.Count + 1;
        }

        var kind = e.Kind switch
        {
            WindowEventKind.Foreground => CaptureKind.Foreground,
            WindowEventKind.TitleChanged => CaptureKind.Title,
            WindowEventKind.MinimizeStart => CaptureKind.Minimize,
            _ => CaptureKind.Restore,
        };
        Raw(kind, e.AtUtc, exe.ToLowerInvariant(), win, e.Title?.Reveal());
        _debounce?.Dispose();
        _debounce = Context.Loop.At(Context.Clock.UtcNow + Debounce, AssignOpen);
    }

    public void OnPresence(PresenceEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var kind = e.Kind switch
        {
            PresenceKind.IdleStart => CaptureKind.IdleStart,
            PresenceKind.IdleEnd => CaptureKind.IdleEnd,
            PresenceKind.Lock => CaptureKind.Lock,
            PresenceKind.Unlock => CaptureKind.Unlock,
            PresenceKind.Suspend => CaptureKind.Suspend,
            _ => CaptureKind.Resume,
        };
        _idleSince = e.Kind switch
        {
            PresenceKind.IdleStart or PresenceKind.Lock => _idleSince ?? e.AtUtc,
            PresenceKind.IdleEnd or PresenceKind.Unlock or PresenceKind.Resume => null,
            _ => _idleSince,
        };
        Raw(kind, e.AtUtc, null, null, null, e.Idle);
    }

    private void OnTick()
    {
        var now = Context.Clock.UtcNow;
        SyncPause(now);
        AssignOpen();
        var idleFor = _idleSince is { } since ? now - since : TimeSpan.Zero;
        if (!_paused && Planner.Due(now, idleFor, OnAc(), Busy(), _newActivities, (int)Math.Min(int.MaxValue, _total)))
        {
            StartRefit();
        }
    }

    private ActivityFeatures Features(IReadOnlyList<KeyValuePair<string, long>> tokens, float[] dense, string app, string? prev, string? next)
    {
        var buckets = tokens.Select(t => (Bucket: _buckets!.Of(t.Key), Ms: (double)t.Value)).GroupBy(x => x.Bucket).Select(g => (g.Key, g.Sum(x => x.Ms))).OrderBy(x => x.Key).ToList();
        return new ActivityFeatures([.. buckets.Select(b => b.Key)], [.. buckets.Select(b => b.Item2)], [.. dense.Select(f => (double)f)], app, prev, next);
    }

    private static string Display(string app)
    {
        var name = app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app[..^4] : app;
        return name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : name;
    }

    /// <summary>Assign the open activity and update the shown workflow (C9).</summary>
    public void AssignOpen()
    {
        _pipe?.Tick(Mono(Context.Clock.UtcNow)); // accrue to now: an activity opens once time passes in the foreground
        if (_pipe?.Builder.Open is not { } open || _current is null)
        {
            return;
        }

        LevelResult? cluster = null;
        if (Model is { } m)
        {
            var now = Context.Clock.UtcNow;
            var dense = TrainingExtract.Dense(now.ToUnixTimeMilliseconds() - open.ActiveMs, (int)TimeZoneInfo.Local.GetUtcOffset(now).TotalMinutes, open.ActiveMs, 0, 0, 0, 0);
            var (id, conf) = m.Assign(m.Project(m.Vector(Features([.. open.Tokens], dense, open.App, null, null))));
            id = MergedInto(id);
            cluster = new LevelResult(id, NameOf(id), conf);
        }

        if (_current.Update(null, cluster, open.App, Display(open.App)))
        {
            var shown = _current.Shown;
            Context.UpdateState(s => s with { Workflow = shown });
        }
    }

    private void OnClosed(ActivityRecord a)
    {
        string? cluster = null;
        double conf = 0;
        var level = "app";
        if (Model is { } m)
        {
            var dense = TrainingExtract.Dense(a.StartUtcMs, a.TzMin, a.ActiveMs, a.IdleMs, a.Glances, a.TitleChanges, a.JobFrac);
            (cluster, conf) = m.Assign(m.Project(m.Vector(Features(a.Tokens, dense, a.App, a.PrevApp, a.NextApp))));
            if (cluster is not null)
            {
                level = "activity";
            }
        }

        cluster = MergedInto(cluster);
        var shown = cluster is not null && conf >= _prereg.Assignment.Floor && _current?.IsSuppressed(cluster) != true;
        var label = shown ? cluster! : "app:" + a.App;
        var name = shown ? NameOf(cluster)! : Display(a.App);
        if (shown)
        {
            Feedback.Shown(cluster!);
        }

        var version = Model?.Version;
        _writer?.Enqueue(s => s.Episodes.Assign(s.Base + a.Index, version, cluster, conf, level));

        var day = Days.Of(a.StartUtcMs, a.TzMin);
        if (day != _day)
        {
            _today.Clear();
            _day = day;
        }

        _today[name] = _today.GetValueOrDefault(name) + a.ActiveMs;
        AddDaily(day, name, a.ActiveMs);
        var start = DateTimeOffset.FromUnixTimeMilliseconds(a.StartUtcMs);
        _recent.Add(new ClosedActivity(start, start + TimeSpan.FromMilliseconds(a.EndMs - a.StartMs), a.ActiveMs, a.IdleMs, label, shown ? conf : 0));
        _recent.RemoveAll(r => r.End < Context.Clock.UtcNow - RecentKeep);
        _total++;
        _newActivities++;
        ActivityAssigned?.Invoke(a, label, name, shown ? conf : 0);
    }

    /// <summary>
    /// E11.2: C's numbers for a focus block from the closed activities inside it (overlap-weighted): the share in the
    /// intended label (cluster id or <c>app:&lt;exe&gt;</c>; the block's most common label when none was picked), label
    /// switches, idle share and mean confidence. Null when nothing closed inside the block.
    /// </summary>
    public BlockMeasures? Measure(DateTimeOffset from, DateTimeOffset to, string? intended)
    {
        var parts = _recent.Where(r => r.End > from && r.Start < to).OrderBy(r => r.Start)
            .Select(r =>
            {
                var span = (r.End - r.Start).TotalMilliseconds;
                var overlap = (Min(r.End, to) - Max(r.Start, from)).TotalMilliseconds;
                var frac = span > 0 ? Math.Clamp(overlap / span, 0, 1) : 1;
                return (r.Label, Active: r.ActiveMs * frac, Idle: r.IdleMs * frac, r.Confidence);
            })
            .Where(p => p.Active > 0)
            .ToList();
        if (parts.Count == 0)
        {
            return null;
        }

        var total = parts.Sum(p => p.Active);
        var target = intended ?? parts.GroupBy(p => p.Label).MaxBy(g => g.Sum(p => p.Active))!.Key;
        var onIntent = parts.Where(p => p.Label == target).Sum(p => p.Active) / total;
        var switches = parts.Zip(parts.Skip(1)).Count(x => x.First.Label != x.Second.Label);
        var idle = parts.Sum(p => p.Idle);
        return new BlockMeasures(Math.Round(onIntent, 6), switches, Math.Round(idle / (total + idle), 6), null, Math.Round(parts.Average(p => p.Confidence), 6));
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    /// <summary>C8 refit now (the planner said so, or <c>tasktor refit</c>): extract → worker → validate → swap.</summary>
    public bool StartRefit()
    {
        if (Planner.Running || _writer is null || _loader is null)
        {
            return false;
        }

        Planner.Running = true;
        var now = Context.Clock.UtcNow;
        var version = "v" + now.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var extract = Path.Combine(Context.Store.ExtractDir, version);
        var staging = Path.Combine(Context.Store.ModelsDir, "staging", version);
        var overrides = Path.Combine(Context.Store.ModelsDir, "overrides.json");
        var job = new WorkerJob
        {
            Extract = extract,
            PreviousModel = Model?.Directory,
            Overrides = File.Exists(overrides) ? overrides : null,
            Prereg = PreregPath,
            Output = staging,
        };
        _refitCancel = new CancellationTokenSource();
        var token = _refitCancel.Token;
        var c = _prereg.Clustering;
        Tasks?.Set(new ProgressRow { Id = RefitTask, Label = "Workflow refit", Stage = "extract", State = Contracts.Jobs.ProgressState.Running, UpdatedUtc = now }, () => _refitCancel?.Cancel());
        Context.Log.Info("workflow.refit_start", Logging.LogField.Of("new", _newActivities));
        _writer.Enqueue(s =>
        {
            int rows;
            try
            {
                rows = TrainingExtract.Write(s.Episodes, extract, version, now, c.FitDays, c.FitCap).Count;
            }
            catch (Exception e) when (e is IOException or SqliteException or UnauthorizedAccessException)
            {
                rows = -1;
            }

            Context.Loop.Post(() => Launch(job, rows, token));
        });
        return true;
    }

    private void Launch(WorkerJob job, int rows, CancellationToken token)
    {
        if (rows < 0 || token.IsCancellationRequested)
        {
            Finish(job, -1);
            return;
        }

        Tasks?.Set(new ProgressRow { Id = RefitTask, Label = "Workflow refit", Stage = "fits", Msg = rows + " activities", State = Contracts.Jobs.ProgressState.Running, UpdatedUtc = Context.Clock.UtcNow }, () => _refitCancel?.Cancel());
        var run = RunWorker ?? DefaultRunWorker;
        Context.OffLoop(() =>
        {
            int code;
            try
            {
                code = run(job, token).GetAwaiter().GetResult();
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                code = -1;
            }

            Context.Loop.Post(() => Finish(job, code));
        });
    }

    private async Task<int> DefaultRunWorker(WorkerJob job, CancellationToken token)
    {
        var pc = Context.Processes ?? throw new InvalidOperationException("no process control");
        var handle = pc.StartWorker(new WorkerSpec
        {
            Kind = WorkerKind.Learning,
            Exe = PythonExe,
            Args = ["-I", "-m", "tasktor_worker"],
            WorkingDir = WorkerDir,
            Stdin = JsonSerializer.Serialize(job, ContractsJsonContext.Default.WorkerJob),
            MemoryMb = 1536,
            TimeoutMs = (int)WorkerTimeout.TotalMilliseconds,
            ClearEnv = ["PYTHONPATH", "PYTHONHOME", "PYTHONSTARTUP"],
            Eco = true,
        });
        using var reg = token.Register(() => pc.CancelWorker(handle));
        var exit = await pc.WaitWorker(handle, token).ConfigureAwait(false);
        return exit.TimedOut ? -1 : exit.ExitCode ?? -1;
    }

    private void Finish(WorkerJob job, int code)
    {
        var now = Context.Clock.UtcNow;
        var reason = code == 0 ? _loader?.Validate(job.Output) : "worker exit " + code;
        if (reason is null && _loader is not null && Enabled)
        {
            var previous = Model;
            Model = _loader.Swap(job.Output);
            Feedback.Reset();
            _current?.ClearSuppressed();
            var (g5, newCluster) = Persistence(Model, previous);
            Planner.OnFitResult(now, g5, newCluster);
            _newActivities = 0;
            _failures = 0;
            Refits++;
            Context.Log.Info("workflow.refit_done", Logging.LogField.Of("version", Model.Version), Logging.LogField.Of("clusters", Model.Manifest.Clusters.Count));
            AssignOpen();
        }
        else
        {
            // A failed or refused fit counts as today's attempt (no retry storm); the current model stays.
            _failures++;
            Planner.OnFitResult(now, null, newCluster: false);
            Planner.Restore(now, Planner.Interval.TotalDays);
            Context.Log.Warn("workflow.refit_failed", Logging.LogField.Of("reason", reason ?? "disabled"), Logging.LogField.Of("in_a_row", _failures));
            TryDelete(job.Output);
        }

        TryDelete(job.Extract);
        Context.Persist(PlannerPath, RefitPlannerStore.Bytes(new RefitPlannerFile { LastFit = Planner.LastFit, IntervalDays = Planner.Interval.TotalDays, FailuresInARow = _failures }));
        Tasks?.Remove(RefitTask);
    }

    /// <summary>G5 from the new model's eval.json, and whether a cluster id is new against the previous model.</summary>
    private static (double? G5, bool NewCluster) Persistence(WorkflowModel model, WorkflowModel? previous)
    {
        double? g5 = null;
        try
        {
            var eval = JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(model.Directory, "eval.json")), ContractsJsonContext.Default.EvalReport);
            g5 = eval?.Gates.FirstOrDefault(g => g.Id == "G5")?.Value;
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            g5 = null;
        }

        var old = previous?.Manifest.Clusters.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var fresh = old is not null && model.Manifest.Clusters.Any(c => !old.Contains(c.Id));
        return (g5, fresh);
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // left for the next start's cleanup
        }
    }

    private PreparedAction Wrong(ActionRequest req)
    {
        var id = _current?.Shown?.Id;
        if (id is null || id.StartsWith("app:", StringComparison.Ordinal))
        {
            return new PreparedAction { Summary = "That's wrong", Refusal = "Nothing learned is shown now" };
        }

        var hide = Feedback.Wrong(id);
        return new PreparedAction
        {
            Summary = "That's wrong",
            Immediate = new ActionResult
            {
                Action = "workflow.wrong",
                Status = ActionStatus.Ok,
                Message = hide ? "Hidden until the next refit: shown by app meanwhile" : "Noted",
            },
            Done = _ =>
            {
                if (hide)
                {
                    _current!.Suppress(id);
                    Context.Log.Info("workflow.cluster_hidden", Logging.LogField.Of("wrong_share", Math.Round(Feedback.WrongShare(id), 2)));
                }

                AssignOpen();
            },
        };
    }

    // ---------------- C7 names and merges, C9 totals and summary ----------------

    private string OverridesPath => Path.Combine(Context.Store.ModelsDir, "overrides.json");

    private OverridesFile ReadOverrides()
    {
        try
        {
            return File.Exists(OverridesPath) ? JsonSerializer.Deserialize(File.ReadAllBytes(OverridesPath), ContractsJsonContext.Default.OverridesFile) ?? new() : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    /// <summary>A merged cluster shows as the one it was merged into until the next fit makes the union one id (C7).</summary>
    private string? MergedInto(string? id)
    {
        for (var i = 0; id is not null && i < 8 && _overrides.Merge.TryGetValue(id, out var to); i++)
        {
            id = to;
        }

        return id;
    }

    /// <summary>The user's name for a cluster or workflow wins over the model's auto name.</summary>
    public string? NameOf(string? id) => id is null ? null : _overrides.Names.GetValueOrDefault(id) ?? Model?.Cluster(id)?.Name ?? Model?.Workflow(id)?.Name ?? id;

    private void SaveOverrides(OverridesFile o)
    {
        _overrides = o;
        Context.Persist(OverridesPath, JsonSerializer.SerializeToUtf8Bytes(o, ContractsJsonContext.Default.OverridesFile));
    }

    private bool Known(string id) => Model?.Cluster(id) is not null || Model?.Workflow(id) is not null;

    private PreparedAction Rename(ActionRequest req)
    {
        var id = req.Target?.GetValueOrDefault("workflow");
        var name = req.Args?.GetValueOrDefault("name")?.Trim();
        if (id is null || !Known(id))
        {
            return new PreparedAction { Summary = "Rename", Refusal = "No such workflow in the current model" };
        }

        if (name is not { Length: > 0 and <= 80 })
        {
            return new PreparedAction { Summary = "Rename", Refusal = "A name has 1–80 characters" };
        }

        return new PreparedAction
        {
            Summary = "Rename",
            Immediate = new ActionResult { Action = "workflow.rename", Status = ActionStatus.Ok, Message = "Renamed; kept across refits" },
            Done = _ =>
            {
                SaveOverrides(_overrides with { Names = new Dictionary<string, string>(_overrides.Names, StringComparer.Ordinal) { [id] = name } });
                AssignOpen();
            },
        };
    }

    private PreparedAction Merge(ActionRequest req)
    {
        var from = req.Target?.GetValueOrDefault("workflow");
        var into = req.Args?.GetValueOrDefault("into");
        if (from is null || into is null || from == into || !Known(from) || !Known(into))
        {
            return new PreparedAction { Summary = "Merge", Refusal = "Merge needs two different workflows of the current model" };
        }

        if (MergedInto(into) == from)
        {
            return new PreparedAction { Summary = "Merge", Refusal = "That would merge a workflow into itself" };
        }

        return new PreparedAction
        {
            Summary = "Merge",
            Immediate = new ActionResult { Action = "workflow.merge", Status = ActionStatus.Ok, Message = "Merged; the next refit fits them as one" },
            Done = _ =>
            {
                SaveOverrides(_overrides with { Merge = new Dictionary<string, string>(_overrides.Merge, StringComparer.Ordinal) { [from] = into } });
                AssignOpen();
            },
        };
    }

    private void AddDaily(int day, string name, long ms)
    {
        if (!_daily.TryGetValue(day, out var d))
        {
            _daily[day] = d = new Dictionary<string, long>(StringComparer.Ordinal);
        }

        d[name] = d.GetValueOrDefault(name) + ms;
        while (_daily.Count > 31)
        {
            _daily.Remove(_daily.Keys.First());
        }
    }

    /// <summary>Earlier runs' totals (30 days) from episodes.db, read off the loop on its own read-only connection.</summary>
    private void LoadHistory(long runBase)
    {
        var path = Context.Store.EpisodesDb;
        var model = Model;
        var from = (Context.Clock.UtcNow - TimeSpan.FromDays(30)).ToUnixTimeMilliseconds();
        Context.OffLoop(() =>
        {
            var rows = new List<(int Day, string App, string? Cluster, double Conf, long Ms)>();
            try
            {
                using var db = SqliteDb.Open(path, readOnly: true);
                using var q = db.Prepare("SELECT a.day, a.app, s.cluster, COALESCE(s.confidence, 0), a.active_ms FROM activity a LEFT JOIN assignment s ON s.activity = a.id WHERE a.start_utc_ms >= ? AND a.id < ?");
                q.BindAll(from, runBase);
                while (q.Step())
                {
                    rows.Add(((int)q.Int64(0), q.Text(1)!, q.IsNull(2) ? null : q.Text(2), q.Double(3), q.Int64(4)));
                }
            }
            catch (SqliteException)
            {
                return; // a new install: no history yet
            }

            Context.Loop.Post(() =>
            {
                foreach (var r in rows)
                {
                    var cluster = MergedInto(r.Cluster);
                    var shown = cluster is not null && r.Conf >= _prereg.Assignment.Floor && model?.Cluster(cluster) is not null;
                    AddDaily(r.Day, shown ? NameOf(cluster)! : Display(r.App), r.Ms);
                }
            });
        });
    }

    /// <summary>
    /// <c>tasktor summary</c> (numbers only, U1 D5): active minutes per workflow (or app) over the last days, from the
    /// in-memory daily totals. No titles; names are the cluster names the card shows.
    /// </summary>
    public JsonObject SummaryNode(string? since)
    {
        var span = DurationText.TryParse(since ?? "7d", out var s) && s > TimeSpan.Zero ? s : TimeSpan.FromDays(7);
        var now = Context.Clock.UtcNow;
        var today = Days.Of(now.ToUnixTimeMilliseconds(), (int)TimeZoneInfo.Local.GetUtcOffset(now).TotalMinutes);
        var firstDay = today - Math.Max(0, (int)Math.Ceiling(span.TotalDays) - 1);
        var by = new Dictionary<string, long>(StringComparer.Ordinal);
        var days = 0;
        foreach (var (day, d) in _daily.Where(kv => kv.Key >= firstDay && kv.Key <= today))
        {
            days++;
            foreach (var (name, ms) in d)
            {
                by[name] = by.GetValueOrDefault(name) + ms;
            }
        }

        var workflows = new JsonObject();
        foreach (var (name, ms) in by.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            workflows[name] = Math.Round(ms / 60_000.0, 1);
        }

        return new JsonObject
        {
            ["since"] = DurationText.Humanize(span),
            ["active_days"] = days,
            ["active_min"] = Math.Round(by.Values.Sum() / 60_000.0, 1),
            ["switches_today"] = _current?.Shown?.SwitchesToday ?? 0,
            ["model"] = Model?.Version ?? "none (shown by app)",
            ["by_workflow"] = workflows,
        };
    }

    public override void Contribute(RuleInputs inputs, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        inputs.State("tracking_paused", _paused);
    }

    public override IReadOnlyList<Card> Cards()
    {
        if (!Enabled)
        {
            return [];
        }

        var top = _today.OrderByDescending(kv => kv.Value).Take(3).Select(kv => kv.Key + " · " + ((int)(kv.Value / 60_000)).ToString(CultureInfo.InvariantCulture) + " min").ToList();
        var blocks = new List<Block>
        {
            _paused
                ? new BadgeBlock { Text = Value.Literal("Tracking paused"), Priority = 1 }
                : new MetricBlock { Label = "Now", Value = Value.Binding("/state/workflow/name"), Priority = 1 },
            new MetricBlock { Label = "Switches today", Value = Value.Binding("/state/workflow/switches_today"), Priority = 3 },
        };
        if (top.Count > 0)
        {
            blocks.Insert(1, new ListBlock { Label = "Today", Items = Value.FromRawJson(JsonSerializer.Serialize<IReadOnlyList<string>>(top, ContractsJsonContext.Default.IReadOnlyListString)), Max = 3, Priority = 2 });
        }

        if (Model is null)
        {
            blocks.Add(new BadgeBlock { Text = Value.Literal("Learning your workflows: shown by app until there is enough data"), Priority = 4 });
        }

        return
        [
            new Card
            {
                Id = "core.workflow",
                Source = CardKit.Core,
                Title = "Workflow",
                Icon = IconKey.Workflow,
                Blocks = blocks,
                Actions =
                [
                    new CardAction { Id = "pause", Label = _paused ? "Resume tracking" : "Pause tracking", Action = _paused ? "tracking.resume" : "tracking.pause", Priority = 1 },
                    new CardAction { Id = "wrong", Label = "That's wrong", Action = "workflow.wrong", Priority = 2 },
                ],
            },
        ];
    }
}
