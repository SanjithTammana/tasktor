using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Snapshot;
using Tasktor.Core.Actions;
using Tasktor.Core.Rules;
using Tasktor.Core.Storage;

namespace Tasktor.Core.Features.Network;

/// <summary>Drops per hour, persisted (30 days) for the card and the export.</summary>
public sealed record ConnectivityFile
{
    public int SchemaVersion { get; init; } = 1;

    /// <summary>UTC hour (ISO, <c>2026-11-04T09</c>) → drops.</summary>
    public Dictionary<string, int> Hourly { get; set; } = new(StringComparer.Ordinal);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ConnectivityFile))]
internal sealed partial class ConnectivityJsonContext : JsonSerializerContext;

/// <summary>
/// I14 Connectivity watch (Module <c>connectivity</c>): online / offline / captive portal from the OS's connectivity
/// events (zero traffic, D-S6), drops (transitions to offline) per hour and day, Wi-Fi signal as a sparkline only while
/// the card holds a lease, and the processes holding the most established TCP connections ("connections, not bytes":
/// per-process bytes would need admin, and Tasktor never elevates). States <c>offline</c> / <c>captive_portal</c>,
/// metric <c>connectivity.drops_10m</c>, event <c>net.dropped</c>. No SSIDs or locations are read.
/// </summary>
public sealed class Connectivity : Feature
{
    public const string SignalSampler = "wifi";
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    private readonly IEventSource<NetEvent>? _events;
    private readonly IMetricSource? _signal;
    private readonly List<DateTimeOffset> _drops = [];
    private readonly List<RuleEvent> _pending = [];
    private readonly List<double> _signalPoints = [];
    private ConnectivityFile _file = new();

    public Connectivity(FeatureContext context, IEventSource<NetEvent>? events, IMetricSource? signal)
        : base(context)
    {
        _events = events;
        _signal = signal;
    }

    public override string Id => "connectivity";

    public override FeatureTier Tier => FeatureTier.Module;

    public NetStatus Status { get; private set; } = NetStatus.Unknown;

    /// <summary>Established TCP connections per app, most first (Windows: <c>GetExtendedTcpTable</c>, no admin). Off the loop.</summary>
    public Func<IReadOnlyList<(string App, int Connections)>>? TopConnections { get; set; }

    public IReadOnlyList<(string App, int Connections)> Holders { get; private set; } = [];

