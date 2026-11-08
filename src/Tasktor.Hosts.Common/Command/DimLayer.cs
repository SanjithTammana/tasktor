using System.Runtime.InteropServices;

namespace Tasktor.Hosts.Common.Command;

/// <summary>
/// G2 (D-G12): the dimmed desktop under the command line, a separate layered Win32 window (WinUI 3 has no transparent
/// full-monitor window): black, partly transparent, click-through (a click outside still reaches the desktop and
/// closes the command line), never activated, not in Alt+Tab, topmost and placed right under the command window.
/// Create, show and hide it on the UI thread.
/// </summary>
public sealed unsafe partial class DimLayer : IDisposable
{
    private const uint WsPopup = 0x80000000;
    private const uint WsExLayered = 0x00080000, WsExTransparent = 0x00000020, WsExToolWindow = 0x00000080, WsExNoActivate = 0x08000000, WsExTopmost = 0x00000008;
    private const uint LwaAlpha = 0x2, SwpNoActivate = 0x0010, SwpShowWindow = 0x0040;
    private const int SwHide = 0, SwShowNoActivate = 4, BlackBrush = 4, SpiGetHighContrast = 0x0042;
    private static readonly string ClassName = "TasktorDimLayer";
    private static bool s_registered;
    private nint _hwnd;

    /// <summary>Whether Windows' high-contrast mode is on (then no dim: <c>DimSpec</c>).</summary>
    public static bool HighContrast()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var hc = new HighContrastInfo { Size = (uint)sizeof(HighContrastInfo) };
        return SystemParametersInfoW(SpiGetHighContrast, hc.Size, &hc, 0) != 0 && (hc.Flags & 1) != 0;
    }

    /// <summary>Cover the rectangle (physical pixels) at <paramref name="alpha"/>, just under <paramref name="above"/>.</summary>
    public void Show(int x, int y, int width, int height, byte alpha, nint above)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (_hwnd == 0)
        {
            Register();
            fixed (char* cls = ClassName)
            {
                _hwnd = CreateWindowExW(WsExLayered | WsExTransparent | WsExToolWindow | WsExNoActivate | WsExTopmost, cls, null, WsPopup, x, y, width, height, 0, 0, GetModuleHandleW(null), 0);
            }

            if (_hwnd == 0) return;
        }

        SetLayeredWindowAttributes(_hwnd, 0, alpha, LwaAlpha);
        SetWindowPos(_hwnd, -1, x, y, width, height, SwpNoActivate); // topmost
        ShowWindow(_hwnd, SwShowNoActivate);
        if (above != 0) SetWindowPos(above, -1, 0, 0, 0, 0, SwpNoActivate | 0x0001 | 0x0002 | SwpShowWindow); // the command line over it
    }

    public void Hide()
    {
        if (_hwnd != 0) ShowWindow(_hwnd, SwHide);
    }

    public void Dispose()
    {
        if (_hwnd != 0) DestroyWindow(_hwnd);
        _hwnd = 0;
    }

    private static void Register()
    {
        if (s_registered) return;
        var user32 = NativeLibrary.Load("user32.dll");
        fixed (char* cls = ClassName)
        {
            var wc = new WndClassEx
            {
                Size = (uint)sizeof(WndClassEx),
                WndProc = NativeLibrary.GetExport(user32, "DefWindowProcW"),
                Instance = GetModuleHandleW(null),
                Background = GetStockObject(BlackBrush),
                ClassName = cls,
            };
            s_registered = RegisterClassExW(&wc) != 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WndClassEx
    {
        public uint Size, Style;
        public nint WndProc;
        public int ClsExtra, WndExtra;
        public nint Instance, Icon, Cursor, Background;
        public char* MenuName, ClassName;
        public nint IconSmall;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrastInfo
    {
        public uint Size, Flags;
        public nint DefaultScheme;
    }

    [LibraryImport("user32.dll")] private static partial ushort RegisterClassExW(WndClassEx* wc);
    [LibraryImport("user32.dll")] private static partial nint CreateWindowExW(uint exStyle, char* className, char* name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [LibraryImport("user32.dll")] private static partial int SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
    [LibraryImport("user32.dll")] private static partial int SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [LibraryImport("user32.dll")] private static partial int ShowWindow(nint hwnd, int cmd);
    [LibraryImport("user32.dll")] private static partial int DestroyWindow(nint hwnd);
    [LibraryImport("user32.dll")] private static partial int SystemParametersInfoW(uint action, uint param, void* value, uint winIni);
    [LibraryImport("gdi32.dll")] private static partial nint GetStockObject(int obj);
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint GetModuleHandleW(string? module);
}
