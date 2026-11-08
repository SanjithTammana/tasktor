using Tasktor.Capture.Security;
namespace Tasktor.Capture.Tests;

public sealed class SessionNotificationTests
{
    [Fact]
    public void Registration_requires_valid_owner_session_and_native_success()
    {
        var native = new FakeNative();
        var notices = new List<SessionNotice>();
        var adapter = new Win32SessionNotifications(native, n => { notices.Add(n); return true; });
        Assert.False(adapter.TryRegister(0));
        Assert.Equal(0, native.RegisterCalls);
        native.QueryOk = false;
        Assert.False(adapter.TryRegister(42));
        Assert.Equal(0, native.RegisterCalls);
        native.QueryOk = true;
        native.RegisterOk = false;
        Assert.False(adapter.TryRegister(42));
        Assert.Equal(NotificationDispatch.Ignored, adapter.Handle(42, Win32SessionNotifications.WmSessionChange, 7, 12));
        native.RegisterOk = true;
        Assert.True(adapter.TryRegister(42));
        Assert.False(adapter.TryRegister(42));
        Assert.Empty(notices);
    }

    [Fact]
    public void Session_notifications_are_filtered_by_id_and_unknown_codes_are_preserved()
    {
        var native = new FakeNative { Session = 12 };
        var notices = new List<SessionNotice>();
        var adapter = new Win32SessionNotifications(native, n => { notices.Add(n); return true; });
        Assert.True(adapter.TryRegister(42));
        Assert.Equal(NotificationDispatch.Ignored, adapter.Handle(43, Win32SessionNotifications.WmSessionChange, 7, 12));
        Assert.Equal(NotificationDispatch.Ignored, adapter.Handle(42, Win32SessionNotifications.WmSessionChange, 7, 13));
        Assert.Equal(NotificationDispatch.Posted, adapter.Handle(42, Win32SessionNotifications.WmSessionChange, 7, 12));
        Assert.Equal(NotificationDispatch.Posted, adapter.Handle(42, Win32SessionNotifications.WmSessionChange, 0xffff, 12));
        Assert.Equal(new SessionNotice(SessionNoticeKind.SessionChange, 7), notices[0]);
        Assert.Equal(new SessionNotice(SessionNoticeKind.SessionChange, 0xffff), notices[1]);
    }

    [Fact]
    public void Power_events_always_post_and_other_messages_do_not()
    {
        var notices = new List<SessionNotice>();
        var adapter = new Win32SessionNotifications(new FakeNative(), n => { notices.Add(n); return true; });
        Assert.True(adapter.TryRegister(42));
        Assert.Equal(NotificationDispatch.Ignored, adapter.Handle(42, 0x1234, 7, 0));
        Assert.Equal(NotificationDispatch.Posted, adapter.Handle(42, Win32SessionNotifications.WmPowerBroadcast, 0xffff, 0));
        Assert.Equal(new SessionNotice(SessionNoticeKind.PowerChange, 0xffff), Assert.Single(notices));
    }

    [Fact]
    public void Failed_or_throwing_post_is_reported_without_throwing_from_window_callback()
    {
        var failed = new Win32SessionNotifications(new FakeNative(), _ => false);
        Assert.True(failed.TryRegister(42));
        Assert.Equal(NotificationDispatch.Failed, failed.Handle(42, Win32SessionNotifications.WmPowerBroadcast, 4, 0));
        var throwing = new Win32SessionNotifications(new FakeNative(), _ => throw new InvalidOperationException());
        Assert.True(throwing.TryRegister(42));
        Assert.Equal(NotificationDispatch.Failed, throwing.Handle(42, Win32SessionNotifications.WmPowerBroadcast, 4, 0));
    }

    [Fact]
    public void Stop_unregisters_once_and_failure_is_explicit()
    {
        var native = new FakeNative { UnregisterOk = false };
        var adapter = new Win32SessionNotifications(native, _ => true);
        Assert.True(adapter.TryRegister(42));
        Assert.False(adapter.TryUnregister());
        Assert.Equal(NotificationDispatch.Ignored, adapter.Handle(42, Win32SessionNotifications.WmPowerBroadcast, 4, 0));
        Assert.Equal(1, native.UnregisterCalls);
        native.UnregisterOk = true;
        Assert.True(adapter.TryUnregister());
        Assert.Equal(2, native.UnregisterCalls);
        Assert.True(adapter.TryUnregister());
        Assert.Equal(2, native.UnregisterCalls);
    }

    [Fact]
    public void Native_exceptions_never_make_registration_or_unregistration_succeed()
    {
        var native = new FakeNative { Throw = true };
        var adapter = new Win32SessionNotifications(native, _ => true);
        Assert.False(adapter.TryRegister(42));
        native.Throw = false;
        Assert.True(adapter.TryRegister(42));
        native.Throw = true;
        Assert.False(adapter.TryUnregister());
    }

    private sealed class FakeNative : ISessionNotificationNative
    {
        public uint Session { get; set; } = 12;
        public bool QueryOk { get; set; } = true;
        public bool RegisterOk { get; set; } = true;
        public bool UnregisterOk { get; set; } = true;
        public bool Throw { get; set; }
        public int RegisterCalls { get; private set; }
        public int UnregisterCalls { get; private set; }
        public bool TryCurrentSession(out uint id) { if (Throw) throw new InvalidOperationException(); id = Session; return QueryOk; }
        public bool Register(nint owner) { RegisterCalls++; if (Throw) throw new InvalidOperationException(); return RegisterOk; }
        public bool Unregister(nint owner) { UnregisterCalls++; if (Throw) throw new InvalidOperationException(); return UnregisterOk; }
    }
}


