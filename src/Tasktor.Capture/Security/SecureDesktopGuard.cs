namespace Tasktor.Capture.Security;

/// <summary>Session state supplied by the existing session-notification source. Unknown is never treated as active.</summary>
public enum SessionAvailability : byte
{
    Unknown = 0,
    UnlockedConnected = 1,
    Locked = 2,
    Disconnected = 3,
}

/// <summary>Whether the input desktop is proven to be the capture thread's normal interactive desktop.</summary>
public enum DesktopObservation : byte
{
    Unknown = 0,
    UserDesktop = 1,
    OtherDesktop = 2,
}

/// <summary>Reads desktop identity without exposing desktop names to callers.</summary>
public interface IDesktopProbe
{
    DesktopObservation Read();
}

/// <summary>Conservative identity rule for an interactive Windows desktop.</summary>
public static class DesktopIdentityPolicy
{
    public static bool IsUserDesktop(ReadOnlySpan<char> input, ReadOnlySpan<char> current) =>
        input.Equals("Default", StringComparison.OrdinalIgnoreCase)
        && current.Equals("Default", StringComparison.OrdinalIgnoreCase)
        && input.Equals(current, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Logic-thread decision; no timers, hooks, logs, or session-state assumptions.</summary>
public sealed class SecureDesktopGuard(IDesktopProbe probe)
{
    public bool ShouldSuppress(SessionAvailability session)
    {
        if (session != SessionAvailability.UnlockedConnected) return true;
        try
        {
            return probe.Read() != DesktopObservation.UserDesktop;
        }
        catch
        {
            return true;
        }
    }
}
