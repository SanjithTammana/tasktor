using System.Runtime.InteropServices;

namespace Tasktor.Capture.Security;

/// <summary>
/// Reads the current input desktop with least privilege. Must run on the capture logic thread attached to the same
/// desktop as the hook. A separate session signal is required: a disconnected session can still report Default.
/// Desktop names never leave this adapter. Any failed read is Unknown and must suppress capture.
/// </summary>
public sealed unsafe partial class Win32DesktopProbe : IDesktopProbe
{
    private const uint DesktopReadObjects = 0x0001;
    private const int UoiName = 2, UoiIo = 6;
    private const int MaxNameChars = 256;

    public DesktopObservation Read()
    {
        if (!OperatingSystem.IsWindows()) return DesktopObservation.Unknown;
        var input = OpenInputDesktop(0, 0, DesktopReadObjects);
        if (input == 0) return DesktopObservation.Unknown;
        try
        {
            var current = GetThreadDesktop(GetCurrentThreadId());
            if (current == 0) return DesktopObservation.Unknown;
            Span<char> inputName = stackalloc char[MaxNameChars];
            Span<char> currentName = stackalloc char[MaxNameChars];
            if (!TryReadName(input, inputName, out var inputLength) || !TryReadName(current, currentName, out var currentLength))
                return DesktopObservation.Unknown;
            if (!DesktopIdentityPolicy.IsUserDesktop(inputName[..inputLength], currentName[..currentLength]))
                return DesktopObservation.OtherDesktop;

            int receivingInput = 0;
            uint needed = 0;
            if (GetUserObjectInformationW(current, UoiIo, &receivingInput, sizeof(int), &needed) == 0 || needed > sizeof(int))
                return DesktopObservation.Unknown;
            return receivingInput != 0 ? DesktopObservation.UserDesktop : DesktopObservation.OtherDesktop;
        }
        finally
        {
            CloseDesktop(input); // GetThreadDesktop is borrowed and is not closed.
        }
    }

    private static bool TryReadName(nint desktop, Span<char> buffer, out int length)
    {
        buffer.Clear();
        uint needed = 0;
        fixed (char* chars = buffer)
        {
            if (GetUserObjectInformationW(desktop, UoiName, chars, checked((uint)(buffer.Length * sizeof(char))), &needed) == 0)
            {
                length = 0;
                return false;
            }
        }

        length = buffer.IndexOf('\0');
        return length > 0 && needed > 0 && needed <= buffer.Length * sizeof(char);
    }

    [LibraryImport("user32.dll")] private static partial nint OpenInputDesktop(uint flags, int inherit, uint desiredAccess);
    [LibraryImport("user32.dll")] private static partial int CloseDesktop(nint desktop);
    [LibraryImport("user32.dll")] private static partial nint GetThreadDesktop(uint threadId);
    [LibraryImport("user32.dll")] private static partial int GetUserObjectInformationW(nint handle, int index, void* info, uint length, uint* needed);
    [LibraryImport("kernel32.dll")] private static partial uint GetCurrentThreadId();
}
