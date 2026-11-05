using System.Globalization;
using System.Text.Json.Nodes;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Snapshot;
using Tasktor.Core.Loop;
using Tasktor.Core.Rules;
using Tasktor.Core.Shapes;

namespace Tasktor.Core.Features.SystemMonitor;

/// <summary>
/// A1: CPU, RAM, commit, GPU per adapter, disk, network (D-A1: totals every 60 s always, the last hour in a 60-slot ring
/// in memory; 1–2 s while a viewer holds a lease). Colour thresholds change only after holding 10 s. Owns
/// <c>state.system</c> and the <c>core.system</c> card; the A2.7 verdict comes from <see cref="Verdict"/>.
/// </summary>
public sealed class SystemTotals : Feature
{
    public const string SamplerName = "system";
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(60);

    private static readonly string[] HourSeries = ["system.cpu_pct", "system.ram_used_pct", "system.commit_pct", "system.gpu_pct", "system.disk_active_pct", "system.net_rx_bps", "system.net_tx_bps"];

    private readonly IMetricSource _source;
    private readonly Dictionary<string, RingBuffer<float>> _hour = [];
    private readonly Dictionary<string, RingBuffer<float>> _live = [];
    private readonly Dictionary<MetricKey, RateCounter> _rates = [];
    private readonly Dictionary<string, SeverityHold> _holds = [];
    private DemandLease? _tickLease;
    private DateTimeOffset _lastHourPoint = DateTimeOffset.MinValue;

    public SystemTotals(FeatureContext context, IMetricSource source)
        : base(context)
    {
        _source = source;
        foreach (var s in HourSeries)
        {
            _hour[s] = new RingBuffer<float>(60);
            _live[s] = new RingBuffer<float>(60);
        }
    }

    public override string Id => "system";

    public override FeatureTier Tier => FeatureTier.Core;

    /// <summary>The latest values (loop-only).</summary>
    public SystemState Latest { get; private set; } = new();

    /// <summary>The latest raw frame (DiskGuard reads volumes from it).</summary>
    public MetricFrame LatestFrame { get; } = new();

    /// <summary>The A2.7 verdict line and its severity, computed by the apps feature (null when nothing is saturated).</summary>
    public Func<SystemState, (string Text, Severity Severity)?>? Verdict { get; set; }

    /// <summary>Raised on the loop after each sample; <c>isTick</c> marks the 60 s history points.</summary>
    public event Action<MetricFrame, bool>? Sampled;

    public long Samples { get; private set; }

    public IReadOnlyDictionary<string, RingBuffer<float>> HourRing => _hour;

    protected override void OnEnable()
    {
        if (!Context.Scheduler.IsRegistered(SamplerName))
        {
            Context.Scheduler.Register(SamplerName, _ => Read(), ReleaseLive);
        }

        _tickLease = Context.Scheduler.Lease(SamplerName, Tick, ColumnSet.Names, holder: "tick");
        Context.RegisterPull("series", PullSeries);
    }

    protected override void OnDisable()
    {
        _tickLease?.Dispose();
        _tickLease = null;
    }

    /// <summary>Read the source off the loop and post the frame back (sources only enqueue).</summary>
    public void Read()
    {
        Context.OffLoop(() =>
        {
            var frame = new MetricFrame();
            _source.Read(frame);
            frame.At = Context.Clock.UtcNow;
            Context.Loop.Post(() => Ingest(frame));
        });
    }

