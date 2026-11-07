using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Platform;
using Tasktor.Core.Features.Network;
using Tasktor.Core.Rules;
using Tasktor.Core.Storage;
using Tasktor.Tests.Shared;

namespace Tasktor.Core.Tests;

/// <summary>U2 I14: status from OS events, drops counted and kept 30 days, captive portal and drop rules, no SSIDs.</summary>
public sealed class ConnectivityTests : IDisposable
{
    private readonly FeatureRig _rig = new();
    private readonly List<ExportTableSource> _exports = [];
    private readonly Connectivity _net;

    public ConnectivityTests()
    {
        var ctx = new Features.FeatureContext
        {
            Loop = _rig.Context.Loop,
            Scheduler = _rig.Context.Scheduler,
            Router = _rig.Context.Router,
            Log = _rig.Context.Log,
            Settings = _rig.Context.Settings,
            Store = _rig.Context.Store,
            UpdateState = _rig.Context.UpdateState,
            State = _rig.Context.State,
            RegisterPull = _rig.Context.RegisterPull,
            Launch = _rig.Context.Launch,
            OffLoop = _rig.Context.OffLoop,
            RulesDirty = _rig.Context.RulesDirty,
            RegisterExport = _exports.Add,
        };
        _net = _rig.Add(new Connectivity(ctx, null, null) { TopConnections = () => [("chrome.exe", 42), ("Teams.exe", 7)] });
    }

    public void Dispose() => _rig.Dispose();

    private void Net(NetStatus s, TimeSpan? after = null)
    {
        _rig.Clock.Advance(after ?? TimeSpan.FromSeconds(30));
        _net.Apply(new NetEvent(s, _rig.Clock.UtcNow));
        _rig.Loop.RunPending();
    }

    [Fact]
    public void Drops_are_transitions_to_offline_counted_per_hour_and_persisted()
    {
        Net(NetStatus.Online);
        Net(NetStatus.Offline);
        Net(NetStatus.Offline);
        Net(NetStatus.Online);
        Net(NetStatus.Offline);
        Assert.Equal(2, _net.DropsSince(_rig.Clock.UtcNow.AddHours(-1)));
        Assert.Equal("offline", _rig.State.Current.Connectivity!.Status);
        Assert.Equal(2, _net.HourlyLast24(_rig.Clock.UtcNow)[^1]);

        var again = new Connectivity(_rig.Context, null, null);
        _rig.Add(again);
        Assert.Equal(2, again.DropsThisWeek(_rig.Clock.UtcNow));
        Assert.DoesNotContain("ssid", File.ReadAllText(_rig.Store.Layout.Connectivity), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Captive_portal_toasts_a_sign_in_button_that_opens_the_os_trigger_page()
    {
        _rig.LoadRules();
        Net(NetStatus.CaptivePortal);
        _rig.Advance(TimeSpan.FromSeconds(11));
        _rig.Rules.Evaluate();
        _rig.Advance(TimeSpan.FromSeconds(11));
        var toast = Assert.Single(_rig.Rules.Evaluate(), o => o.Toast is not null).Toast!;
        Assert.Equal("Sign in to this network", toast.Title);
        var r = _rig.Act(toast.Buttons[0].Request);
        Assert.Equal(ActionStatus.Ok, r.Status);
        Assert.Equal(["http://www.msftconnecttest.com/redirect"], _rig.Launched);
    }

    [Fact]
    public void More_than_3_drops_in_10_minutes_warn_once_an_hour()
    {
        _rig.LoadRules();
        for (var i = 0; i < 4; i++)
        {
            Net(NetStatus.Online);
            Net(NetStatus.Offline);
        }

        var inputs = new RuleInputs();
        _net.Contribute(inputs, _rig.Clock.UtcNow);
        Assert.Equal(4, inputs.Metrics[new MetricKey("connectivity.drops_10m")]);
        Assert.Equal(4, inputs.Events.Count(e => e.Name == "net.dropped"));
        Assert.Contains(_rig.Rules.Evaluate(), o => o.Toast?.Title == "The network dropped 4 times in 10 minutes");
        Net(NetStatus.Online);
        Net(NetStatus.Offline);
        Assert.DoesNotContain(_rig.Rules.Evaluate(), o => o.Toast is not null);
    }

    [Fact]
    public void The_card_lists_connection_holders_as_connections_not_bytes_and_the_export_has_hourly_drops()
    {
        Net(NetStatus.Online);
        Net(NetStatus.Offline);
        var card = Assert.Single(_net.Cards());
        Assert.Contains(card.Blocks, b => b is Contracts.Cards.ListBlock l && l.Label == "Most connections (not bytes)" && l.Items.ToString().Contains("chrome.exe", StringComparison.Ordinal) && l.Items.ToString().Contains("42 connections", StringComparison.Ordinal));
        var table = Assert.Single(_exports, t => t.Name == "connectivity_hourly");
        var row = Assert.Single(table.Rows(null));
        Assert.Equal(1, row[1]);
    }
}
