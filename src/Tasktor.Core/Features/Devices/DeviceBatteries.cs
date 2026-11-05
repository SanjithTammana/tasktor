using System.Text.Json;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Platform;

namespace Tasktor.Core.Features.Devices;

/// <summary>
/// E8 Bluetooth batteries (Module <c>batteries</c>, limited): paired devices with their battery percent, low at 15%.
/// The source is read every 10 minutes and only while the Module is on (a lease); a device that does not report its
/// level is listed as such. Left / right / case values of earbuds are not exposed by Windows (E9).
/// </summary>
public sealed class DeviceBatteries : Feature
{
    public const string SamplerName = "bt-batteries";
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    public const int Low = 15;

    private readonly ITableSource<string, DeviceBattery> _source;
    private IDisposable? _lease;

    public DeviceBatteries(FeatureContext context, ITableSource<string, DeviceBattery> source)
        : base(context) => _source = source;

    public override string Id => "batteries";

    public override FeatureTier Tier => FeatureTier.Module;

    public IReadOnlyList<DeviceBattery> Devices { get; private set; } = [];

    protected override void OnEnable()
    {
        if (!Context.Scheduler.IsRegistered(SamplerName))
        {
            Context.Scheduler.Register(SamplerName, _ => Read(), () => Devices = []);
        }

        _lease = Context.Scheduler.Lease(SamplerName, Interval, ColumnSet.None, holder: "batteries");
    }

    protected override void OnDisable()
    {
        _lease?.Dispose();
        Devices = [];
    }

    public void Read() => Context.OffLoop(() =>
    {
        var rows = new List<KeyValuePair<string, DeviceBattery>>();
        _source.Read(rows, ColumnSet.None);
        Context.Loop.Post(() => Devices = [.. rows.Select(r => r.Value).OrderBy(d => d.Pct ?? 101).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)]);
    });

    public override IReadOnlyList<Card> Cards()
    {
        if (!Enabled)
        {
            return [];
        }

        var blocks = new List<Block>();
        var i = 1;
        foreach (var d in Devices.Take(6))
        {
            blocks.Add(d.Pct is { } pct
                ? new BarBlock { Label = d.Name, Value = Value.Literal(pct), Max = Value.Literal(100), Unit = "%", Severity = Value.Literal(pct <= Low ? "warn" : "normal"), Priority = i++ }
                : new BadgeBlock { Text = Value.Literal(d.Name + ": does not report its battery"), Priority = i++ });
        }

        if (blocks.Count == 0)
        {
            blocks.Add(new BadgeBlock { Text = Value.Literal("No paired devices report a battery level"), Priority = 1 });
        }

        return
        [
            new Card
            {
                Id = "module.batteries",
                Source = CardKit.ModuleSource,
                Title = "Device batteries",
                Icon = IconKey.Battery,
                Severity = Value.Literal(Devices.Any(d => d.Pct <= Low) ? "warn" : "normal"),
                Blocks = blocks,
                StaleAfterS = (int)(Interval.TotalSeconds * 2),
            },
        ];
    }
}
