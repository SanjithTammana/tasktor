using System.Globalization;
using Tasktor.Contracts.Platform;
using Tasktor.Core.Learning;
using Tasktor.Core.Loop;
using Tasktor.Core.Rules;
using Tasktor.Core.Shapes;

namespace Tasktor.Core.Features.SystemMonitor;

/// <summary>
/// A6 pressure alerts, tiered to stay inside D-A3: (1) watch cheaply (Windows' low-memory event, totals on the tick);
/// (2) escalate: when the event fires or a total crosses its warn level, sample totals and processes every 5 s; (3) the
/// default rules confirm (hold) and alert. The offender is the app group with the most private memory, with its growth
/// over the escalation window; a learned per-app normal growth only changes the wording ("faster than usual"), never
/// whether an alert fires. Escalated sampling stops and buffers are freed within 2 min of the condition clearing.
/// </summary>
public sealed class Pressure : Feature
{
    public static readonly TimeSpan EscalatedInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan EscalationWindow = TimeSpan.FromMinutes(2);

    public static readonly LearnedThreshold Growth = new()
    {
        Policy = "growth",
        Fallback = TimeSpan.FromSeconds(500),
        Min = TimeSpan.FromSeconds(1),
        Max = TimeSpan.FromSeconds(100_000),
        Ring = s => s.Duration,
    };

    private readonly SystemTotals _totals;
    private readonly Apps _apps;
    private readonly IEventSource<bool>? _lowMemory;
    private readonly KeyedHasher? _hasher;
    private readonly Dictionary<string, double> _startBytes = new(StringComparer.Ordinal);
    private DemandLease? _procs;
    private DemandLease? _system;
    private DateTimeOffset _until;
    private DateTimeOffset _startAt;

    public Pressure(FeatureContext context, SystemTotals totals, Apps apps, IEventSource<bool>? lowMemory)
        : base(context)
    {
        _totals = totals;
        _apps = apps;
        _lowMemory = lowMemory;
        _hasher = context.Key?.Hasher("app");
        Learned = new KindStore(context.Key?.Fingerprint() ?? "none");
    }

    public override string Id => "pressure";

    public override FeatureTier Tier => FeatureTier.Core;

    public bool LowMemory { get; private set; }

    public bool Escalated => _procs is not null;

    public KindStore Learned { get; }

    public (string AppId, string Name, double Bytes, double GrowthBytes, TimeSpan Over, bool Faster, string Source)? Offender { get; private set; }

    protected override void OnEnable()
    {
        _totals.Sampled += OnTotals;
        _apps.Sampled += OnApps;
        _lowMemory?.Start(v => Context.Loop.Post(() => OnLowMemory(v)));
        var path = Path.Combine(Context.Store.StateDir, "app-kinds.json");
        if (File.Exists(path))
        {
            Learned.Load(File.ReadAllBytes(path));
        }
    }

    protected override void OnDisable()
    {
        _totals.Sampled -= OnTotals;
        _apps.Sampled -= OnApps;
        _lowMemory?.Stop();
        Release();
    }

    /// <summary>Windows' low-memory notification (a push event, no polling).</summary>
    public void OnLowMemory(bool low)
    {
        LowMemory = low;
        if (low)
        {
            Escalate(Context.Clock.UtcNow);
        }

        Context.RulesDirty();
    }

    private bool OverWarn()
    {
        var r = _totals.Raw;
        var t = Context.Settings;
        return (r.Ram is { } ram && ram >= t.ThresholdFor("ram").Warn)
            || (r.Commit is { } commit && commit >= 90)
            || (r.Cpu is { } cpu && cpu >= t.ThresholdFor("cpu").Warn)
            || (r.DiskActive is { } disk && disk >= t.ThresholdFor("disk_active").Warn);
    }

    private void OnTotals(MetricFrame frame, bool isTick)
    {
        var now = frame.At == default ? Context.Clock.UtcNow : frame.At;
        if (OverWarn() || LowMemory)
        {
            Escalate(now);
        }
        else if (Escalated && now >= _until)
        {
            Release();
        }
    }