    /// <summary>Apply one frame (loop-only; tests call it directly).</summary>
    public void Ingest(MetricFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Samples++;
        var now = frame.At == default ? Context.Clock.UtcNow : frame.At;
        frame.CopyTo(LatestFrame);
        var settings = Context.Settings;

        double? cpu = frame.TryGet(MetricIds.CpuPct, out var c) ? Math.Clamp(c, 0, 100) : null;
        double? ramPct = null;
        double? availGb = null;
        double? totalGb = null;
        if (frame.TryGet(MetricIds.RamAvailableMb, out var avail) && frame.TryGet(MetricIds.RamTotalMb, out var total) && total > 0)
        {
            ramPct = Math.Clamp(100 * (1 - (avail / total)), 0, 100);
            availGb = avail / 1024;
            totalGb = total / 1024;
        }

        double? commitPct = frame.TryGet(MetricIds.CommitBytes, out var cb) && frame.TryGet(MetricIds.CommitLimitBytes, out var cl) && cl > 0 ? Math.Clamp(100 * cb / cl, 0, 100) : null;

        var gpus = new List<GpuRow>();
        foreach (var inst in Instances(frame, MetricIds.GpuPct, MetricIds.GpuDedicatedMb, MetricIds.GpuSharedMb))
        {
            frame.TryGet(new MetricKey(MetricIds.GpuPct, inst), out var gp);
            frame.TryGet(new MetricKey(MetricIds.GpuDedicatedMb, inst), out var ded);
            frame.TryGet(new MetricKey(MetricIds.GpuSharedMb, inst), out var sh);
            if (gp <= 0 && ded <= 0 && sh < 1)
            {
                continue; // virtual / indirect display adapters: no engines in use, no memory (A1 edge case)
            }

            gpus.Add(new GpuRow { Id = inst, Pct = Math.Round(Math.Clamp(gp, 0, 100), 1), DedicatedMb = Math.Round(ded), SharedMb = Math.Round(sh) });
        }

        var disks = new List<DiskRow>();
        foreach (var inst in Instances(frame, MetricIds.DiskIdlePct))
        {
            frame.TryGet(new MetricKey(MetricIds.DiskIdlePct, inst), out var idle);
            frame.TryGet(new MetricKey(MetricIds.DiskReadBps, inst), out var rd);
            frame.TryGet(new MetricKey(MetricIds.DiskWriteBps, inst), out var wr);
            disks.Add(new DiskRow { Id = inst, ActivePct = Math.Round(Math.Clamp(100 - idle, 0, 100), 1), ReadBps = Math.Round(rd), WriteBps = Math.Round(wr) });
        }

        double rx = 0, tx = 0;
        var anyNet = false;
        foreach (var kv in frame.Values)
        {
            if (kv.Key.Id is MetricIds.NetRxBytes or MetricIds.NetTxBytes)
            {
                if (!_rates.TryGetValue(kv.Key, out var rc))
                {
                    _rates[kv.Key] = rc = new RateCounter();
                }

                if (rc.Update(kv.Value, now) is { } r)
                {
                    anyNet = true;
                    if (kv.Key.Id == MetricIds.NetRxBytes)
                    {
                        rx += r;
                    }
                    else
                    {
                        tx += r;
                    }
                }
            }
        }

        if (frame.TryGet(MetricIds.NetRxBps, out var rxDirect) && frame.TryGet(MetricIds.NetTxBps, out var txDirect))
        {
            rx = rxDirect;
            tx = txDirect;
            anyNet = true;
        }

        var gpuMax = gpus.Count == 0 ? (double?)null : gpus.Max(g => g.Pct);
        var diskMax = disks.Count == 0 ? (double?)null : disks.Max(d => d.ActivePct);
        Raw = new RawTotals(cpu, ramPct, commitPct, gpuMax, diskMax, anyNet ? rx : null, anyNet ? tx : null);

        var state = new SystemState
        {
            CpuPct = cpu is null ? null : Math.Round(cpu.Value, 1),
            RamUsedPct = ramPct is null ? null : Math.Round(ramPct.Value, 1),
            RamAvailableGb = availGb is null ? null : Math.Round(availGb.Value, 2),
            RamTotalGb = totalGb is null ? null : Math.Round(totalGb.Value, 1),
            CommitPct = commitPct is null ? null : Math.Round(commitPct.Value, 1),
            Gpu = gpus.Count == 0 ? null : gpus,
            Disks = disks.Count == 0 ? null : disks,
            Net = anyNet ? new NetState { RxBps = Math.Round(settings.Current.Display?.NetworkBits == true ? rx * 8 : rx), TxBps = Math.Round(settings.Current.Display?.NetworkBits == true ? tx * 8 : tx) } : null,
            Volumes = Context.State().System?.Volumes,
            Battery = Context.State().System?.Battery,
        };

        // A1.7: worst held severity across the coloured rows.
        var sev = Severity.Normal;
        foreach (var (name, v) in new[] { ("cpu", cpu), ("ram", ramPct), ("gpu", gpuMax), ("disk_active", diskMax) })
        {
            if (v is null)
            {
                continue;
            }

            if (!_holds.TryGetValue(name, out var hold))
            {
                _holds[name] = hold = new SeverityHold(TimeSpan.FromSeconds(10));
            }

            var held = hold.Update(SeverityHold.Classify(v.Value, settings.ThresholdFor(name)), now);
            if (held > sev)
            {
                sev = held;
            }
        }

        RowSeverity = sev;
        var verdict = Verdict?.Invoke(state);
        state = state with { Verdict = verdict?.Text, VerdictSeverity = verdict is { } vv ? (vv.Severity > sev ? vv.Severity : sev) : (sev == Severity.Normal ? null : sev) };

        AddPoint(_live, Raw);
        var isTick = now - _lastHourPoint >= Tick - TimeSpan.FromSeconds(2);
        if (isTick)
        {
            _lastHourPoint = now;
            AddPoint(_hour, Raw);
        }

        Latest = state;
        Context.UpdateState(s => s with { System = state });
        Sampled?.Invoke(frame, isTick);
        Context.RulesDirty();
    }

