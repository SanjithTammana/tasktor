using System.Runtime.InteropServices;
using Tasktor.Contracts.Platform;

namespace Tasktor.Windows.Sources;

/// <summary>
/// U2 <c>BluetoothBatterySource</c> (E8, limited): for present Bluetooth devices (<c>BTHENUM\</c>, <c>BTHLE\</c>), the PnP
/// battery-level property Settings shows (<c>{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2</c>, OQ-S3) and the friendly name.
/// A device without the property is listed with no level. No admin. Read every 10 minutes, only under a lease.
/// </summary>
public sealed unsafe partial class BluetoothBatterySource : ITableSource<string, DeviceBattery>
{
    private const uint DigcfPresent = 0x2;
    private const uint DigcfAllClasses = 0x4;
    private static readonly DevPropKey BatteryLevel = new(new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), 2);
    private static readonly DevPropKey FriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    private static readonly DevPropKey InstanceId = new(new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), 256);

    public string Name => "bt-batteries";

    public void Read(ICollection<KeyValuePair<string, DeviceBattery>> into, ColumnSet columns)
    {
        ArgumentNullException.ThrowIfNull(into);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var set = SetupDiGetClassDevsW(null, null, 0, DigcfPresent | DigcfAllClasses);
        if (set == -1)
        {
            return;
        }

        try
        {
            var info = new SpDevInfoData { CbSize = (uint)sizeof(SpDevInfoData) };
            var byName = new Dictionary<string, DeviceBattery>(StringComparer.OrdinalIgnoreCase);
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref info) != 0; i++)
            {
                var id = StringProp(set, ref info, InstanceId);
                if (id is null || !(id.StartsWith(@"BTHENUM\", StringComparison.OrdinalIgnoreCase) || id.StartsWith(@"BTHLE\", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var name = StringProp(set, ref info, FriendlyName);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var level = ByteProp(set, ref info, BatteryLevel);

                // One device appears as several PnP nodes: keep the one that reports a level.
                if (!byName.TryGetValue(name, out var have) || (have.Pct is null && level is not null))
                {
                    byName[name] = new DeviceBattery(name, level is { } l && l <= 100 ? l : null);
                }
            }

            foreach (var (name, d) in byName)
            {
                into.Add(new(name, d));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static string? StringProp(nint set, ref SpDevInfoData info, DevPropKey key)
    {
        var buf = new byte[1024];
        fixed (byte* p = buf)
        {
            var k = key;
            if (SetupDiGetDevicePropertyW(set, ref info, &k, out var type, p, (uint)buf.Length, out var size, 0) == 0 || type != 0x12)
            {
                return null; // DEVPROP_TYPE_STRING
            }

            return new string((char*)p, 0, Math.Max(0, ((int)size / 2) - 1));
        }
    }

    private static int? ByteProp(nint set, ref SpDevInfoData info, DevPropKey key)
    {
        byte value = 0;
        var k = key;
        return SetupDiGetDevicePropertyW(set, ref info, &k, out var type, &value, 1, out _, 0) != 0 && type == 0x3 ? value : null; // DEVPROP_TYPE_BYTE
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DevPropKey(Guid fmtid, uint pid)
    {
        public readonly Guid Fmtid = fmtid;
        public readonly uint Pid = pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public nint Reserved;
    }

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint SetupDiGetClassDevsW(Guid* classGuid, string? enumerator, nint parent, uint flags);

    [LibraryImport("setupapi.dll")]
    private static partial int SetupDiEnumDeviceInfo(nint set, uint index, ref SpDevInfoData info);

    [LibraryImport("setupapi.dll")]
    private static partial int SetupDiGetDevicePropertyW(nint set, ref SpDevInfoData info, DevPropKey* key, out uint type, byte* buffer, uint size, out uint required, uint flags);

    [LibraryImport("setupapi.dll")]
    private static partial int SetupDiDestroyDeviceInfoList(nint set);
}
