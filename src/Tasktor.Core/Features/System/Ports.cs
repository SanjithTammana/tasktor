using System.Globalization;
using System.Text.Json.Nodes;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Pipe;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Snapshot;

namespace Tasktor.Core.Features.SystemMonitor;

/// <summary>
/// A7 dev ports (Module): listening TCP ports with their owner, sampled every 5 s only while a viewer holds a lease (never on
/// the tick). Default filter: the user's own processes (SYSTEM / service listeners and the dynamic range hidden).
/// IPv4 + IPv6 on one port and owner merge into one row; a listener not bound to loopback gets the "open to network"
/// badge (report only). Port-conflict lookup answers "3000 is taken by node (PID …)" or "free".
/// </summary>
public sealed class Ports : Feature
{
    public const string SamplerName = "ports";
    public const int DynamicStart = 49152;

    private readonly ITableSource<string, ListenerEntry> _source;
    private List<ListenerRow> _all = [];

    public Ports(FeatureContext context, ITableSource<string, ListenerEntry> source)
        : base(context) => _source = source;

    public override string Id => "ports";

    public override FeatureTier Tier => FeatureTier.Module;

    public IReadOnlyList<ListenerRow> All => _all;

    protected override void OnEnable()
    {
        if (!Context.Scheduler.IsRegistered(SamplerName))
        {
            Context.Scheduler.Register(SamplerName, _ => Read(), () => _all = []);
        }

        Context.RegisterPull("ports", PullAll);
        Context.RegisterPull("port", PullOne);
    }

    protected override void OnDisable()
    {
        _all = [];
        Context.UpdateState(s => s with { Ports = null });
    }

    public void Read() => Context.OffLoop(() =>
    {
        var rows = new List<KeyValuePair<string, ListenerEntry>>();
        _source.Read(rows, ColumnSet.All);
        Context.Loop.Post(() => Ingest(rows.Select(r => r.Value).ToList()));
    });

    public void Ingest(IReadOnlyList<ListenerEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _all = Merge(entries);
        var shown = Filter(_all, showWindows: false, showEphemeral: false);
        Context.UpdateState(s => s with { Ports = shown });
    }

    /// <summary>Merge IPv4 / IPv6 entries of one port and owner; flag listeners reachable from the network.</summary>
    public static List<ListenerRow> Merge(IReadOnlyList<ListenerEntry> entries) =>
        entries.GroupBy(e => (e.Port, e.Pid)).Select(g => new ListenerRow
        {
            Port = g.Key.Port,
            Pid = g.Key.Pid,
            App = g.First().Exe,
            Addresses = g.Select(e => e.Address).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            Exposed = g.Any(e => !IsLoopback(e.Address)),
            Own = g.First().OwnUser,
        }).OrderBy(r => r.Port).ToList();

    public static List<ListenerRow> Filter(IEnumerable<ListenerRow> rows, bool showWindows, bool showEphemeral) =>
        rows.Where(r => (r.Own || showWindows) && (r.Port < DynamicStart || r.Own || showEphemeral)).ToList();

    public static bool IsLoopback(string address) =>
        address.StartsWith("127.", StringComparison.Ordinal) || address is "::1" or "localhost" or "[::1]";

    public override IReadOnlyList<Card> Cards()
    {
        var shown = Context.State().Ports ?? [];
        var exposed = shown.Count(r => r.Exposed);
        var blocks = new List<Block>
        {
            new TableBlock
            {
                Columns = [new TableColumn { Key = "port", Label = "Port" }, new TableColumn { Key = "app", Label = "App" }, new TableColumn { Key = "addresses", Label = "Address" }],
                Rows = Value.Binding("/state/ports"),
                Max = 5,
                RowKey = "pid",
                Priority = 1,
            },
        };
        if (exposed > 0)
        {
            blocks.Add(new BadgeBlock { Text = Value.Literal(exposed + " open to network"), Severity = Value.Literal("warn"), Priority = 2 });
        }

        return
        [
            new Card
            {
                Id = "module.ports",
                Source = CardKit.ModuleSource,
                Title = "Dev ports",
                Icon = IconKey.Ports,
                Blocks = blocks,
                Actions = [new CardAction { Id = "kill", Label = "Kill…", Action = "proc.end", Target = CardKit.Target("pid", "{row}"), Destructive = true }],
                StaleAfterS = 30,
            },
        ];
    }

    private JsonNode? PullAll(PullMessage pull)
    {
        var windows = pull.Args?.GetValueOrDefault("show_windows") == "true";
        var eph = pull.Args?.GetValueOrDefault("show_ephemeral") == "true";
        var arr = new JsonArray();
        foreach (var r in Filter(_all, windows, eph))
        {
            arr.Add((JsonNode)new JsonObject { ["port"] = r.Port, ["app"] = r.App, ["pid"] = r.Pid, ["addresses"] = string.Join(", ", r.Addresses), ["exposed"] = r.Exposed });
        }

        return arr;
    }

    private JsonNode? PullOne(PullMessage pull)
    {
        if (!int.TryParse(pull.Args?.GetValueOrDefault("port"), NumberStyles.None, CultureInfo.InvariantCulture, out var port))
        {
            return null;
        }

        return JsonValue.Create(Lookup(port));
    }

    /// <summary>A7.5: "3000 is taken by node (PID 1234)" or "3000 is free".</summary>
    public string Lookup(int port)
    {
        var hit = _all.FirstOrDefault(r => r.Port == port);
        return hit is null
            ? port.ToString(CultureInfo.InvariantCulture) + " is free"
            : port.ToString(CultureInfo.InvariantCulture) + " is taken by " + (hit.App ?? "an unknown process") + " (PID " + hit.Pid?.ToString(CultureInfo.InvariantCulture) + ")";
    }
}