    private void Escalate(DateTimeOffset now)
    {
        _until = now + EscalationWindow;
        if (Escalated)
        {
            return;
        }

        _startAt = now;
        _startBytes.Clear();
        _procs = Context.Scheduler.Lease(Apps.SamplerName, EscalatedInterval, ColumnSet.Names | ColumnSet.Usage | ColumnSet.Identity, holder: "pressure");
        _system = Context.Scheduler.Lease(SystemTotals.SamplerName, EscalatedInterval, ColumnSet.Names, holder: "pressure");
        Context.Log.Info("pressure.escalated");
    }

    private void Release()
    {
        if (!Escalated)
        {
            return;
        }

        if (Offender is { } o && o.Over >= TimeSpan.FromSeconds(30) && _hasher is not null)
        {
            // Learn this app's normal growth from the window (numbers only, keyed by a hash of the app id).
            Learned.Get(_hasher.Hash(o.AppId), Context.Clock.UtcNow).RecordRun(new RunOutcome { Good = true, DurationS = PerMinute(o.GrowthBytes, o.Over) });
            Context.Persist(Path.Combine(Context.Store.StateDir, "app-kinds.json"), Learned.Serialize());
        }

        _procs?.Dispose();
        _system?.Dispose();
        _procs = null;
        _system = null;
        _startBytes.Clear();
        Offender = null;
        Context.Log.Info("pressure.released");
    }

    private void OnApps(KeyedTable<ProcKey, ProcessRow> table, ColumnSet columns)
    {
        if (!Escalated || (columns & ColumnSet.Usage) == 0)
        {
            return;
        }

        var now = Context.Clock.UtcNow;
        var first = _startBytes.Count == 0;
        foreach (var g in _apps.Groups)
        {
            if (first || !_startBytes.ContainsKey(g.AppId))
            {
                _startBytes[g.AppId] = g.PrivateBytes;
            }
        }

        var top = _apps.Groups.Where(g => !g.IsSystem).MaxBy(g => g.PrivateBytes);
        if (top is null)
        {
            Offender = null;
            return;
        }

        var growth = top.PrivateBytes - _startBytes.GetValueOrDefault(top.AppId, top.PrivateBytes);
        var over = now - _startAt;
        var perMin = PerMinute(growth, over);
        var stats = _hasher is null ? null : Learned.Find(_hasher.Hash(top.AppId));
        var limit = Growth.Current(stats, Learned.LearningOn);
        Offender = (top.AppId, top.Name, top.PrivateBytes, growth, over, perMin > limit.Value && growth > 0, limit.Source.ToString().ToLowerInvariant());
        Context.RulesDirty();
    }

    private static double PerMinute(double bytes, TimeSpan over) => over.TotalMinutes <= 0 ? 0 : bytes / (1024 * 1024) / Math.Max(over.TotalMinutes, 1.0 / 6);

    public override void Contribute(RuleInputs inputs, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        inputs.State("low_memory", LowMemory);
        if (Offender is { } o)
        {
            inputs.Fields["offender"] = o.Name;
            inputs.Fields["offender_size"] = (o.Bytes / (1024.0 * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
            var mb = o.GrowthBytes / (1024 * 1024);
            inputs.Fields["growth"] = (mb >= 0 ? "+" : "−") + Math.Abs(Math.Round(mb)).ToString(CultureInfo.InvariantCulture) + " MB in " + Math.Max(1, (int)Math.Round(o.Over.TotalMinutes)).ToString(CultureInfo.InvariantCulture) + " min";
            inputs.Fields["faster"] = o.Faster ? " — faster than usual for " + o.Name : string.Empty;
            inputs.Target = new Dictionary<string, string>(StringComparer.Ordinal) { ["app"] = o.AppId };
        }
        else
        {
            inputs.Fields["offender"] = "an app";
            inputs.Fields["offender_size"] = string.Empty;
            inputs.Fields["growth"] = "growing";
            inputs.Fields["faster"] = string.Empty;
        }
    }

    public override IReadOnlyList<Contracts.Cards.Card> Cards() => [];
}
