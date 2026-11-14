using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Tasktor.Hosts.Common.Pipe;

/// <summary>
/// Who is on the other end of a server pipe: the client's process id and full image path, from the pipe handle itself
/// (<c>GetNamedPipeClientProcessId</c>), never from anything the client says. <see cref="PipeOptions.CurrentUserOnly"/>
/// keeps other users out; this tells Tasktor's own programs from any other program the same user runs. Windows only: on
/// other platforms nothing is known (null).
/// </summary>
public static partial class PipeCaller
{
    private const uint ProcessQueryLimited = 0x1000;

    /// <summary>The client's process id, or null when it cannot be read.</summary>
    public static uint? ProcessId(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        return OperatingSystem.IsWindows() && GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) ? pid : null;
    }

    /// <summary>The full path of the client's exe, or null when it cannot be read.</summary>
    public static string? ImagePath(NamedPipeServerStream pipe) => ProcessId(pipe) is { } pid ? ImagePath(pid) : null;

    public static unsafe string? ImagePath(uint pid)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var h = OpenProcess(ProcessQueryLimited, false, pid);
        if (h == 0)
        {
            return null;
        }

        try
        {
            var buffer = stackalloc char[1024];
            var size = 1024;
            return QueryFullProcessImageNameW(h, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    /// <summary>True when the client is exactly the exe at <paramref name="expectedPath"/> (a full path, compared without
    /// regard to case, as Windows paths are).</summary>
    public static bool Is(NamedPipeServerStream pipe, string expectedPath) =>
        ImagePath(pipe) is { } path && string.Equals(Path.GetFullPath(path), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageNameW(nint process, uint flags, char* buffer, ref int size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
