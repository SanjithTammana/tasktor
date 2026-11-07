namespace Tasktor.Contracts.Platform;

/// <summary>I14 connectivity levels (the OS's connectivity hint): captive portal = constrained internet access.</summary>
public enum NetStatus : byte
{
    Unknown,
    Offline,
    LocalOnly,
    Online,
    CaptivePortal,
}

/// <summary>
/// U2 <c>ConnectivitySource</c> (I14): a connectivity change. Never the SSID, BSSID or location (D-S5); Wi-Fi signal is
/// read as a metric (<c>net.wifi_signal</c>, 0–100) only while something leases it.
/// </summary>
public sealed record NetEvent(NetStatus Status, DateTimeOffset At);