    /// <summary>The unrounded totals of the last sample.</summary>
    public RawTotals Raw { get; private set; } = new(null, null, null, null, null, null, null);

    /// <summary>The worst row colour after the 10 s hold.</summary>
    public Severity RowSeverity { get; private set; }

    public sealed record RawTotals(double? Cpu, double? Ram, double? Commit, double? Gpu, double? DiskActive, double? RxBps, double? TxBps);

    public override void Contribute(RuleInputs inputs, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var r = Raw;
        if (r.Cpu is { } cpu)
        {
            inputs.Metric("system.cpu_pct", cpu);
            inputs.Metric("system.cpu_pct_2m", Average(_live["system.cpu_pct"], 120) ?? cpu);
        }

        if (r.Ram is { } ram)
        {
            inputs.Metric("system.ram_used_pct", ram);
        }

        if (Latest.RamAvailableGb is { } ag)
        {
            inputs.Metric("system.ram_available_gb", ag);
        }

        if (r.Commit is { } commit)
        {
            inputs.Metric("system.commit_pct", commit);
        }

        if (r.Gpu is { } gpu)
        {
            inputs.Metric("system.gpu_pct", gpu);
        }

        foreach (var d in Latest.Disks ?? [])
        {
            inputs.Metric("disk.active_pct", d.ActivePct, d.Id);
        }

        if (r.RxBps is { } rx)
        {
            inputs.Metric("net.rx_bps", rx);
        }

        if (r.TxBps is { } tx)
        {
            inputs.Metric("net.tx_bps", tx);
        }
    }

