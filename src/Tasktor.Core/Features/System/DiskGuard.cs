using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Snapshot;
using Tasktor.Core.Actions;
using Tasktor.Core.Learning;
using Tasktor.Core.Loop;
using Tasktor.Core.Rules;
using Tasktor.Core.Shapes;

namespace Tasktor.Core.Features.System;

/// <summary>
/// A5 disk guard: free space per fixed volume (from the 60 s tick), the low-space alert (<c>max(10%, 15 GB)</c> for 5 min;
/// again only after 5 GB more are lost or 24 h pass; Snooze 1 day), the learned fill-rate warning (adopted from B: learned
/// normal per volume ×1.5 for 30 min and low within 24 h; fallback ≥ 5 GB/h), and the biggest-folders scan (A5.3) as
/// one of Tasktor's own long tasks. Never deletes anything.
/// </summary>
public sealed class DiskGuard : Feature
{
    public const string LowRuleId = "disk-low";
    public const string FillRuleId = "disk-filling";
    public const double LowPct = 10;
    public const double LowGb = 15;
    public static readonly TimeSpan LowHold = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Realert = TimeSpan.FromHours(24);
    public const double RealertDropGb = 5;
    public static readonly TimeSpan FillWindow = TimeSpan.FromMinutes(30);

    public static readonly LearnedThreshold FillRate = new()
    {
        Policy = "fill_rate",
        Fallback = TimeSpan.FromSeconds(5),
        Min = TimeSpan.FromSeconds(0.5),
        Max = TimeSpan.FromSeconds(1000),
        Ring = s => s.Duration,
    };

    private readonly SystemTotals _totals;
    private readonly InternalTasks _tasks;
    private readonly Dictionary<string, VolumeWatch> _watches = new(StringComparer.Ordinal);
    private readonly KeyedHasher? _hasher;
    private readonly FolderScanner _scanner;
    private CancellationTokenSource? _scan;

    public DiskGuard(FeatureContext context, SystemTotals totals, InternalTasks tasks, FolderScanner? scanner = null)
        : base(context)
    {
        _totals = totals;
        _tasks = tasks;
        _scanner = scanner ?? new FolderScanner();
        _hasher = context.Key?.Hasher("volume");
        Learned = new KindStore(context.Key?.Fingerprint() ?? "none");
    }

    public override string Id => "disk";

    public override FeatureTier Tier => FeatureTier.Core;

    /// <summary>Learned fill rates per volume (GB/h), keyed by a hash of the volume id.</summary>
    public KindStore Learned { get; }

    public IReadOnlyList<VolumeRow> Volumes { get; private set; } = [];

    public FolderNode? LastScan { get; private set; }

    protected override void OnEnable()
    {
        _totals.Sampled += OnSampled;
        if (File.Exists(Context.Store.LearnedVolumes))
        {
            Learned.Load(File.ReadAllBytes(Context.Store.LearnedVolumes));
        }

        Context.RegisterPull("folders", _ => LastScan is null ? null : JsonSerializer.SerializeToNode(LastScan, FolderJsonContext.Default.FolderNode));
        Context.Router.Register("disk.scan", req => StartScan(req));
        Context.Router.Register("disk.open", req =>
        {
            var path = req.Target?.GetValueOrDefault("path") ?? Root(req.Target?.GetValueOrDefault("instance"));
            return new PreparedAction { Summary = "Open " + path, Run = _ => Done("disk.open", Context.Launch(path)) };
        });
        Context.Router.Register("disk.storage_settings", _ => new PreparedAction { Summary = "Open Storage settings", Run = _ => Done("disk.storage_settings", Context.Launch("ms-settings:storagesense")) });
    }

    protected override void OnDisable()
    {
        _totals.Sampled -= OnSampled;
        _scan?.Cancel();
    }

    private static ActionResult Done(string action, bool ok) => new() { Action = action, Status = ok ? ActionStatus.Ok : ActionStatus.Failed, Message = ok ? "Opened" : "Could not open" };

    private static string Root(string? instance) => string.IsNullOrEmpty(instance) ? Path.GetPathRoot(Environment.SystemDirectory) ?? "/" : (instance.EndsWith(':') ? instance + "\\" : instance);

