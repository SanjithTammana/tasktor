using Tasktor.Capture.Security;

namespace Tasktor.Capture.Tests;

public sealed class SecureDesktopTests
{
    [Theory]
    [InlineData("Default", "Default", true)]
    [InlineData("DEFAULT", "default", true)]
    [InlineData("Winlogon", "Default", false)]
    [InlineData("Default", "Winlogon", false)]
    [InlineData("Other", "Other", false)]
    [InlineData("", "Default", false)]
    [InlineData("Default", "", false)]
    public void Only_matching_default_desktops_are_accepted(string input, string current, bool allowed)
    {
        Assert.Equal(allowed, DesktopIdentityPolicy.IsUserDesktop(input, current));
    }

    [Theory]
    [InlineData(SessionAvailability.Unknown)]
    [InlineData(SessionAvailability.Locked)]
    [InlineData(SessionAvailability.Disconnected)]
    [InlineData((SessionAvailability)200)]
    public void Unverified_or_inactive_sessions_are_suppressed_without_querying_the_desktop(SessionAvailability session)
    {
        var probe = new FakeProbe(DesktopObservation.UserDesktop);
        var guard = new SecureDesktopGuard(probe);
        Assert.True(guard.ShouldSuppress(session));
        Assert.Equal(0, probe.Calls);
    }

    [Theory]
    [InlineData(DesktopObservation.Unknown, true)]
    [InlineData(DesktopObservation.OtherDesktop, true)]
    [InlineData(DesktopObservation.UserDesktop, false)]
    [InlineData((DesktopObservation)200, true)]
    public void Only_known_user_desktop_in_an_unlocked_connected_session_is_allowed(DesktopObservation observation, bool suppressed)
    {
        var guard = new SecureDesktopGuard(new FakeProbe(observation));
        Assert.Equal(suppressed, guard.ShouldSuppress(SessionAvailability.UnlockedConnected));
    }

    [Fact]
    public void An_adapter_exception_fails_closed()
    {
        var guard = new SecureDesktopGuard(new FakeProbe(DesktopObservation.Unknown, throws: true));
        Assert.True(guard.ShouldSuppress(SessionAvailability.UnlockedConnected));
    }

    [Theory]
    [InlineData(1u, 7u, 7u, 0, 1, SessionAvailability.UnlockedConnected)]
    [InlineData(1u, 7u, 7u, 0, 0, SessionAvailability.Locked)]
    [InlineData(1u, 7u, 7u, 4, 1, SessionAvailability.Disconnected)]
    [InlineData(1u, 7u, 7u, 1, 1, SessionAvailability.Unknown)]
    [InlineData(1u, 7u, 7u, 0, -1, SessionAvailability.Unknown)]
    [InlineData(1u, 7u, 7u, 0, 2, SessionAvailability.Unknown)]
    [InlineData(2u, 7u, 7u, 0, 1, SessionAvailability.Unknown)]
    [InlineData(1u, 7u, 8u, 0, 1, SessionAvailability.Unknown)]
    public void Snapshot_accepts_only_the_current_active_unlocked_session(uint level, uint actualId, uint expectedId, int connection, int flags, SessionAvailability expected)
    {
        var source = new FakeWts(true, new WtsSessionHeader(level, actualId, expectedId, connection, flags));
        Assert.Equal(expected, new WtsSessionSnapshot(source).Read());
    }

    [Fact]
    public void Failed_or_throwing_session_queries_fail_closed()
    {
        var valid = new WtsSessionHeader(1, 7, 7, 0, 1);
        Assert.Equal(SessionAvailability.Unknown, new WtsSessionSnapshot(new FakeWts(false, valid)).Read());
        Assert.Equal(SessionAvailability.Unknown, new WtsSessionSnapshot(new FakeWts(true, valid, true)).Read());
    }

    [Fact]
    public void Native_snapshot_prefix_uses_the_documented_eight_byte_aligned_union()
    {
        var bytes = new byte[20];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 7);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 1);
        Assert.False(WtsSessionPrefix.TryParse(bytes.AsSpan(0, 19), 7, out _));
        Assert.True(WtsSessionPrefix.TryParse(bytes, 7, out var header));
        Assert.Equal(SessionAvailability.UnlockedConnected, new WtsSessionSnapshot(new FakeWts(true, header)).Read());
    }

    private sealed class FakeWts(bool success, WtsSessionHeader header, bool throws = false) : IWtsSessionQuery
    {
        public bool TryQuery(out WtsSessionHeader result)
        {
            if (throws) throw new InvalidOperationException("synthetic failure");
            result = header;
            return success;
        }
    }
    private sealed class FakeProbe(DesktopObservation result, bool throws = false) : IDesktopProbe
    {
        public int Calls { get; private set; }
        public DesktopObservation Read()
        {
            Calls++;
            if (throws) throw new InvalidOperationException("synthetic failure");
            return result;
        }
    }
}

