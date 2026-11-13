using System.Runtime.InteropServices;

namespace Tasktor.Shell;

/// <summary>The Win32 / DWM calls the switcher window needs (LibraryImport: Native AOT-safe, no runtime marshalling).</summary>
internal static unsafe partial class Native
{
    public const uint WsPopup = 0x80000000;
    public const uint WsExToolWindow = 0x00000080;
    public const uint WsExTopmost = 0x00000008;

    public const uint WmDestroy = 0x0002;
    public const uint WmActivate = 0x0006;
    public const uint WmPaint = 0x000F;
    public const uint WmEraseBkgnd = 0x0014;
    public const uint WmSysColorChange = 0x0015, WmSettingChange = 0x001A, WmThemeChanged = 0x031A;
    public const uint WmKeyDown = 0x0100;
    public const uint WmKeyUp = 0x0101;
    public const uint WmChar = 0x0102;
    public const uint WmSysKeyDown = 0x0104;
    public const uint WmSysKeyUp = 0x0105;
    public const uint WmSysChar = 0x0106;
    public const uint WmDpiChanged = 0x02E0;
    public const uint WmHotKey = 0x0312;
    public const uint WmApp = 0x8000;

    public const int VkBack = 0x08;
    public const int VkTab = 0x09;
    public const int VkReturn = 0x0D;
    public const int VkShift = 0x10;
    public const int VkControl = 0x11;
    public const int VkMenu = 0x12;
    public const int VkEscape = 0x1B;
    public const int VkLeft = 0x25;
    public const int VkUp = 0x26;
    public const int VkRight = 0x27;
    public const int VkDown = 0x28;

    public const int SwHide = 0;
    public const int SwShow = 5;
    public const int SwRestore = 9;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpShowWindow = 0x0040;
    public const uint ModNoRepeat = 0x4000;
    public const uint MonitorDefaultToNearest = 2;
    public const uint DtLeft = 0x0, DtVCenter = 0x4, DtSingleLine = 0x20, DtEndEllipsis = 0x8000, DtNoPrefix = 0x800;

    public const uint TnpRectDestination = 0x1, TnpOpacity = 0x4, TnpVisible = 0x8, TnpSourceClientAreaOnly = 0x10;

    public static readonly nint DpiAwarePerMonitorV2 = -4;

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left, Top, Right, Bottom;

        public Rect(int x, int y, int w, int h) => (Left, Top, Right, Bottom) = (x, y, x + w, y + h);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Size
    {
        public int Cx, Cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam, LParam;
        public uint Time;
        public Point Pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WndClassEx
    {
        public uint CbSize;
        public uint Style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> WndProc;
        public int ClsExtra, WndExtra;
        public nint Instance, Icon, Cursor, Background, MenuName, ClassName, IconSmall;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PaintStruct
    {
        public nint Hdc;
        public int Erase;
        public Rect Paint;
        public int Restore, IncUpdate;
        public fixed byte Reserved[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MonitorInfoW
    {
        public uint CbSize;
        public Rect Monitor, Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ThumbnailProperties
    {
        public uint Flags;
        public Rect Destination, Source;
        public byte Opacity;
        public int Visible, SourceClientAreaOnly;
    }

    public static uint Rgb(byte r, byte g, byte b) => r | ((uint)g << 8) | ((uint)b << 16);

    [LibraryImport("user32.dll")] public static partial ushort RegisterClassExW(WndClassEx* wc);
    [LibraryImport("user32.dll")] public static partial nint CreateWindowExW(uint exStyle, nint className, nint name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [LibraryImport("user32.dll")] public static partial int DestroyWindow(nint hwnd);
    [LibraryImport("user32.dll")] public static partial nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);
    [LibraryImport("user32.dll")] public static partial int GetMessageW(Msg* msg, nint hwnd, uint min, uint max);
    [LibraryImport("user32.dll")] public static partial int TranslateMessage(Msg* msg);
    [LibraryImport("user32.dll")] public static partial nint DispatchMessageW(Msg* msg);
    [LibraryImport("user32.dll")] public static partial int PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);
    [LibraryImport("user32.dll")] public static partial void PostQuitMessage(int code);
    [LibraryImport("user32.dll")] public static partial int RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk);
    [LibraryImport("user32.dll")] public static partial int UnregisterHotKey(nint hwnd, int id);
    [LibraryImport("user32.dll")] public static partial int ShowWindow(nint hwnd, int cmd);
    [LibraryImport("user32.dll")] public static partial int SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [LibraryImport("user32.dll")] public static partial int SetForegroundWindow(nint hwnd);
    [LibraryImport("user32.dll")] public static partial int IsIconic(nint hwnd);
    [LibraryImport("user32.dll")] public static partial short GetKeyState(int vk);
    [LibraryImport("user32.dll")] public static partial short GetAsyncKeyState(int vk);
    [LibraryImport("user32.dll")] public static partial int GetCursorPos(Point* p);
    [LibraryImport("user32.dll")] public static partial nint MonitorFromPoint(Point p, uint flags);
    [LibraryImport("user32.dll")] public static partial int GetMonitorInfoW(nint monitor, MonitorInfoW* info);
    [LibraryImport("user32.dll")] public static partial uint GetDpiForWindow(nint hwnd);
    [LibraryImport("user32.dll")] public static partial nint SetThreadDpiAwarenessContext(nint context);
    [LibraryImport("user32.dll")] public static partial uint GetSysColor(int index);
    [LibraryImport("user32.dll")] public static partial int InvalidateRect(nint hwnd, Rect* rect, int erase);
    [LibraryImport("user32.dll")] public static partial nint BeginPaint(nint hwnd, PaintStruct* ps);
    [LibraryImport("user32.dll")] public static partial int EndPaint(nint hwnd, PaintStruct* ps);
    [LibraryImport("user32.dll")] public static partial int FillRect(nint hdc, Rect* rect, nint brush);
    [LibraryImport("user32.dll")] public static partial int DrawTextW(nint hdc, char* text, int count, Rect* rect, uint format);
    [LibraryImport("user32.dll")] public static partial nint LoadCursorW(nint instance, nint name);
    [LibraryImport("kernel32.dll")] public static partial nint GetModuleHandleW(char* name);

    [LibraryImport("gdi32.dll")] public static partial nint CreateSolidBrush(uint color);
    [LibraryImport("gdi32.dll")] public static partial int DeleteObject(nint obj);
    [LibraryImport("gdi32.dll")] public static partial nint SelectObject(nint hdc, nint obj);
    [LibraryImport("gdi32.dll")] public static partial int SetBkMode(nint hdc, int mode);
    [LibraryImport("gdi32.dll")] public static partial uint SetTextColor(nint hdc, uint color);
    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);

    [LibraryImport("dwmapi.dll")] public static partial int DwmRegisterThumbnail(nint destination, nint source, nint* thumbnail);
    [LibraryImport("dwmapi.dll")] public static partial int DwmUnregisterThumbnail(nint thumbnail);
    [LibraryImport("dwmapi.dll")] public static partial int DwmUpdateThumbnailProperties(nint thumbnail, ThumbnailProperties* properties);
    [LibraryImport("dwmapi.dll")] public static partial int DwmQueryThumbnailSourceSize(nint thumbnail, Size* size);
}
