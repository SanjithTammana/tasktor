using System.Runtime.InteropServices;

namespace Tasktor.Capture.Security;

/// <summary>Only the non-personal WTSINFOEX fields needed by capture gating.</summary>
public readonly record struct WtsSessionHeader(uint Level, uint SessionId, uint ExpectedSessionId, int ConnectionState, int SessionFlags);

/// <summary>A one-shot query; implementations do not create a poller or a thread.</summary>
public interface IWtsSessionQuery
{
    bool TryQuery(out WtsSessionHeader header);
}

/// <summary>Fail-closed WTS state interpretation for supported Windows 11 sessions.</summary>
public sealed class WtsSessionSnapshot(IWtsSessionQuery query)
{
    public SessionAvailability Read()
    {
        try
        {
            if (!query.TryQuery(out var value) || value.Level != 1 || value.SessionId != value.ExpectedSessionId)
                return SessionAvailability.Unknown;
            if (value.ConnectionState == 4) return SessionAvailability.Disconnected; // WTSDisconnected
            if (value.ConnectionState != 0) return SessionAvailability.Unknown; // only WTSActive is known connected
            return value.SessionFlags switch
            {
                0 => SessionAvailability.Locked,
                1 => SessionAvailability.UnlockedConnected,
                _ => SessionAvailability.Unknown,
            };
        }
        catch
        {
            return SessionAvailability.Unknown;
        }
    }
}

/// <summary>
/// WTSINFOEXW prefix. Its Data union contains LARGE_INTEGER members and begins at byte 8 under Windows SDK ABI.
/// Only the first three Level1 fields are read; the user, domain and workstation names are never decoded.
/// </summary>
public static class WtsSessionPrefix
{
    public const int MinimumBytes = 20;

    public static bool TryParse(ReadOnlySpan<byte> bytes, uint expectedSessionId, out WtsSessionHeader header)
    {
        header = default;
        if (bytes.Length < MinimumBytes) return false;
        var native = MemoryMarshal.Read<NativePrefix>(bytes);
        header = new WtsSessionHeader(native.Level, native.SessionId, expectedSessionId, native.ConnectionState, native.SessionFlags);
        return true;
    }

    [StructLayout(LayoutKind.Explicit, Size = MinimumBytes)]
    private struct NativePrefix
    {
        [FieldOffset(0)] public uint Level;
        [FieldOffset(8)] public uint SessionId;
        [FieldOffset(12)] public int ConnectionState;
        [FieldOffset(16)] public int SessionFlags;
    }
}
