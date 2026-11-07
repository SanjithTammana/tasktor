namespace Tasktor.Capture.Security;

/// <summary>
/// Logic-thread-only session state. Native callbacks post notifications; they never mutate this policy directly.
/// Every WTS or power notification invalidates state. A query started before the latest invalidation cannot restore it.
/// </summary>
public sealed class SessionChangePolicy
{
    private bool _registered;

    /// <summary>Snapshot token; pass this value back to <see cref="ApplySnapshot"/> with the corresponding result.</summary>
    public long Generation { get; private set; }

    /// <summary>Untrusted until notification registration and a fresh valid snapshot have both succeeded.</summary>
    public SessionAvailability Current { get; private set; } = SessionAvailability.Unknown;

    /// <summary>Called after registration is attempted; changing registration always invalidates pending snapshots.</summary>
    public void Register(bool succeeded)
    {
        _registered = succeeded;
        Invalidate();
    }

    /// <summary>
    /// Called for a current-session WM_WTSSESSION_CHANGE. Unknown codes also invalidate: they never imply safety.
    /// The native message owner must filter messages to its own session before posting them.
    /// </summary>
    public void SessionChanged(uint notification) => Invalidate();

    /// <summary>Called for WM_POWERBROADCAST. Even an unknown notification never implies safety.</summary>
    public void PowerChanged(uint notification) => Invalidate();

    /// <summary>Accept only a current-generation snapshot after successful notification registration.</summary>
    public void ApplySnapshot(long generation, SessionAvailability availability)
    {
        if (!_registered || generation != Generation) return;
        Current = availability switch
        {
            SessionAvailability.UnlockedConnected => SessionAvailability.UnlockedConnected,
            SessionAvailability.Locked => SessionAvailability.Locked,
            SessionAvailability.Disconnected => SessionAvailability.Disconnected,
            _ => SessionAvailability.Unknown,
        };
    }

    private void Invalidate()
    {
        Current = SessionAvailability.Unknown;
        Generation = unchecked(Generation + 1);
    }
}