    public override IReadOnlyList<Card> Cards()
    {
        var hidden = Context.Settings.Current.Display?.HiddenRows ?? [];
        var blocks = new List<Block>
        {
            new MetricBlock { Label = "CPU", Value = Value.Binding("/state/system/cpu_pct"), Unit = "%", Priority = 1, Icon = IconKey.Cpu },
            new MetricBlock { Label = "RAM", Value = Value.Binding("/state/system/ram_used_pct"), Unit = "%", Priority = 1, Icon = IconKey.Ram },
        };
        if (Latest.Gpu is { Count: > 0 } && !hidden.Contains("gpu"))
        {
            blocks.Add(new MetricBlock { Label = "GPU", Value = Value.Binding("/state/system/gpu/0/pct"), Unit = "%", Priority = 2, Icon = IconKey.Gpu });
        }

        if (Latest.Verdict is not null)
        {
            blocks.Add(new BadgeBlock { Text = Value.Binding("/state/system/verdict"), Severity = Value.Binding("/state/system/verdict_severity"), Priority = 1 });
        }

        blocks.Add(new SparklineBlock { Label = "CPU", Series = "system.cpu_pct", Unit = "%", Priority = 3 });
        blocks.Add(new SparklineBlock { Label = "RAM", Series = "system.ram_used_pct", Unit = "%", Priority = 4 });
        blocks.Add(new TableBlock
        {
            Label = "Top apps",
            Columns = [new TableColumn { Key = "name", Label = "App" }, new TableColumn { Key = "cpu_pct", Label = "CPU", Unit = "%" }, new TableColumn { Key = "private_mb", Label = "RAM", Unit = "MB" }],
            Rows = Value.Binding("/state/apps"),
            Max = 3,
            RowKey = "app_id",
            Priority = 5,
        });
        if (Latest.Volumes is { Count: > 0 } && !hidden.Contains("disk"))
        {
            blocks.Add(new MetricBlock { Label = "Free on " + (Latest.Volumes[0].Letter ?? Latest.Volumes[0].Id), Value = Value.Binding("/state/system/volumes/0/free_gb"), Unit = "GB", Priority = 6, Icon = IconKey.Disk });
        }

        if (Latest.Net is not null && !hidden.Contains("network"))
        {
            var bits = Context.Settings.Current.Display?.NetworkBits == true;
            blocks.Add(new MetricBlock { Label = "Down", Value = Value.Binding("/state/system/net/rx_bps"), Unit = bits ? "bit/s" : "B/s", Priority = 7, Icon = IconKey.Network });
        }

        return
        [
            new Card
            {
                Id = "core.system",
                Source = CardKit.Core,
                Title = "System",
                Icon = IconKey.Cpu,
                Severity = Value.Binding("/state/system/verdict_severity"),
                Blocks = blocks,
                Actions = [new CardAction { Id = "open", Label = "Open", Action = "open", Target = CardKit.Target("card", "core.system") }],
                Link = "tasktor://open?card=core.system",
                StaleAfterS = 180,
            },
        ];
    }

    /// <summary><c>pull series</c> with <c>args.id</c>: the hour ring (60 tick points) and the live ring.</summary>
    private JsonNode? PullSeries(Contracts.Pipe.PullMessage pull)
    {
        var id = pull.Args?.GetValueOrDefault("id") ?? "system.cpu_pct";
        if (!_hour.TryGetValue(id, out var hour))
        {
            return null;
        }

        return new JsonObject
        {
            ["id"] = id,
            ["hour"] = new JsonArray([.. hour.ToArray().Select(v => (JsonNode?)JsonValue.Create(Math.Round(v, 2)))]),
            ["live"] = new JsonArray([.. _live[id].ToArray().Select(v => (JsonNode?)JsonValue.Create(Math.Round(v, 2)))]),
        };
    }

    private void ReleaseLive()
    {
        foreach (var r in _live.Values)
        {
            r.Clear();
        }
    }

    private static void AddPoint(Dictionary<string, RingBuffer<float>> rings, RawTotals r)
    {
        Add(rings, "system.cpu_pct", r.Cpu);
        Add(rings, "system.ram_used_pct", r.Ram);
        Add(rings, "system.commit_pct", r.Commit);
        Add(rings, "system.gpu_pct", r.Gpu);
        Add(rings, "system.disk_active_pct", r.DiskActive);
        Add(rings, "system.net_rx_bps", r.RxBps);
        Add(rings, "system.net_tx_bps", r.TxBps);
    }

    private static void Add(Dictionary<string, RingBuffer<float>> rings, string id, double? v)
    {
        if (v is { } value)
        {
            rings[id].Add((float)value);
        }
    }

    private static double? Average(RingBuffer<float> ring, int lastN)
    {
        if (ring.Count == 0)
        {
            return null;
        }

        var n = Math.Min(lastN, ring.Count);
        double sum = 0;
        for (var i = ring.Count - n; i < ring.Count; i++)
        {
            sum += ring[i];
        }

        return sum / n;
    }

    private static IEnumerable<string> Instances(MetricFrame frame, params string[] ids) =>
        frame.Values.Where(kv => ids.Contains(kv.Key.Id)).Select(kv => kv.Key.Instance).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);

    internal static string Gb(double bytes) => (bytes / (1024.0 * 1024 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " GB";
}