    protected override void OnEnable()
    {
        Load();
        _events?.Start(e => Context.Loop.Post(() => Apply(e)));
        if (_signal is not null && !Context.Scheduler.IsRegistered(SignalSampler))
        {
            Context.Scheduler.Register(SignalSampler, _ => ReadSignal());
        }

        Context.Router.Register("net.signin", _ => new PreparedAction
        {
            Summary = "Open the sign-in page",
            // Windows' own captive-portal trigger page; the browser connects, never the agent.
            Run = _ => new ActionResult { Action = "net.signin", Status = Context.Launch("http://www.msftconnecttest.com/redirect") ? ActionStatus.Ok : ActionStatus.Failed },
        });
        Context.RegisterPull("connections", _ =>
        {
            RefreshHolders();
            return null;
        });
        Context.RegisterExport(new ExportTableSource("connectivity_hourly", ["hour_utc", "drops"], since => _file.Hourly
            .Where(kv => since is null || string.CompareOrdinal(kv.Key, since.Value.UtcDateTime.ToString("yyyy-MM-dd'T'HH", CultureInfo.InvariantCulture)) >= 0)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new object?[] { kv.Key, kv.Value })));
        Publish();
    }

    protected override void OnDisable()
    {
        _events?.Stop();
        _signalPoints.Clear();
        Context.UpdateState(s => s with { Connectivity = null });
    }

    public void Apply(NetEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var was = Status;
        Status = e.Status;
        if (was is NetStatus.Online or NetStatus.CaptivePortal or NetStatus.LocalOnly && e.Status == NetStatus.Offline)
        {
            _drops.Add(e.At);
            var hour = e.At.UtcDateTime.ToString("yyyy-MM-dd'T'HH", CultureInfo.InvariantCulture);
            _file.Hourly[hour] = _file.Hourly.GetValueOrDefault(hour) + 1;
            var oldest = (e.At - Retention).UtcDateTime.ToString("yyyy-MM-dd'T'HH", CultureInfo.InvariantCulture);
            foreach (var k in _file.Hourly.Keys.Where(k => string.CompareOrdinal(k, oldest) < 0).ToList())
            {
                _file.Hourly.Remove(k);
            }

            Context.Persist(Context.Store.Connectivity, JsonSerializer.SerializeToUtf8Bytes(_file, ConnectivityJsonContext.Default.ConnectivityFile));
            _pending.Add(new RuleEvent { Name = "net.dropped", Id = "drop-" + e.At.UtcTicks.ToString(CultureInfo.InvariantCulture) });
            Context.Log.Info("connectivity.dropped");
        }

        _drops.RemoveAll(t => e.At - t > TimeSpan.FromDays(1));
        if (was != e.Status)
        {
            RefreshHolders();
            Context.RulesDirty();
        }

        Publish();
    }

    private void ReadSignal()
    {
        Context.OffLoop(() =>
        {
            var frame = new MetricFrame();
            _signal!.Read(frame);
            Context.Loop.Post(() =>
            {
                if (frame.TryGet("net.wifi_signal", out var v))
                {
                    _signalPoints.Add(Math.Round(v));
                    if (_signalPoints.Count > 60)
                    {
                        _signalPoints.RemoveAt(0);
                    }
                }
            });
        });
    }

    public void RefreshHolders()
    {
        if (TopConnections is not { } top)
        {
            return;
        }

        Context.OffLoop(() =>
        {
            var list = top().Take(5).ToList();
            Context.Loop.Post(() => Holders = list);
        });
    }

    public int DropsSince(DateTimeOffset t) => _drops.Count(d => d >= t);

    public int DropsToday(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local);
        var midnight = new DateTimeOffset(local.Date, local.Offset);
        return DropsSince(midnight);
    }

    public int DropsThisWeek(DateTimeOffset now)
    {
        var from = now.AddDays(-7).UtcDateTime.ToString("yyyy-MM-dd'T'HH", CultureInfo.InvariantCulture);
        return _file.Hourly.Where(kv => string.CompareOrdinal(kv.Key, from) >= 0).Sum(kv => kv.Value);
    }

    /// <summary>Drops in each of the last 24 hours, oldest first (the card's sparkline).</summary>
    public List<double> HourlyLast24(DateTimeOffset now)
    {
        var list = new List<double>(24);
        for (var h = 23; h >= 0; h--)
        {
            var key = now.AddHours(-h).UtcDateTime.ToString("yyyy-MM-dd'T'HH", CultureInfo.InvariantCulture);
            list.Add(_file.Hourly.GetValueOrDefault(key));
        }

        return list;
    }

    private void Publish()
    {
        var now = Context.Clock.UtcNow;
        var word = Word(Status);
        var today = DropsToday(now);
        Context.UpdateState(s => s with { Connectivity = new ConnectivityState { Status = word, DropsToday = today } });
    }

    public static string Word(NetStatus s) => s switch
    {
        NetStatus.Online => "online",
        NetStatus.Offline => "offline",
        NetStatus.CaptivePortal => "captive_portal",
        NetStatus.LocalOnly => "local_only",
        _ => "unknown",
    };

    private void Load()
    {
        try
        {
            if (File.Exists(Context.Store.Connectivity))
            {
                _file = JsonSerializer.Deserialize(File.ReadAllBytes(Context.Store.Connectivity), ConnectivityJsonContext.Default.ConnectivityFile) ?? new();
            }
        }
        catch (JsonException)
        {
            _file = new();
        }
    }

    public override void Contribute(RuleInputs inputs, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        inputs.State("offline", Status == NetStatus.Offline);
        inputs.State("captive_portal", Status == NetStatus.CaptivePortal);
        inputs.Metric("connectivity.drops_10m", DropsSince(now - TimeSpan.FromMinutes(10)));
        inputs.Events.AddRange(_pending);
        _pending.Clear();
    }

    public override IReadOnlyList<Card> Cards()
    {
        if (!Enabled)
        {
            return [];
        }

        var now = Context.Clock.UtcNow;
        var blocks = new List<Block>
        {
            new MetricBlock { Label = "Network", Value = Value.Binding("/state/connectivity/status"), Priority = 1 },
            new MetricBlock { Label = "Drops today", Value = Value.Binding("/state/connectivity/drops_today"), Priority = 2 },
            new SparklineBlock { Label = "Drops per hour", Points = HourlyLast24(now), Priority = 3 },
            new MetricBlock { Label = "Drops this week", Value = Value.Literal(DropsThisWeek(now)), Priority = 4 },
        };
        if (_signalPoints.Count > 1)
        {
            blocks.Add(new SparklineBlock { Label = "Wi-Fi signal", Points = [.. _signalPoints], Unit = "%", Priority = 5 });
        }

        if (Holders.Count > 0)
        {
            var items = Holders.Select(h => h.App + " · " + h.Connections + " connections").ToList();
            blocks.Add(new ListBlock { Label = "Most connections (not bytes)", Items = Value.FromRawJson(JsonSerializer.Serialize<IReadOnlyList<string>>(items, Contracts.ContractsJsonContext.Default.IReadOnlyListString)), Max = 5, Priority = 6 });
        }

        return
        [
            new Card
            {
                Id = "module.connectivity",
                Source = CardKit.ModuleSource,
                Title = "Connectivity",
                Icon = IconKey.Network,
                Severity = Value.Literal(Status is NetStatus.Offline or NetStatus.CaptivePortal ? "warn" : "normal"),
                Blocks = blocks,
                Actions = Status == NetStatus.CaptivePortal ? [new CardAction { Id = "signin", Label = "Sign in", Action = "net.signin" }] : null,
            },
        ];
    }
}
