using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tasktor.Contracts.Platform;

namespace Tasktor.Windows.Sources;

/// <summary>
/// U2 <c>ConnectivitySource</c> (I14): the OS's network connectivity hint (<c>NotifyNetworkConnectivityHintChange</c>, the
/// same data Network List Manager reports, without COM): None → offline, LocalAccess → local only, InternetAccess →
/// online, ConstrainedInternetAccess → captive portal. Event-driven, zero traffic. The Wi-Fi signal (<see cref="Read"/>)
/// is the RSSI of the connected interface (<c>WlanQueryInterface</c>, <c>wlan_intf_opcode_rssi</c>), read only under a
/// lease; the SSID and BSSID are never read (D-S5).
/// </summary>
public sealed unsafe partial class ConnectivitySource : IEventSource<NetEvent>, IMetricSource
{
    private static Action<NetEvent>? s_post;
    private nint _handle;

    public string Name => "connectivity";

    public void Start(Action<NetEvent> post)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        s_post = post;
        NotifyNetworkConnectivityHintChange(&Changed, 0, 1, out _handle);
    }

    public void Stop()
    {
        if (_handle != 0)
        {
            CancelMibChangeNotify2(_handle);
            _handle = 0;
        }

        s_post = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Hint
    {
        public int Level;
        public int Cost;
        public byte ApproachingDataLimit;
        public byte OverDataLimit;
        public byte Roaming;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void Changed(nint context, Hint hint)
    {
        var status = hint.Level switch
        {
            1 => NetStatus.Offline,
            2 => NetStatus.LocalOnly,
            3 => NetStatus.Online,
            4 => NetStatus.CaptivePortal,
            _ => NetStatus.Unknown,
        };
        s_post?.Invoke(new NetEvent(status, DateTimeOffset.UtcNow));
    }

    /// <summary><c>net.wifi_signal</c> 0–100 from the connected Wi-Fi interface's RSSI; absent without Wi-Fi.</summary>
    public void Read(MetricFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!OperatingSystem.IsWindows() || WlanOpenHandle(2, 0, out _, out var client) != 0)
        {
            return;
        }

        try
        {
            if (WlanEnumInterfaces(client, 0, out var list) != 0)
            {
                return;
            }

            try
            {
                var count = *(uint*)list;
                const int InfoSize = 532; // GUID (16) + WCHAR[256] (512) + WLAN_INTERFACE_STATE (4)
                for (var i = 0; i < count; i++)
                {
                    var info = (byte*)list + 8 + (i * InfoSize);
                    var state = *(int*)(info + 528);
                    if (state != 1)
                    {
                        continue; // wlan_interface_state_connected
                    }

                    var guid = *(Guid*)info;
                    if (WlanQueryInterface(client, &guid, 0x10000102, 0, out var dataSize, out var data, out _) == 0)
                    {
                        try
                        {
                            if (dataSize >= 4)
                            {
                                var rssi = *(int*)data;
                                frame.Set("net.wifi_signal", Math.Clamp((rssi + 100) * 2, 0, 100));
                            }
                        }
                        finally
                        {
                            WlanFreeMemory(data);
                        }
                    }

                    break;
                }
            }
            finally
            {
                WlanFreeMemory(list);
            }
        }
        finally
        {
            WlanCloseHandle(client, 0);
        }
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial int NotifyNetworkConnectivityHintChange(delegate* unmanaged[Stdcall]<nint, Hint, void> callback, nint context, byte initialNotification, out nint handle);

    [LibraryImport("iphlpapi.dll")]
    private static partial int CancelMibChangeNotify2(nint handle);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanOpenHandle(uint version, nint reserved, out uint negotiated, out nint client);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanCloseHandle(nint client, nint reserved);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanEnumInterfaces(nint client, nint reserved, out nint list);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanQueryInterface(nint client, Guid* iface, int opcode, nint reserved, out uint dataSize, out nint data, out int valueType);

    [LibraryImport("wlanapi.dll")]
    private static partial void WlanFreeMemory(nint memory);
}
