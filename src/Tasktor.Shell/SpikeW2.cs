using System.Globalization;
using Tasktor.Windows.Shell;

namespace Tasktor.Shell;

/// <summary>
/// Spike W2 (U3 D-W7): run <c>tasktor-shell --spike-w2 &lt;pid&gt;</c> on desktop 1 while that process (for example
/// Notepad) has its window on desktop 2. It reads whether the window is on this desktop, asks
/// <c>IVirtualDesktopManager::MoveWindowToDesktop</c> to bring it here (the desktop of a window this process creates), and
/// reads again. Prints pass / fail only; exit 0 when the window moved. "Bring here" stays off until this passes.
/// </summary>
internal static partial class SpikeW2
{
    public static int Run(string? pidText, TextWriter output)
    {
        if (!uint.TryParse(pidText, NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
        {
            output.WriteLine("spike-w2: give the process id of a window on another desktop");
            return 2;
        }

        var target = Win32Windows.Enumerate(include: p => p == pid).FirstOrDefault(w => w.Visible && !w.ToolWindow && w.Owner == 0);
        if (target is null)
        {
            output.WriteLine("spike-w2: no top-level window for that process");
            return 2;
        }

        var before = VirtualDesktopInfo.IsOnCurrent(target.Hwnd);
        using var own = new SpikeWindow();
        var moved = VirtualDesktopInfo.TryBringHere(target.Hwnd, own.Hwnd);
        Thread.Sleep(500);
        var after = VirtualDesktopInfo.IsOnCurrent(target.Hwnd);
        output.WriteLine($"on this desktop before: {Show(before)}; move call: {(moved ? "accepted" : "refused")}; on this desktop after: {Show(after)}");
        var pass = before == false && moved && after == true;
        output.WriteLine(pass ? "W2: pass (bring here works for other processes' windows)" : before == true ? "W2: inconclusive (the window was already on this desktop)" : "W2: FAIL (keep bring here off)");
        return pass ? 0 : 1;
    }

    private static string Show(bool? v) => v switch { true => "yes", false => "no", null => "unknown" };

    /// <summary>A hidden window of this process: its desktop is the current one.</summary>
    private sealed partial class SpikeWindow : IDisposable
    {
        public SpikeWindow()
        {
            Hwnd = CreateWindowExW(0, "Static", "tasktor W2 spike", 0, 0, 0, 1, 1, 0, 0, 0, 0);
        }

        public long Hwnd { get; }

        public void Dispose()
        {
            if (Hwnd != 0) DestroyWindow((nint)Hwnd);
        }

        [System.Runtime.InteropServices.LibraryImport("user32.dll", StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf16)]
        private static partial nint CreateWindowExW(uint exStyle, string className, string name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);

        [System.Runtime.InteropServices.LibraryImport("user32.dll")]
        private static partial int DestroyWindow(nint hwnd);
    }
}
