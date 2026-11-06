using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tasktor.Hosts.Common.Render;
using Tasktor.Hosts.Common.Theme;

namespace Tasktor.Capture.Indicator;

/// <summary>Capture-owned tray ring and click-through physical two-pixel monitor-top lines.</summary>
public sealed unsafe partial class Win32RecordingIndicator : IRecordingIndicator
{
    private const uint WmClose = 0x0010, WmDestroy = 0x0002, WmDisplayChange = 0x007e, WmDpiChanged = 0x02e0;
    private const uint WmNcHitTest = 0x0084, WmAppStop = 0x8001;
    private const uint WsPopup = 0x80000000, WsExTopmost = 0x8, WsExTransparent = 0x20;
    private const uint WsExToolWindow = 0x80, WsExLayered = 0x80000, WsExNoActivate = 0x08000000;
    private const uint SwpNoActivate = 0x10, SwpShowWindow = 0x40;
    private const uint NimAdd = 0, NimDelete = 2, NifIcon = 2, NifTip = 4;
    [ThreadStatic] private static Win32RecordingIndicator? t_current;
    private readonly ManualResetEventSlim _ready = new();
    private readonly List<nint> _edges = [];
    private List<Rect>? _monitors;
    private Thread? _thread;
    private nint _owner, _brush, _icon, _module;
    private uint _taskbarCreated;
    private bool _started, _iconAdded, _rebuilding, _failed;
    private volatile bool _stopRequested;
    private const string WindowClass = "TasktorCaptureIndicator";

    /// <summary>Raised if an indicator cannot be maintained after startup; the host must stop capture.</summary>
    public event Action? Failed;

