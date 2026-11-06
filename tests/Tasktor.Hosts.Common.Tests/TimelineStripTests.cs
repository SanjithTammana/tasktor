using System.Text.Json.Nodes;
using Tasktor.Hosts.Common.Shelf;

namespace Tasktor.Hosts.Common.Tests;

/// <summary>U4 amended D-G11: the shelf's timeline strip from the series pulls and the alerts.</summary>
public sealed class TimelineStripTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 10, 18, 0, TimeSpan.Zero);

    private static JsonNode Hour(IEnumerable<double> values) => new JsonObject { ["hour"] = new JsonArray([.. values.Select(v => (JsonNode?)JsonValue.Create(v))]) };

    private static Dictionary<string, JsonNode?> Pulls(Func<int, double> cpu, Func<int, double> ram, Func<int, double>? net = null)
    {
        var d = new Dictionary<string, JsonNode?>
        {
            ["system.cpu_pct"] = Hour(Enumerable.Range(0, 60).Select(i => cpu(i))),
            ["system.ram_used_pct"] = Hour(Enumerable.Range(0, 60).Select(i => ram(i))),
        };
        if (net is not null)
        {
            d["system.net_rx_bps"] = Hour(Enumerable.Range(0, 60).Select(i => net(i)));
        }

        return d;
    }

    [Fact]
    public void Rows_keep_the_series_order_and_the_highest_now_leads()
    {
        var m = TimelineStrip.Compose(Pulls(_ => 30, i => 60 + (i * 0.4)), null, Now, TimeZoneInfo.Utc);
        Assert.Equal(["CPU", "RAM"], m.Rows.Select(r => r.Label));
        Assert.True(m.Rows[1].Lead);
        Assert.False(m.Rows[0].Lead);
        Assert.Equal(0.3, m.Rows[0].Values[0], 9);
        Assert.Equal(("9:18", "10:33"), (m.Start, m.End));
    }

    [Fact]
    public void A_rising_lead_is_carried_past_now_and_labelled_when_it_fills_in_time()
    {
        // RAM climbs 1 % a minute and is at 90 % now: full in 10 minutes.
        var m = TimelineStrip.Compose(Pulls(_ => 20, i => 31 + i), null, Now, TimeZoneInfo.Utc);
        Assert.Equal(15, m.Forecast!.Values.Count);
        Assert.Equal(0.91, m.Forecast.Values[0], 6);
        Assert.Equal(1.0, m.Forecast.Values[^1], 6);
        Assert.Equal("full ≈10:28", m.Forecast.Label);
    }

    [Fact]
    public void A_flat_or_slow_lead_has_a_forecast_but_no_label()
    {
        Assert.Null(TimelineStrip.Compose(Pulls(_ => 20, _ => 70), null, Now, TimeZoneInfo.Utc).Forecast!.Label);
        Assert.Null(TimelineStrip.Compose(Pulls(_ => 20, i => 40 + (i * 0.1)), null, Now, TimeZoneInfo.Utc).Forecast!.Label);
        var cpu = TimelineStrip.Compose(Pulls(i => 31 + i, _ => 20), null, Now, TimeZoneInfo.Utc);
        Assert.Equal("100% ≈10:28", cpu.Forecast!.Label);
    }

    [Fact]
    public void Network_is_scaled_to_its_hour_peak_with_a_floor()
    {
        var busy = TimelineStrip.Compose(Pulls(_ => 10, _ => 10, i => i == 30 ? 5_000_000 : 1_000_000), null, Now, TimeZoneInfo.Utc);
        var net = busy.Rows.Single(r => r.Label == "Net");
        Assert.Equal(1.0, net.Values.Max(), 9);
        Assert.Equal(0.2, net.Values[0], 9);

        var idle = TimelineStrip.Compose(Pulls(_ => 10, _ => 10, _ => 1_000), null, Now, TimeZoneInfo.Utc);
        Assert.True(idle.Rows.Single(r => r.Label == "Net").Values.Max() < 0.01);
    }

    [Fact]
    public void Alerts_within_the_hour_are_written_on_the_strip_most_recent_three()
    {
        string At(int minutesAgo) => Now.AddMinutes(-minutesAgo).ToString("O");
        var alerts = new JsonArray(
            new JsonObject { ["rule"] = "old", ["since"] = At(90), ["text"] = "too old" },
            new JsonObject { ["rule"] = "a", ["since"] = At(50), ["text"] = "first" },
            new JsonObject { ["rule"] = "b", ["since"] = At(30), ["text"] = "RAM passed 80%" },
            new JsonObject { ["rule"] = "c", ["since"] = At(20) },
            new JsonObject { ["rule"] = "d", ["since"] = At(6), ["text"] = new string('x', 50) });
        var m = TimelineStrip.Compose(Pulls(_ => 10, _ => 10), alerts, Now, TimeZoneInfo.Utc);
        Assert.Equal(["RAM passed 80%", "c", new string('x', 31) + "…"], m.Events.Select(e => e.Label));
        Assert.Equal(0.5, m.Events[0].At, 6);
        Assert.Equal(0.9, m.Events[2].At, 6);
    }

    [Fact]
    public void The_users_and_rules_actions_are_written_on_the_strip_with_the_alerts()
    {
        var alerts = new JsonArray(new JsonObject { ["rule"] = "ram", ["since"] = Now.AddMinutes(-30).ToString("O"), ["text"] = "RAM passed 80%" });
        var actions = new JsonArray(
            new JsonObject { ["at"] = Now.AddMinutes(-12).ToString("O"), ["text"] = "Lower Chrome", ["origin"] = "shelf" },
            new JsonObject { ["at"] = Now.AddMinutes(-6).ToString("O"), ["text"] = "Pause train.py", ["origin"] = "rule" });
        var m = TimelineStrip.Compose(Pulls(_ => 10, _ => 10), alerts, Now, TimeZoneInfo.Utc, actions);
        Assert.Equal(["RAM passed 80%", "you: Lower Chrome", "a rule: Pause train.py"], m.Events.Select(e => e.Label));
    }

    [Fact]
    public void Missing_series_are_skipped_and_short_history_sits_at_the_right()
    {
        var pulled = new Dictionary<string, JsonNode?> { ["system.cpu_pct"] = Hour([10, 20, 30]), ["system.gpu_pct"] = null };
        var m = TimelineStrip.Compose(pulled, null, Now, TimeZoneInfo.Utc);
        Assert.Equal("CPU", Assert.Single(m.Rows).Label);
        Assert.Equal(1.0, TimelineStrip.At(2, 3), 9);
        Assert.Equal(57 / 59.0, TimelineStrip.At(0, 3), 9);

        var empty = TimelineStrip.Compose(new Dictionary<string, JsonNode?>(), null, Now, TimeZoneInfo.Utc);
        Assert.Empty(empty.Rows);
        Assert.Null(empty.Forecast);
    }
}
