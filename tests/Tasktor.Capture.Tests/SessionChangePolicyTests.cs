using Tasktor.Capture.Security;

namespace Tasktor.Capture.Tests;

public sealed class SessionChangePolicyTests
{
    [Fact]
    public void Snapshot_is_not_trusted_without_successful_registration()
    {
        var policy = new SessionChangePolicy();
        policy.ApplySnapshot(policy.Generation, SessionAvailability.UnlockedConnected);
        Assert.Equal(SessionAvailability.Unknown, policy.Current);
        policy.Register(false);
        policy.ApplySnapshot(policy.Generation, SessionAvailability.UnlockedConnected);
        Assert.Equal(SessionAvailability.Unknown, policy.Current);
        policy.Register(true);
        Assert.Equal(SessionAvailability.Unknown, policy.Current);
        policy.ApplySnapshot(policy.Generation, SessionAvailability.UnlockedConnected);
        Assert.Equal(SessionAvailability.UnlockedConnected, policy.Current);
    }

    [Theory]
    [InlineData(0x1u)] // console connect
    [InlineData(0x2u)] // console disconnect
    [InlineData(0x3u)] // remote connect
    [InlineData(0x4u)] // remote disconnect
    [InlineData(0x5u)] // logon
    [InlineData(0x6u)] // logoff
    [InlineData(0x7u)] // lock
    [InlineData(0x8u)] // unlock
    [InlineData(0xFu)] // desktop ready
    [InlineData(0xFEu)] // unknown cannot imply safe
    public void Every_session_change_requires_a_fresh_snapshot(uint notification)
    {
        var policy = Ready();
        var oldTicket = policy.Generation;
        policy.SessionChanged(notification);
        Assert.Equal(SessionAvailability.Unknown, policy.Current);
        policy.ApplySnapshot(oldTicket, SessionAvailability.UnlockedConnected);
        Assert.Equal(SessionAvailability.Unknown, policy.Current);
        policy.ApplySnapshot(policy.Generation, SessionAvailability.UnlockedConnected);
        Assert.Equal(SessionAvailability.UnlockedConnected, policy.Current);
    }

    [Theory]
    [InlineData(0x4u)] // suspend
    [InlineData(0x7u)] // resume suspend
    [InlineData(0x12u)] // resume automatic
    [InlineData(0xFFFFu)] // unknown cannot imply safe
    public void Every_power_notification_requires_a_fresh_snapshot(uint notification)
    {
        var policy = Ready();
        var ticket = policy.Generation;
        policy.PowerChanged(notification);
        Assert.Equal(SessionAvailability.Unknown, policy.Current);
        policy.ApplySnapshot(ticket, SessionAvailability.UnlockedConnected);
        Assert.Equal(SessionAvailability.Unknown, policy.Current);
    }

    [Fact]
    public void Failed_registration_and_re_registration_invalidate_old_snapshots()
    {
        var policy = Ready();
        var oldTicket = policy.Generation;
        policy.Register(false);
        Assert.Equal(SessionAvailability.Unknown, policy.Current);
        policy.ApplySnapshot(policy.Generation, SessionAvailability.UnlockedConnected);
        Assert.Equal(SessionAvailability.Unknown, policy.Current);
        policy.Register(true);
        policy.ApplySnapshot(oldTicket, SessionAvailability.UnlockedConnected);
        Assert.Equal(SessionAvailability.Unknown, policy.Current);
        policy.ApplySnapshot(policy.Generation, SessionAvailability.Disconnected);
        Assert.Equal(SessionAvailability.Disconnected, policy.Current);
    }

    [Fact]
    public void Unknown_snapshot_values_do_not_become_safe()
    {
        var policy = Ready();
        policy.ApplySnapshot(policy.Generation, (SessionAvailability)200);
        Assert.Equal(SessionAvailability.Unknown, policy.Current);
    }

    private static SessionChangePolicy Ready()
    {
        var policy = new SessionChangePolicy();
        policy.Register(true);
        policy.ApplySnapshot(policy.Generation, SessionAvailability.UnlockedConnected);
        return policy;
    }
}
