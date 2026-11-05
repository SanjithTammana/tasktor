using System.Globalization;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Workflow;
using Tasktor.Core.Actions;
using Tasktor.Core.Learning;
using Tasktor.Core.Rules;
using Tasktor.Core.Workflow;

namespace Tasktor.Core.Features;

/// <summary>
/// C9 Workflow tracking (Module <c>tracking</c>, card <c>core.workflow</c>): window and presence events → privacy gate →
/// episodes → stores; the open activity is assigned online (debounced 5 s, and on the 60 s tick) through the current
/// model and <see cref="CurrentWorkflow"/> (workflow → cluster → app); closed activities feed today's totals. Disable
/// closes the stores and releases the sources (D-C7 acceptance 1).
/// </summary>
public sealed class WorkflowTracking : Feature
{
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(5);

    private readonly IEventSource<WindowEvent>? _windows;
    private readonly IEventSource<PresenceEvent>? _presence;
    private readonly Func<StoreSet> _openStores;
    private readonly KeyedHasher _hasher;
    private readonly PreregFile _prereg = new();
    private readonly Dictionary<(nint, ProcKey), int> _winIds = [];
    private readonly Dictionary<string, long> _today = new(StringComparer.Ordinal);
    private StoreWriter? _writer;
    private TrackingPipeline? _pipe;
    private CurrentWorkflow? _current;
    private IDisposable? _tick;
    private IDisposable? _debounce;
    private bool _paused;
    private int _day = int.MinValue;

    public WorkflowTracking(FeatureContext context, IEventSource<WindowEvent>? windows, IEventSource<PresenceEvent>? presence, Func<StoreSet> openStores, KeyedHasher hasher)
        : base(context)
    {
        _windows = windows;
        _presence = presence;
        _openStores = openStores;
        _hasher = hasher;
    }

    public override string Id => "tracking";

    public override FeatureTier Tier => FeatureTier.Module;

    /// <summary>The model in use (set by the loader after a swap; null = app fallback only).</summary>
    public WorkflowModel? Model { get; set; }

    /// <summary>The host's persisted "pause tracking" flag (C3.1).</summary>
    public Func<bool> PausedNow { get; set; } = () => false;

    /// <summary>The user's own names for redaction (Windows user name and display name).</summary>
    public IReadOnlyList<string> OwnNames { get; init; } = [];

    public TrackingPipeline? Pipeline => _pipe;

    public IReadOnlyDictionary<string, long> TodayMs => _today;

    protected override void OnEnable()
    {
        _writer = StoreWriter.Threaded(_openStores());
        var s = Context.Settings.Current.Tracking;
        _pipe = new TrackingPipeline(new PrivacyGate(new Redactor(OwnNames), s?.ExcludeApps, s?.ExcludeTitles), new TokenBuckets(_hasher), _writer, _prereg);
        _pipe.ActivityClosed += OnClosed;
        _current = new CurrentWorkflow(_prereg.Assignment);
        _windows?.Start(e => Context.Loop.Post(() => OnWindow(e)));
        _presence?.Start(e => Context.Loop.Post(() => OnPresence(e)));
        _tick = Context.Loop.Every(TimeSpan.FromSeconds(60), OnTick);
        Raw(CaptureKind.AgentStart, Context.Clock.UtcNow, null, null, null);
        Context.Router.Register("workflow.wrong", Wrong);
    }

    protected override void OnDisable()
    {
        _windows?.Stop();
        _presence?.Stop();
        _tick?.Dispose();
        _debounce?.Dispose();
        var now = Context.Clock.UtcNow;
        Raw(CaptureKind.AgentStop, now, null, null, null);
        _pipe?.Flush(Mono(now));
        _writer?.Dispose(); // drains and closes both stores
        _writer = null;
        _pipe = null;
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
        Raw(kind, e.AtUtc, null, null, null, e.Idle);
    }

    private void OnTick()
    {
        var now = Context.Clock.UtcNow;
        SyncPause(now);
        AssignOpen();
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
            var buckets = open.Tokens.Select(t => (_hasherBucket(t.Key), (double)t.Value)).GroupBy(x => x.Item1).Select(g => (g.Key, g.Sum(x => x.Item2))).OrderBy(x => x.Key).ToList();
            var now = Context.Clock.UtcNow;
            var dense = TrainingExtract.Dense(now.ToUnixTimeMilliseconds() - open.ActiveMs, (int)TimeZoneInfo.Local.GetUtcOffset(now).TotalMinutes, open.ActiveMs, 0, 0, 0, 0).Select(f => (double)f).ToList();
            var (id, conf) = m.Assign(m.Project(m.Vector(new ActivityFeatures([.. buckets.Select(b => b.Key)], [.. buckets.Select(b => b.Item2)], dense, open.App, null, null))));
            cluster = new LevelResult(id, m.Cluster(id)?.Name, conf);
        }

        var name = open.App.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? open.App[..^4] : open.App;
        if (_current.Update(null, cluster, open.App, name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : name))
        {
            var shown = _current.Shown;
            Context.UpdateState(s => s with { Workflow = shown });
        }
    }

    private int _hasherBucket(string token) => new TokenBuckets(_hasher).Of(token);

    private void OnClosed(ActivityRecord a)
    {
        var day = Days.Of(a.StartUtcMs, a.TzMin);
        if (day != _day)
        {
            _today.Clear();
            _day = day;
        }

        var key = _current?.Shown?.Name ?? a.App;
        _today[key] = _today.GetValueOrDefault(key) + a.ActiveMs;
    }

    private PreparedAction Wrong(ActionRequest req)
    {
        var id = _current?.Shown?.Id;
        if (id is null || id.StartsWith("app:", StringComparison.Ordinal))
        {
            return new PreparedAction { Summary = "That's wrong", Refusal = "Nothing learned is shown now" };
        }

        return new PreparedAction
        {
            Summary = "That's wrong",
            Immediate = new ActionResult { Action = "workflow.wrong", Status = ActionStatus.Ok, Message = "Noted: shown as the app until the next refit" },
            Done = _ =>
            {
                _current!.Suppress(id);
                AssignOpen();
            },
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
            blocks.Insert(1, new ListBlock { Label = "Today", Items = Value.FromRawJson(System.Text.Json.JsonSerializer.Serialize<IReadOnlyList<string>>(top, Contracts.ContractsJsonContext.Default.IReadOnlyListString)), Max = 3, Priority = 2 });
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