    private void OnSampled(MetricFrame frame, bool isTick)
    {
        var now = frame.At == default ? Context.Clock.UtcNow : frame.At;
        var off = Context.Settings.Current.Disk?.Off ?? [];
        var rows = new List<VolumeRow>();
        foreach (var inst in frame.Values.Where(v => v.Key.Id == MetricIds.VolumeFreeBytes).Select(v => v.Key.Instance).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!frame.TryGet(new MetricKey(MetricIds.VolumeTotalBytes, inst), out var total) || total <= 0)
            {
                continue;
            }

            frame.TryGet(new MetricKey(MetricIds.VolumeFreeBytes, inst), out var free);
            var freeGb = free / (1024.0 * 1024 * 1024);
            var pct = 100 * free / total;
            var low = !off.Contains(inst) && (pct < LowPct || freeGb < LowGb);
            rows.Add(new VolumeRow
            {
                Id = inst,
                Letter = inst.Length == 2 && inst[1] == ':' ? inst : null,
                FreeGb = Math.Round(freeGb, 1),
                TotalGb = Math.Round(total / (1024.0 * 1024 * 1024), 1),
                FreePct = Math.Round(pct, 1),
                Low = low ? true : null,
            });
            if (isTick && !off.Contains(inst))
            {
                Watch(inst).Tick(this, now, freeGb, pct, low);
            }
        }

