using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using Tasktor.Contracts.Platform;

namespace Tasktor.Windows.Sources;

/// <summary>
/// A7: <c>GetExtendedTcpTable(TCP_TABLE_OWNER_PID_LISTENER)</c> for IPv4 and IPv6 (no admin needed), with the owner's exe
/// and whether it is the user's own process. Addresses are formatted here (no <c>System.Net</c> in Tasktor's processes).
/// </summary>
public sealed partial class ListenerSource(Win32ProcessTable processes) : ITableSource<string, ListenerEntry>
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidListener = 3;

    public string Name => "tcp-listeners";

    public unsafe void Read(ICollection<KeyValuePair<string, ListenerEntry>> into, ColumnSet columns)
    {
        ArgumentNullException.ThrowIfNull(into);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var owners = new Dictionary<uint, ProcessRow?>();
        foreach (var family in new[] { AfInet, AfInet6 })
        {
            uint size = 0;
            GetExtendedTcpTable(null, ref size, false, family, TcpTableOwnerPidListener, 0);
            if (size == 0)
            {
                continue;
            }

            var buf = new byte[size];
            fixed (byte* p = buf)
            {
                if (GetExtendedTcpTable(p, ref size, false, family, TcpTableOwnerPidListener, 0) != 0)
                {
                    continue;
                }

                var count = *(uint*)p;
                var span = new ReadOnlySpan<byte>(p + 4, (int)size - 4);
                var rowSize = family == AfInet ? 24 : 56;
                for (var i = 0; i < count && (i + 1) * rowSize <= span.Length; i++)
                {
                    var row = span.Slice(i * rowSize, rowSize);
                    string address;
                    int port;
                    uint pid;
                    if (family == AfInet)
                    {
                        // dwState, dwLocalAddr, dwLocalPort, dwRemoteAddr, dwRemotePort, dwOwningPid
                        address = string.Join(".", row.Slice(4, 4).ToArray());
                        port = BinaryPrimitives.ReadUInt16BigEndian(row.Slice(8, 2));
                        pid = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(20, 4));
                    }
                    else
                    {
                        // ucLocalAddr[16], dwLocalScopeId, dwLocalPort, ucRemoteAddr[16], dwRemoteScopeId, dwRemotePort, dwState, dwOwningPid
                        address = Ipv6(row[..16]);
                        port = BinaryPrimitives.ReadUInt16BigEndian(row.Slice(20, 2));
                        pid = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(52, 4));
                    }

                    if (!owners.TryGetValue(pid, out var owner))
                    {
                        owners[pid] = owner = pid is 0 or 4 ? null : processes.ReadOne(new ProcKey(pid, 0));
                    }

                    var entry = new ListenerEntry
                    {
                        Port = port,
                        Address = address,
                        Pid = pid,
                        Exe = pid == 4 ? "System" : owner?.Exe,
                        OwnUser = owner is { OwnUser: true, Elevated: false },
                        CreatedTicks = owner?.Key.CreatedTicks ?? 0,
                    };
                    into.Add(new KeyValuePair<string, ListenerEntry>(address + ":" + port.ToString(CultureInfo.InvariantCulture) + "/" + pid.ToString(CultureInfo.InvariantCulture), entry));
                }
            }
        }
    }

    /// <summary>RFC 5952-ish text for 16 bytes ("::", "::1", "fe80::1"); good enough for display and loopback checks.</summary>
    public static string Ipv6(ReadOnlySpan<byte> b)
    {
        Span<ushort> w = stackalloc ushort[8];
        for (var i = 0; i < 8; i++)
        {
            w[i] = BinaryPrimitives.ReadUInt16BigEndian(b.Slice(i * 2, 2));
        }

        int bestStart = -1, bestLen = 0;
        for (var i = 0; i < 8;)
        {
            if (w[i] != 0)
            {
                i++;
                continue;
            }

            var j = i;
            while (j < 8 && w[j] == 0)
            {
                j++;
            }

            if (j - i > bestLen && j - i > 1)
            {
                bestStart = i;
                bestLen = j - i;
            }

            i = j;
        }

        var parts = new List<string>();
        for (var i = 0; i < 8; i++)
        {
            if (i == bestStart)
            {
                parts.Add(i == 0 ? ":" : string.Empty);
                i += bestLen - 1;
                if (i == 7)
                {
                    parts.Add(string.Empty);
                }

                continue;
            }

            parts.Add(w[i].ToString("x", CultureInfo.InvariantCulture));
        }

        return string.Join(":", parts);
    }

    [LibraryImport("iphlpapi.dll")]
    private static unsafe partial uint GetExtendedTcpTable(byte* table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);
}
