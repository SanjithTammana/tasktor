using System.Globalization;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Snapshot;
using Tasktor.Core.Actions;
using Tasktor.Core.Rules;

namespace Tasktor.Core.Features.SystemMonitor;

/// <summary>
/// A8 battery (laptops only): percent, charging, time left and battery saver, event-driven (no polling between power
/// events). Unknown sentinels (255 %, −1 s) become null, never raw numbers. On a desktop (no system battery) the card and
/// the A8.2 rule are hidden. Unplugging raises <c>power.unplugged</c> for the default rule; plugging back in withdraws its toast.
/// </summary>
public sealed class Battery : Feature
{
    public const string FlagNoBattery = "battery.flag";

    private readonly IMetricSource _source;
    private readonly IEventSource<int>? _events;
    private readonly SystemTotals _totals;
    private readonly List<RuleEvent> _pending = [];
    private bool? _onAc;
    private string? _lastUnplug;
    private long _unplugs;

    public Battery(FeatureContext context, IMetricSource source, IEventSource<int>? events, SystemTotals totals)
        : base(context)
    {
        _source = source;
        _events = events;
        _totals = totals;
    }

    public override string Id => "battery";

    public override FeatureTier Tier => FeatureTier.Core;

    public bool HasBattery { get; private set; }

    public BatteryState? Current { get; private set; }

    /// <summary>Top app or job name for the A8.2 text, set by the composition (B's top job, else A2's top CPU group).</summary>
    public Func<string?>? BusyName { get; set; }

    protected override void OnEnable()
    {
        _events?.Start(_ => Context.Loop.Post(Read));
        _totals.Sampled += (_, isTick) =>
        {
            if (isTick)
            {
                Read();
            }
        };
        Context.Router.Register("battery.settings", _ => new PreparedAction
        {
            Summary = "Battery settings",
            Run = _ => new ActionResult { Action = "battery.settings", Status = Context.Launch("ms-settings:batterysaver") ? ActionStatus.Ok : ActionStatus.Failed },
        });
        Read();
    }

    protected override void OnDisable() => _events?.Stop();

    public void Read()
    {
        var frame = new MetricFrame();
        _source.Read(frame);
        Ingest(frame);
    }

    /// <summary>Apply one power reading: <c>battery.flag</c> (128 = none), <c>battery.pct</c> (255 = unknown), <c>battery.ac</c> (0 / 1 / 255), <c>battery.time_left_s</c> (−1 = unknown), <c>battery.saver</c>.</summary>
    public void Ingest(MetricFrame f)
    {
        ArgumentNullException.ThrowIfNull(f);
        var flag = f.TryGet(FlagNoBattery, out var fl) ? (int)fl : 128;
        HasBattery = flag != 128 && flag != 255;
        if (!HasBattery)
        {
            Current = null;
            Context.UpdateState(s => s with { Power = new PowerState { OnBattery = false, HasBattery = false }, System = (s.System ?? new SystemState()) with { Battery = null } });
            return;
        }

        var ac = f.TryGet("battery.ac", out var a) ? (int)a : 255;
        var pct = f.TryGet("battery.pct", out var p) && p is >= 0 and <= 100 ? p : (double?)null;
        var left = f.TryGet("battery.time_left_s", out var l) && l >= 0 && ac != 1 ? (long)l : (long?)null;
        var onAc = ac == 1;
        Current = new BatteryState { Pct = pct, Charging = (flag & 8) != 0, OnAc = onAc, TimeLeftS = left, Saver = f.TryGet("battery.saver", out var sv) && sv > 0 };
        var state = Current;
        Context.UpdateState(s => s with { Power = new PowerState { OnBattery = !onAc && ac != 255, HasBattery = true }, System = (s.System ?? new SystemState()) with { Battery = state } });

        if (_onAc == true && !onAc && ac != 255)
        {
            _unplugs++;
            _lastUnplug = "unplug-" + _unplugs.ToString(CultureInfo.InvariantCulture);
            _pending.Add(new RuleEvent
            {
                Name = "power.unplugged",
                Id = _lastUnplug,
                Fields = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["process"] = BusyName?.Invoke() ?? "an app",
                    ["time_left"] = left is { } s2 ? Time(s2) : "calculating",
                },
            });
            Context.RulesDirty();
        }
        else if (_onAc == false && onAc && _lastUnplug is not null)
        {
            Context.Notifier.Withdraw("unplugged-load:" + _lastUnplug); // plugging back in withdraws the toast
            Context.Router.Toasts.Withdraw("unplugged-load:" + _lastUnplug);
            _lastUnplug = null;
        }

        _onAc = ac == 255 ? _onAc : onAc;
    }

    public override void Contribute(RuleInputs inputs, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        inputs.State("has_battery", HasBattery);
        inputs.State("on_battery", HasBattery && Current is { OnAc: false });
        if (Current?.Pct is { } pct)
        {
            inputs.Metric("battery.pct", pct);
        }

        if (HasBattery)
        {
            inputs.Events.AddRange(_pending);
        }

        _pending.Clear();
    }

    public override IReadOnlyList<Card> Cards()
    {
        if (!HasBattery)
        {
            return [];
        }

        return
        [
            new Card
            {
                Id = "core.battery",
                Source = CardKit.Core,
                Title = "Battery",
                Icon = IconKey.Battery,
                Blocks =
                [
                    new MetricBlock { Label = "Battery", Value = Value.Binding("/state/system/battery/pct"), Unit = "%", Priority = 1 },
                    new BadgeBlock { Text = Value.Literal(Current is { Charging: true } ? "Charging" : Current is { OnAc: true } ? "Plugged in" : Current is { Saver: true } ? "On battery · saver on" : "On battery"), Priority = 2 },
                    new MetricBlock { Label = "Time left", Value = Value.Literal(Current?.TimeLeftS is { } t ? Time(t) : "—"), Priority = 3 },
                ],
                Actions = [new CardAction { Id = "settings", Label = "Battery settings", Action = "battery.settings" }],
                StaleAfterS = 600,
            },
        ];
    }

    private static string Time(long seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? (int)t.TotalHours + " h " + t.Minutes + " m" : t.Minutes + " min";
    }
}