    public bool Start()
    {
        if (!OperatingSystem.IsWindows() || _thread is not null) return false;
        _thread = new Thread(Run) { IsBackground = true, Name = "tasktor-capture-indicator" };
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(10)))
        {
            Dispose();
            return false;
        }
        return _started;
    }

    public void Dispose()
    {
        var thread = _thread;
        if (thread is null) return;
        _stopRequested = true;
        if (_owner != 0) PostMessageW(_owner, WmAppStop, 0, 0);
        if (Thread.CurrentThread != thread && !thread.Join(TimeSpan.FromSeconds(10)))
            throw new InvalidOperationException("Indicator thread did not stop.");
        _thread = null;
    }

    private void Run()
    {
        t_current = this;
        if (SetThreadDpiAwarenessContext(-4) == 0)
        {
            _ready.Set();
            t_current = null;
            return;
        }
        _module = GetModuleHandleW(null);
        _brush = CreateSolidBrush(0x001c2bc4);
        fixed (char* cls = WindowClass)
        {
            var wc = new WndClassEx { CbSize = (uint)sizeof(WndClassEx), WndProc = &WndProc, Instance = _module, Background = _brush, ClassName = (nint)cls };
            var registered = _brush != 0 && RegisterClassExW(&wc) != 0;
            try
            {
                if (!registered) return;
                _owner = CreateWindowExW(WsExToolWindow, (nint)cls, 0, WsPopup, 0, 0, 0, 0, 0, 0, _module, 0);
                if (_owner == 0) return;
                fixed (char* name = "TaskbarCreated") _taskbarCreated = RegisterWindowMessageW(name);
                _icon = MakeIcon();
                if (_icon == 0 || !AddIcon() || !RebuildEdges() || _stopRequested) return;
                _started = true;
                _ready.Set();
                Msg msg;
                while (GetMessageW(&msg, 0, 0, 0) > 0)
                {
                    TranslateMessage(&msg);
                    DispatchMessageW(&msg);
                }
                if (!_failed) Fail(); // unexpected loop exit is also an indicator failure
            }
            finally
            {
                _ready.Set();
                RemoveIcon();
                foreach (var edge in _edges) DestroyWindow(edge);
                _edges.Clear();
                if (_owner != 0) DestroyWindow(_owner);
                _owner = 0;
                if (_icon != 0) DestroyIcon(_icon);
                if (registered) UnregisterClassW((nint)cls, _module);
                if (_brush != 0) DeleteObject(_brush);
                t_current = null;
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        var self = t_current;
        if (self is null) return DefWindowProcW(hwnd, msg, wParam, lParam);
        try
        {
            if (msg == WmNcHitTest) return -1;
            if (hwnd == self._owner && msg == WmAppStop)
            {
                self._failed = true; // intentional shutdown
                PostQuitMessage(0);
                return 0;
            }
            if (hwnd == self._owner && msg == self._taskbarCreated && self._taskbarCreated != 0)
            {
                self._iconAdded = false;
                if (!self.AddIcon()) self.Fail();
                return 0;
            }
            if ((msg == WmDisplayChange || msg == WmDpiChanged) && !self._rebuilding && self._started)
            {
                if (!self.RebuildEdges()) self.Fail();
                return 0;
            }
            if (msg == WmDestroy)
            {
                if (self._started && !self._stopRequested && !self._rebuilding && (hwnd == self._owner || self._edges.Contains(hwnd))) self.Fail();
                return 0;
            }
            if (msg == WmClose) return 0;
        }
        catch { self.Fail(); }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void Fail()
    {
        if (_failed) return;
        _failed = true;
        Failed?.Invoke();
    }

    private bool RebuildEdges()
    {
        _rebuilding = true;
        var next = new List<nint>();
        try
        {
            _monitors = [];
            if (EnumDisplayMonitors(0, 0, &MonitorCallback, 0) == 0 || _monitors.Count == 0) return false;
            fixed (char* cls = WindowClass)
            {
                foreach (var r in _monitors)
                {
                    if (r.Right <= r.Left || r.Bottom <= r.Top) return false;
                    var edge = CreateWindowExW(WsExLayered | WsExTopmost | WsExToolWindow | WsExNoActivate | WsExTransparent,
                        (nint)cls, 0, WsPopup, r.Left, r.Top, r.Right - r.Left, 2, 0, 0, _module, 0);
                    if (edge == 0) return false;
                    next.Add(edge);
                    if (SetLayeredWindowAttributes(edge, 0, 255, 2) == 0 || SetWindowPos(edge, -1, r.Left, r.Top, r.Right - r.Left, 2, SwpNoActivate | SwpShowWindow) == 0) return false;
                }
            }
            foreach (var edge in _edges) DestroyWindow(edge);
            _edges.Clear();
            _edges.AddRange(next);
            next.Clear();
            return true;
        }
        finally
        {
            foreach (var edge in next) DestroyWindow(edge);
            _monitors = null;
            _rebuilding = false;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int MonitorCallback(nint monitor, nint dc, Rect* bounds, nint data)
    {
        try { t_current?._monitors?.Add(*bounds); return 1; }
        catch { return 0; }
    }

    private bool AddIcon()
    {
        var data = new NotifyIconData { CbSize = (uint)sizeof(NotifyIconData), Hwnd = _owner, Id = 1, Flags = NifIcon | NifTip, Icon = _icon };
        const string tip = "Recording typing and screen text";
        for (var i = 0; i < tip.Length; i++) data.Tip[i] = tip[i];
        _iconAdded = Shell_NotifyIconW(NimAdd, &data) != 0;
        return _iconAdded;
    }

    private void RemoveIcon()
    {
        if (!_iconAdded) return;
        var data = new NotifyIconData { CbSize = (uint)sizeof(NotifyIconData), Hwnd = _owner, Id = 1 };
        Shell_NotifyIconW(NimDelete, &data);
        _iconAdded = false;
    }

    private static nint MakeIcon()
    {
        const int size = 32;
        var pixels = TrayArt.Icon(TrayIconState.Recording, false, size, ThemeTokens.Dark);
        var header = new BitmapInfoHeader { Size = (uint)sizeof(BitmapInfoHeader), Width = size, Height = -size, Planes = 1, BitCount = 32 };
        nint bits;
        var color = CreateDIBSection(0, &header, 0, &bits, 0, 0);
        var mask = CreateBitmap(size, size, 1, 1, null);
        try
        {
            if (color == 0 || bits == 0 || mask == 0) return 0;
            fixed (uint* p = pixels) Buffer.MemoryCopy(p, (void*)bits, size * size * 4, size * size * 4);
            var info = new IconInfo { Icon = 1, Mask = mask, Color = color };
            return CreateIconIndirect(&info);
        }
        finally { if (color != 0) DeleteObject(color); if (mask != 0) DeleteObject(mask); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Msg { public nint Hwnd; public uint Message; public nint WParam, LParam; public uint Time; public int X, Y; public uint Private; }
    [StructLayout(LayoutKind.Sequential)] private struct WndClassEx { public uint CbSize, Style; public delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint> WndProc; public int ClsExtra, WndExtra; public nint Instance, Icon, Cursor, Background, MenuName, ClassName, IconSmall; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NotifyIconData { public uint CbSize; public nint Hwnd; public uint Id, Flags, CallbackMessage; public nint Icon; public fixed char Tip[128]; public uint State, StateMask; public fixed char Info[256]; public uint Version; public fixed char InfoTitle[64]; public uint InfoFlags; public Guid Item; public nint BalloonIcon; }
    [StructLayout(LayoutKind.Sequential)] private struct IconInfo { public int Icon; public uint HotspotX, HotspotY; public nint Mask, Color; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant, Colors; }
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint GetModuleHandleW(string? name);
    [LibraryImport("user32.dll")] private static partial nint SetThreadDpiAwarenessContext(nint context);
    [LibraryImport("user32.dll")] private static partial ushort RegisterClassExW(WndClassEx* wc);
    [LibraryImport("user32.dll")] private static partial int UnregisterClassW(nint name, nint instance);
    [LibraryImport("user32.dll")] private static partial nint CreateWindowExW(uint ex, nint cls, nint name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [LibraryImport("user32.dll")] private static partial int DestroyWindow(nint hwnd);
    [LibraryImport("user32.dll")] private static partial nint DefWindowProcW(nint hwnd, uint msg, nint wp, nint lp);
    [LibraryImport("user32.dll")] private static partial int GetMessageW(Msg* msg, nint hwnd, uint min, uint max);
    [LibraryImport("user32.dll")] private static partial int TranslateMessage(Msg* msg);
    [LibraryImport("user32.dll")] private static partial nint DispatchMessageW(Msg* msg);
    [LibraryImport("user32.dll")] private static partial int PostMessageW(nint hwnd, uint msg, nint wp, nint lp);
    [LibraryImport("user32.dll")] private static partial void PostQuitMessage(int code);
    [LibraryImport("user32.dll")] private static partial uint RegisterWindowMessageW(char* name);
    [LibraryImport("user32.dll")] private static partial int EnumDisplayMonitors(nint dc, nint clip, delegate* unmanaged[Stdcall]<nint, nint, Rect*, nint, int> callback, nint data);
    [LibraryImport("user32.dll")] private static partial int SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
    [LibraryImport("user32.dll")] private static partial int SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [LibraryImport("shell32.dll")] private static partial int Shell_NotifyIconW(uint msg, NotifyIconData* data);
    [LibraryImport("user32.dll")] private static partial nint CreateIconIndirect(IconInfo* info);
    [LibraryImport("user32.dll")] private static partial int DestroyIcon(nint icon);
    [LibraryImport("gdi32.dll")] private static partial nint CreateSolidBrush(uint color);
    [LibraryImport("gdi32.dll")] private static partial int DeleteObject(nint obj);
    [LibraryImport("gdi32.dll")] private static partial nint CreateBitmap(int width, int height, uint planes, uint depth, void* bits);
    [LibraryImport("gdi32.dll")] private static partial nint CreateDIBSection(nint dc, BitmapInfoHeader* info, uint usage, nint* bits, nint section, uint offset);
}