        Volumes = rows;
        Context.UpdateState(s => s with { System = (s.System ?? new SystemState()) with { Volumes = rows.Count == 0 ? null : rows } });
        PersistIfDirty();
    }

    private VolumeWatch Watch(string inst)
    {
        if (!_watches.TryGetValue(inst, out var w))
        {
            _watches[inst] = w = new VolumeWatch(inst, _hasher?.Hash(inst.ToUpperInvariant()));
        }

        return w;
    }

    public override void Contribute(RuleInputs inputs, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        foreach (var v in Volumes)
        {
            inputs.Metric("disk.free_pct", v.FreePct, v.Id);
            inputs.Metric("disk.free_gb", v.FreeGb, v.Id);
            if (_watches.TryGetValue(v.Id, out var w) && w.RateGbPerHour is { } r)
            {
                inputs.Metric("disk.fill_gb_per_h", r, v.Id);
            }
        }
    }

    public override IReadOnlyList<Card> Cards()
    {
        if (Volumes.Count == 0)
        {
            return [];
        }

        var blocks = new List<Block>
        {
            new MetricBlock { Label = "Free on " + (Volumes[0].Letter ?? Volumes[0].Id), Value = Value.Binding("/state/system/volumes/0/free_gb"), Unit = "GB", Priority = 1, Icon = IconKey.Disk },
        };
        for (var i = 0; i < Volumes.Count; i++)
        {
            blocks.Add(new BarBlock
            {
                Label = Volumes[i].Letter ?? Volumes[i].Id,
                Value = Value.Literal(Math.Round(100 - Volumes[i].FreePct, 1)),
                Max = Value.Literal(100),
                Unit = "%",
                Text = Value.Literal(Volumes[i].FreeGb.ToString("0.#", CultureInfo.InvariantCulture) + " GB free"),
                Severity = Value.Literal(Volumes[i].Low == true ? "warn" : "normal"),
                Priority = 2,
            });
        }

        if (Volumes.FirstOrDefault(v => v.Low == true) is { } low)
        {
            blocks.Add(new BadgeBlock { Text = Value.Literal((low.Letter ?? low.Id) + " is low on space"), Severity = Value.Literal("warn"), Priority = 1 });
        }

        return
        [
            new Card
            {
                Id = "core.disk",
                Source = CardKit.Core,
                Title = "Disk",
                Icon = IconKey.Disk,
                Severity = Value.Literal(Volumes.Any(v => v.Low == true) ? "warn" : "normal"),
                Blocks = blocks,
                Actions =
                [
                    new CardAction { Id = "scan", Label = "Biggest folders", Action = "disk.scan", Target = CardKit.Target("instance", Volumes[0].Id), Priority = 1 },
                    new CardAction { Id = "explorer", Label = "Open in Explorer", Action = "disk.open", Target = CardKit.Target("instance", Volumes[0].Id), Priority = 2 },
                ],
                Link = "tasktor://open?card=core.disk",
                StaleAfterS = 180,
            },
        ];
    }

    private PreparedAction StartScan(ActionRequest req)
    {
        var root = req.Target?.GetValueOrDefault("path") ?? Root(req.Target?.GetValueOrDefault("instance"));
        if (!Directory.Exists(root))
        {
            return new PreparedAction { Summary = "Biggest folders", Refusal = "Folder not found" };
        }

        _scan?.Cancel();
        var cts = new CancellationTokenSource();
        _scan = cts;
        var id = "scan";
        var started = Context.Clock.UtcNow;
        _tasks.Set(new ProgressRow { Id = id, Label = "Folder scan · " + root, Stage = "scanning", State = Contracts.Jobs.ProgressState.Indeterminate, UpdatedUtc = started, StartedUtc = started, Source = ProgressSource.Internal }, cts.Cancel);
        var thread = new Thread(() =>
        {
            try
            {
                var result = _scanner.Scan(root, cts.Token, n => Context.Loop.Post(() => _tasks.Set(new ProgressRow { Id = id, Label = "Folder scan · " + root, Stage = "scanning", Msg = n.ToString("N0", CultureInfo.InvariantCulture) + " folders", State = Contracts.Jobs.ProgressState.Indeterminate, UpdatedUtc = Context.Clock.UtcNow, StartedUtc = started, Source = ProgressSource.Internal })));
                Context.Loop.Post(() =>
                {
                    LastScan = result;
                    _tasks.Remove(id);
                    Context.Persist(Path.Combine(Context.Store.CacheDir, "folders.json"), JsonSerializer.SerializeToUtf8Bytes(result, FolderJsonContext.Default.FolderNode));
                });
            }
            catch (OperationCanceledException)
            {
                Context.Loop.Post(() => _tasks.Remove(id));
            }
        })
        { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "tasktor-folder-scan" };
        return new PreparedAction
        {
            Summary = "Biggest folders in " + root,
            Immediate = new ActionResult { Action = "disk.scan", Status = ActionStatus.Ok, Message = "Scanning " + root + " at low priority" },
            Done = _ => thread.Start(),
        };
    }

    private void PersistIfDirty()
    {
        if (Learned.Dirty)
        {
            Context.Persist(Context.Store.LearnedVolumes, Learned.Serialize());
        }
    }

    private void Toast(string tag, string title, Severity sev, params (string Label, ActionRequest Req)[] buttons)
    {
        var toast = new ToastRequest { Tag = tag, Title = title, Severity = sev, Buttons = buttons.Select(b => new ToastButton { Label = b.Label, Request = b.Req with { Toast = tag } }).ToList() };
        foreach (var b in toast.Buttons)
        {
            Context.Router.Toasts.Offer(tag, b.Request);
        }

        Context.Notifier.Show(toast);
        Context.Log.Info("rule.fired", Logging.LogField.Of("rule", tag.Split(':')[0]), Logging.LogField.Of("severity", CardKit.SeverityWord(sev)));
    }

    /// <summary>Per-volume watch state: the low-space alert and the fill-rate learner.</summary>
    private sealed class VolumeWatch(string instance, KindId? kind)
    {
        private readonly HoldFor _lowHold = new(LowHold);
        private readonly RingBuffer<(DateTimeOffset At, double FreeGb)> _ring = new(61);
        private DateTimeOffset? _alertedAt;
        private double _alertedFreeGb;
        private DateTimeOffset? _snoozedUntil;
        private readonly HoldFor _fillHold = new(FillWindow);
        private DateTimeOffset? _fillAlertedAt;
        private DateTimeOffset _hourStart;
        private double _hourStartFree = double.NaN;

        public double? RateGbPerHour { get; private set; }

        public void Tick(DiskGuard g, DateTimeOffset now, double freeGb, double pct, bool low)
        {
            // Sleep excluded: a gap over 2 ticks restarts the windows.
            if (_ring.Count > 0 && now - _ring.Latest.At > TimeSpan.FromMinutes(3))
            {
                _ring.Clear();
                _fillHold.Reset();
                _hourStartFree = double.NaN;
            }

            _ring.Add((now, freeGb));
            Low(g, now, freeGb, pct, low);
            Fill(g, now, freeGb);
        }

        private void Low(DiskGuard g, DateTimeOffset now, double freeGb, double pct, bool low)
        {
            if (!g.Context.RuleEnabled(LowRuleId) || (_snoozedUntil is { } s && now < s))
            {
                return;
            }

            if (!_lowHold.Update(low, now))
            {
                if (!low)
                {
                    _alertedAt = null;
                }

                return;
            }

            var again = _alertedAt is null || now - _alertedAt >= Realert || _alertedFreeGb - freeGb >= RealertDropGb;
            if (!again)
            {
                return;
            }

            _alertedAt = now;
            _alertedFreeGb = freeGb;
            var tag = LowRuleId + ":" + instance;
            g.Toast(
                tag,
                instance + " is low: " + Math.Round(freeGb).ToString(CultureInfo.InvariantCulture) + " GB free (" + Math.Round(pct).ToString(CultureInfo.InvariantCulture) + "%)",
                Severity.Warn,
                ("Biggest folders", new ActionRequest { Action = "disk.scan", Origin = Origin.Toast, Target = CardKit.Target("instance", instance) }),
                ("Open Storage settings", new ActionRequest { Action = "disk.storage_settings", Origin = Origin.Toast }),
                ("Snooze 1 day", new ActionRequest { Action = "alert.snooze", Origin = Origin.Toast, Target = new Dictionary<string, string>(StringComparer.Ordinal) { ["rule"] = LowRuleId, ["subject"] = instance }, Args = CardKit.Target("for", "1d") }));
            g.Context.UpdateState(st => st with
            {
                Alerts = [.. (st.Alerts ?? []).Where(a => !(a.Rule == LowRuleId && a.Text?.StartsWith(instance, StringComparison.Ordinal) == true)), new AlertState { Rule = LowRuleId, Severity = Severity.Warn, Card = "core.disk", Since = now, Text = instance + " is low on space" }],
            });
        }

        public void Snooze(DateTimeOffset until) => _snoozedUntil = until;

        private void Fill(DiskGuard g, DateTimeOffset now, double freeGb)
        {
            // Rate over the last 30 min (needs a full window).
            var oldest = -1;
            for (var i = 0; i < _ring.Count; i++)
            {
                if (now - _ring[i].At <= FillWindow + TimeSpan.FromSeconds(30))
                {
                    oldest = i;
                    break;
                }
            }

            RateGbPerHour = null;
            if (oldest >= 0 && now - _ring[oldest].At >= FillWindow - TimeSpan.FromSeconds(30))
            {
                var hours = (now - _ring[oldest].At).TotalHours;
                RateGbPerHour = Math.Max(0, (_ring[oldest].FreeGb - freeGb) / hours);
            }

            // Learn: one sample of the normal fill rate per hour.
            if (double.IsNaN(_hourStartFree))
            {
                _hourStart = now;
                _hourStartFree = freeGb;
            }
            else if (now - _hourStart >= TimeSpan.FromHours(1) && kind is { } k)
            {
                var hourly = Math.Max(0, (_hourStartFree - freeGb) / (now - _hourStart).TotalHours);
                g.Learned.Get(k, now).RecordRun(new RunOutcome { Good = true, DurationS = hourly });
                _hourStart = now;
                _hourStartFree = freeGb;
            }

            if (RateGbPerHour is not { } rate || !g.Context.RuleEnabled(FillRuleId))
            {
                _fillHold.Reset();
                return;
            }

            var stats = kind is { } kk ? g.Learned.Find(kk) : null;
            var threshold = FillRate.Current(stats, g.Learned.LearningOn);
            var lowAt = Math.Max(LowGb, 0.1 * (g.Volumes.FirstOrDefault(v => v.Id == instance)?.TotalGb ?? 0));
            var hoursToLow = rate > 0 ? (freeGb - lowAt) / rate : double.PositiveInfinity;
            var fast = rate >= threshold.Value && hoursToLow is > 0 and < 24;
            if (_fillHold.Update(fast, now) && (_fillAlertedAt is null || now - _fillAlertedAt >= Realert))
            {
                _fillAlertedAt = now;
                var source = threshold.Source is Contracts.Snapshot.ValueSource.Learned or Contracts.Snapshot.ValueSource.Blended ? "learned" : "default";
                g.Toast(
                    FillRuleId + ":" + instance,
                    instance + " is filling fast: " + rate.ToString("0.#", CultureInfo.InvariantCulture) + " GB/h (" + source + " limit " + threshold.Value.ToString("0.#", CultureInfo.InvariantCulture) + " GB/h) · low in about " + Math.Max(1, Math.Round(hoursToLow)).ToString(CultureInfo.InvariantCulture) + " h",
                    Severity.Warn,
                    ("Biggest folders", new ActionRequest { Action = "disk.scan", Origin = Origin.Toast, Target = CardKit.Target("instance", instance) }),
                    ("Open Storage settings", new ActionRequest { Action = "disk.storage_settings", Origin = Origin.Toast }));
                LastFillSource = source;
            }
        }

        public string? LastFillSource { get; private set; }
    }

    /// <summary>Snooze a volume's low-space alert (the toast's Snooze 1 day).</summary>
    public void Snooze(string instance, DateTimeOffset until) => Watch(instance).Snooze(until);

    /// <summary>For tests: which limit the last fill-rate toast used ("learned" or "default").</summary>
    public string? LastFillSource(string instance) => _watches.TryGetValue(instance, out var w) ? w.LastFillSource : null;
}
