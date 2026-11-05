using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Platform;
using Tasktor.Core.Features.Devices;
using Tasktor.Tests.Shared;

namespace Tasktor.Core.Tests;

/// <summary>U2 E8 (limited): read every 10 min only while the Module is on; low at 15%; silent devices say so.</summary>
public sealed class DeviceBatteriesTests : IDisposable
{
    private readonly FeatureRig _rig = new();

    public void Dispose() => _rig.Dispose();

    [Fact]
    public void Devices_are_read_under_a_10_minute_lease_and_low_ones_warn()
    {
        var source = new FakeTable<string, DeviceBattery>
        {
            Rows = [new("Mouse", new DeviceBattery("Mouse", 12)), new("Headset", new DeviceBattery("Headset", 80)), new("Keyboard", new DeviceBattery("Keyboard", null))],
        };
        var f = _rig.Add(new DeviceBatteries(_rig.Context, source));
        _rig.Advance(TimeSpan.FromMinutes(25), TimeSpan.FromMinutes(1));
        Assert.Equal(3, source.Reads.Count); // at the lease (the scheduler runs a first lease at once), then 10 and 20 minutes
        Assert.Equal(["Mouse", "Headset", "Keyboard"], f.Devices.Select(d => d.Name));
        var card = Assert.Single(f.Cards());
        Assert.Equal("\"warn\"", card.Severity!.ToString());
        Assert.Contains(card.Blocks, b => b is BadgeBlock bb && bb.Text.ToString().Contains("does not report", StringComparison.Ordinal));
        f.Disable();
        _rig.Advance(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(1));
        Assert.Equal(3, source.Reads.Count); // nothing more once the Module is off
    }
}
