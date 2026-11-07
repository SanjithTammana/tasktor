using System.Runtime.InteropServices;

namespace Tasktor.Capture.Security;

/// <summary>Only an invalidation category; no notification asserts that a session is safe.</summary>
public enum SessionNoticeKind : byte { Unknown = 0, SessionChange = 1, PowerChange = 2 }

/// <summary>The native notification code is preserved, including unknown values.</summary>
public readonly record struct SessionNotice(SessionNoticeKind Kind, ulong NativeCode);

/// <summary>The window procedure must fail capture closed when posting fails.</summary>
public enum NotificationDispatch : byte { Ignored = 0, Posted = 1, Failed = 2 }

/// <summary>Native boundary; the implementing functions must be called on the owner window thread.</summary>
public interface ISessionNotificationNative
{
    bool TryCurrentSession(out uint sessionId);
    bool Register(nint owner);
    bool Unregister(nint owner);
}

/// <summary>
/// Registers an existing window for current-session WTS changes. This class owns no window, hook, timer, or thread.
/// Register before starting a session snapshot. The owner forwards window messages with <see cref="Handle"/> and must
/// fail closed if registration, posting, or unregistration fails. The supplied post must only enqueue and must not block.
/// Power messages are observed without reading their lParam pointer; the owner remains responsible for the Win32 result.
/// Call all methods on the owner window thread and unregister before destroying the window.
/// </summary>
public sealed class Win32SessionNotifications(ISessionNotificationNative native, Func<SessionNotice, bool> post)
{
    public const uint WmSessionChange = 0x02b1;
    public const uint WmPowerBroadcast = 0x0218;
    private nint _owner;
    private uint _session;
    private bool _registered;
    private bool _acceptPosts;

    /// <summary>False means the caller must leave session state unknown and suppress capture.</summary>
    public bool TryRegister(nint owner)
    {
        if (owner == 0 || _registered) return false;
        try
        {
            if (!native.TryCurrentSession(out var session) || !native.Register(owner)) return false;
            _owner = owner;
            _session = session;
            _registered = true;
            _acceptPosts = true;
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Post an invalidation only for the registered owner; WTS messages from other sessions are ignored. Unknown event
    /// codes are preserved and must still invalidate. A failed post must immediately fail capture closed.
    /// </summary>
    public NotificationDispatch Handle(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (!_registered || !_acceptPosts || hwnd != _owner) return NotificationDispatch.Ignored;
        SessionNoticeKind kind;
        if (message == WmSessionChange)
        {
            if (unchecked((nuint)lParam) != _session) return NotificationDispatch.Ignored;
            kind = SessionNoticeKind.SessionChange;
        }
        else if (message == WmPowerBroadcast)
        {
            kind = SessionNoticeKind.PowerChange;
        }
        else return NotificationDispatch.Ignored;
        try
        {
            return post(new SessionNotice(kind, (ulong)wParam)) ? NotificationDispatch.Posted : NotificationDispatch.Failed;
        }
        catch { return NotificationDispatch.Failed; }
    }

    /// <summary>On failure remain invalid and retain the registration handle so teardown can retry.</summary>
    public bool TryUnregister()
    {
        _acceptPosts = false;
        if (!_registered) return true;
        try
        {
            if (!native.Unregister(_owner)) return false;
            _registered = false;
            _owner = 0;
            return true;
        }
        catch { return false; }
    }
}

/// <summary>Windows implementation; registration targets only the session attached to the supplied window.</summary>
public sealed unsafe partial class WindowsSessionNotificationNative : ISessionNotificationNative
{
    public bool TryCurrentSession(out uint sessionId)
    {
        sessionId = 0;
        if (!OperatingSystem.IsWindows()) return false;
        uint id;
        if (ProcessIdToSessionId(GetCurrentProcessId(), &id) == 0) return false;
        sessionId = id;
        return true;
    }

    public bool Register(nint owner) => OperatingSystem.IsWindows() && WTSRegisterSessionNotification(owner, 0) != 0;
    public bool Unregister(nint owner) => OperatingSystem.IsWindows() && WTSUnRegisterSessionNotification(owner) != 0;

    [LibraryImport("kernel32.dll")] private static partial uint GetCurrentProcessId();
    [LibraryImport("kernel32.dll")] private static partial int ProcessIdToSessionId(uint processId, uint* sessionId);
    [LibraryImport("wtsapi32.dll")] private static partial int WTSRegisterSessionNotification(nint hwnd, uint flags);
    [LibraryImport("wtsapi32.dll")] private static partial int WTSUnRegisterSessionNotification(nint hwnd);
}
