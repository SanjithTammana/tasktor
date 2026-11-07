using System.Runtime.InteropServices;

namespace Tasktor.Capture.Security;

/// <summary>
/// Queries only the current local session. The result is a snapshot, not notification registration; callers must
/// invalidate on session or power changes and requery before capture. Windows 11 is the supported OS target.
/// </summary>
public sealed unsafe partial class Win32WtsSessionQuery : IWtsSessionQuery
{
    private const uint CurrentSession = 0xffffffff;
    private const int WtsSessionInfoEx = 25;

    public bool TryQuery(out WtsSessionHeader header)
    {
        header = default;
        if (!OperatingSystem.IsWindows()) return false;
        nint buffer = 0;
        try
        {
            uint sessionId;
            if (ProcessIdToSessionId(GetCurrentProcessId(), &sessionId) == 0) return false;
            uint bytes = 0;
            if (WTSQuerySessionInformationW(0, CurrentSession, WtsSessionInfoEx, &buffer, &bytes) == 0
                || buffer == 0 || bytes < WtsSessionPrefix.MinimumBytes) return false;
            return WtsSessionPrefix.TryParse(new ReadOnlySpan<byte>((void*)buffer, WtsSessionPrefix.MinimumBytes), sessionId, out header);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (buffer != 0) WTSFreeMemory(buffer);
        }
    }

    [LibraryImport("wtsapi32.dll")] private static partial int WTSQuerySessionInformationW(nint server, uint session, int infoClass, nint* buffer, uint* bytesReturned);
    [LibraryImport("wtsapi32.dll")] private static partial void WTSFreeMemory(nint buffer);
    [LibraryImport("kernel32.dll")] private static partial uint GetCurrentProcessId();
    [LibraryImport("kernel32.dll")] private static partial int ProcessIdToSessionId(uint processId, uint* sessionId);
}
