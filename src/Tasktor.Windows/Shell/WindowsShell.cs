using System.Runtime.InteropServices;

namespace Tasktor.Windows.Shell;

/// <summary>Small shell helpers for U2 features: bring a process tree's window up (I1 Show) and the Recycle Bin size (I7).</summary>
public static unsafe partial class WindowsShell
{
    private static uint[]? s_chain;
    private static nint s_found;

    /// <summary>
    /// Restore and bring forward the first visible top-level window owned by a process in <paramref name="chain"/> (the
    /// agent, then its ancestors: a console agent's window belongs to its terminal). Windows may refuse to move the
    /// foreground; the window then flashes instead. Returns false when no window was found.
    /// </summary>
    public static bool ShowWindowOf(IReadOnlyList<uint> chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        if (!OperatingSystem.IsWindows() || chain.Count == 0)
        {
            return false;
        }

        foreach (var pid in chain)
        {
            s_chain = [pid];
            s_found = 0;
            EnumWindows(&Visit, 0);
            if (s_found != 0)
            {
                if (IsIconic(s_found) != 0)
                {
                    ShowWindow(s_found, 9); // SW_RESTORE
                }

                if (SetForegroundWindow(s_found) == 0)
                {
                    var info = new FlashInfo { CbSize = (uint)sizeof(FlashInfo), Hwnd = s_found, Flags = 0x3 | 0xC, Count = 3 };
                    FlashWindowEx(ref info);
                }

                return true;
            }
        }

        return false;
    }

    [UnmanagedCallersOnly]
    private static int Visit(nint hwnd, nint lParam)
    {
        if (IsWindowVisible(hwnd) == 0 || GetWindow(hwnd, 4) != 0)
        {
            return 1; // hidden, or owned by another window
        }

        GetWindowThreadProcessId(hwnd, out var pid);
        if (s_chain is { } c && c.Contains(pid))
        {
            s_found = hwnd;
            return 0;
        }

        return 1;
    }

    /// <summary>Bytes in the Recycle Bins of all drives, or null.</summary>
    public static long? RecycleBinBytes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var info = new RecycleInfo { CbSize = (uint)sizeof(RecycleInfo) };
        return SHQueryRecycleBinW(null, ref info) == 0 ? info.Size : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint CbSize;
        public nint Hwnd;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct RecycleInfo
    {
        public uint CbSize;
        public long Size;
        public long Items;
    }

    [LibraryImport("user32.dll")]
    private static partial int EnumWindows(delegate* unmanaged<nint, nint, int> callback, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial int IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindow(nint hwnd, uint cmd);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    [LibraryImport("user32.dll")]
    private static partial int IsIconic(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int ShowWindow(nint hwnd, int cmd);

    [LibraryImport("user32.dll")]
    private static partial int SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int FlashWindowEx(ref FlashInfo info);

    [LibraryImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHQueryRecycleBinW(string? root, ref RecycleInfo info);
}
