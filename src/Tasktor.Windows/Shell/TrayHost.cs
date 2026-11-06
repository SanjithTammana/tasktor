using System.Runtime.InteropServices;
using Tasktor.Contracts.Actions;

namespace Tasktor.Windows.Shell;

/// <summary>A rectangle in physical pixels.</summary>
public readonly record struct TrayRect(int X, int Y, int Width, int Height);

/// <summary>The primary monitor as the tray thread sees it (per-monitor DPI aware), and the taskbar's theme.</summary>
public sealed record TrayDisplay(TrayRect Monitor, TrayRect Work, double Scale, int IconSize, bool LightTaskbar);

/// <summary>One row of the tray menu: an action to send (origin tray), a disabled line, a submenu, or a separator.</summary>
public sealed record TrayMenuItem(string Label, ActionRequest? Action = null, bool Enabled = true, IReadOnlyList<TrayMenuItem>? Children = null)
{
    public static TrayMenuItem Separator { get; } = new("-", Enabled: false);

    public bool IsSeparator => ReferenceEquals(this, Separator);
}

/// <summary>The edge strip's bounds and premultiplied BGRA pixels (<c>0xAARRGGBB</c>, rows top first).</summary>
public sealed record TrayEdgeImage(TrayRect Bounds, uint[] Pixels);

/// <summary>Everything the tray shows. The agent composes it; the host only draws it.</summary>
public sealed record TrayView(uint[] Icon, int IconSize, string Tooltip, IReadOnlyList<TrayMenuItem> Menu, TrayEdgeImage? Edge);

/// <summary>
/// U4 D-G4 / amended D-G10, D-G15: the agent's tray icon and the edge strip on one UI thread with its own message loop
/// (per-monitor DPI aware). It draws what <see cref="Update"/> hands it and raises events on that thread; the agent
/// posts them to its logic loop. Hover or click on the icon or the edge raises <see cref="OpenRequested"/>; the
/// right-click menu is Windows' own (<c>TrackPopupMenuEx</c>). No hooks, no polling, no network.
/// </summary>
public sealed unsafe partial class TrayHost : IDisposable
{
    private const uint WmApp = 0x8000;
    private const uint WmTrayCallback = WmApp + 1;
    private const uint WmApply = WmApp + 2;
    private const uint WmStop = WmApp + 3;
    private const uint WmNull = 0x0000;
    private const uint WmDestroy = 0x0002;
    private const uint WmSettingChange = 0x001A;
    private const uint WmContextMenu = 0x007B;
    private const uint WmDisplayChange = 0x007E;
    private const uint WmMouseMove = 0x0200;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmMouseHover = 0x02A1;
    private const uint WmDpiChanged = 0x02E0;
    private const uint NinSelect = 0x0400;
    private const uint NinKeySelect = 0x0401;
    private const uint NinPopupOpen = 0x0406;
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;
    private const uint NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4;
    private const uint WsPopup = 0x80000000;
    private const uint WsExTopmost = 0x8, WsExToolWindow = 0x80, WsExLayered = 0x80000, WsExNoActivate = 0x8000000;
    private static readonly nint DpiAwarePerMonitorV2 = -4;

    private static TrayHost? s_host;
    private readonly object _gate = new();
    private TrayView? _pending;
    private TrayView? _shown;
    private Thread? _thread;
    private nint _owner;
    private nint _edge;
    private nint _icon;
    private uint _taskbarCreated;
    private bool _iconAdded;
    private bool _hoverTracking;
    private List<TrayMenuItem> _menuIds = [];

    /// <summary>The display changed (and once at start). Raised on the tray thread.</summary>
    public event Action<TrayDisplay>? DisplayChanged;

    /// <summary>Hover or click on the icon or the edge. Raised on the tray thread.</summary>
    public event Action? OpenRequested;

    /// <summary>A menu row with an action was chosen. Raised on the tray thread.</summary>
    public event Action<TrayMenuItem>? MenuChosen;

    /// <summary>Starts the tray thread. False when not on Windows, another host runs, or the windows can't be created.</summary>
    public bool Start()
    {
        if (!OperatingSystem.IsWindows() || Interlocked.CompareExchange(ref s_host, this, null) is not null)
        {
            return false;
        }

        var ok = false;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready, value => ok = value)) { IsBackground = true, Name = "tasktor-tray" };
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
        return ok;
    }

    /// <summary>Show <paramref name="view"/>; safe from any thread (the latest one wins).</summary>
    public void Update(TrayView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        lock (_gate)
        {
            _pending = view;
        }

        if (_owner != 0)
        {
            PostMessageW(_owner, WmApply, 0, 0);
        }
    }

    public void Dispose()
    {
        if (_owner != 0)
        {
            PostMessageW(_owner, WmStop, 0, 0);
        }

        _thread?.Join(TimeSpan.FromSeconds(2));
        Interlocked.CompareExchange(ref s_host, null, this);
    }

    private void Run(ManualResetEventSlim ready, Action<bool> started)
    {
        SetThreadDpiAwarenessContext(DpiAwarePerMonitorV2);
        var module = GetModuleHandleW(null);
        var className = Marshal.StringToHGlobalUni("TasktorTray");
        try
        {
            var wc = new WndClassEx { CbSize = (uint)sizeof(WndClassEx), WndProc = &WndProc, Instance = module, ClassName = className, Cursor = LoadCursorW(0, 32512) };
            RegisterClassExW(&wc);
            _owner = CreateWindowExW(WsExToolWindow, className, 0, WsPopup, 0, 0, 0, 0, 0, 0, module, 0);
            _edge = CreateWindowExW(WsExLayered | WsExToolWindow | WsExTopmost | WsExNoActivate, className, 0, WsPopup, 0, 0, 1, 1, _owner, 0, module, 0);
            if (_owner == 0 || _edge == 0)
            {
                started(false);
                ready.Set();
                return;
            }

            fixed (char* name = "TaskbarCreated")
            {
                _taskbarCreated = RegisterWindowMessageW(name);
            }

            started(true);
            ready.Set();
            RaiseDisplay();
            Msg msg;
            while (GetMessageW(&msg, 0, 0, 0) > 0)
            {
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
        }
        finally
        {
            ready.Set();
            RemoveIcon();
            if (_edge != 0)
            {
                DestroyWindow(_edge);
            }

            if (_owner != 0)
            {
                DestroyWindow(_owner);
            }

            _owner = _edge = 0;
            UnregisterClassW(className, module);
            Marshal.FreeHGlobal(className);
        }
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        var host = s_host;
        return host is not null && host.Handle(hwnd, msg, wParam, lParam, out var result) ? result : DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private bool Handle(nint hwnd, uint msg, nint wParam, nint lParam, out nint result)
    {
        result = 0;
        if (hwnd == _owner)
        {
            switch (msg)
            {
                case WmApply:
                    Apply();
                    return true;
                case WmStop:
                    RemoveIcon();
                    PostQuitMessage(0);
                    return true;
                case WmTrayCallback:
                    switch ((uint)(lParam & 0xFFFF))
                    {
                        case NinSelect or NinKeySelect or NinPopupOpen:
                            OpenRequested?.Invoke();
                            break;
                        case WmContextMenu:
                            ShowMenu((short)(wParam & 0xFFFF), (short)((wParam >> 16) & 0xFFFF));
                            break;
                        default:
                            break; // other notifications (balloons, popup close) are not used
                    }

                    return true;
                case WmDisplayChange or WmSettingChange:
                    RaiseDisplay();
                    return false;
                default:
                    if (msg == _taskbarCreated && _taskbarCreated != 0)
                    {
                        _iconAdded = false; // Explorer restarted: add the icon again
                        _shown = null;
                        Apply();
                        return true;
                    }

                    return false;
            }
        }

        if (hwnd == _edge)
        {
            switch (msg)
            {
                case WmMouseMove when !_hoverTracking:
                    var tme = new TrackMouseEventData { CbSize = (uint)sizeof(TrackMouseEventData), Flags = 0x1 | 0x2, Track = _edge, HoverTime = 200 }; // TME_HOVER | TME_LEAVE
                    _hoverTracking = TrackMouseEvent(&tme) != 0;
                    return true;
                case WmMouseHover:
                    _hoverTracking = false;
                    OpenRequested?.Invoke();
                    return true;
                case 0x02A3: // WM_MOUSELEAVE
                    _hoverTracking = false;
                    return true;
                case WmLButtonUp:
                    OpenRequested?.Invoke();
                    return true;
                case WmDpiChanged:
                    RaiseDisplay();
                    return true;
                default:
                    return false;
            }
        }

        return false;
    }

    private void Apply()
    {
        TrayView? view;
        lock (_gate)
        {
            view = _pending;
        }

        if (view is null || _owner == 0)
        {
            return;
        }

        var iconChanged = _shown is null || !ReferenceEquals(_shown.Icon, view.Icon);
        if (iconChanged)
        {
            var next = CreateIcon(view.Icon, view.IconSize);
            if (next != 0)
            {
                var old = _icon;
                _icon = next;
                SetIcon(view.Tooltip);
                if (old != 0)
                {
                    DestroyIcon(old);
                }
            }
        }
        else if (_shown!.Tooltip != view.Tooltip)
        {
            SetIcon(view.Tooltip);
        }

        if (view.Edge is { } edge && (_shown?.Edge is null || !ReferenceEquals(_shown.Edge.Pixels, edge.Pixels) || _shown.Edge.Bounds != edge.Bounds))
        {
            DrawEdge(edge);
        }
        else if (view.Edge is null && _shown?.Edge is not null)
        {
            ShowWindow(_edge, 0);
        }

        _shown = view;
    }

    private void SetIcon(string tooltip)
    {
        var data = new NotifyIconData { CbSize = (uint)sizeof(NotifyIconData), Hwnd = _owner, Id = 1, Flags = NifMessage | NifIcon | NifTip, CallbackMessage = WmTrayCallback, Icon = _icon };
        var tip = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        for (var i = 0; i < tip.Length; i++)
        {
            data.Tip[i] = tip[i];
        }

        if (!_iconAdded)
        {
            _iconAdded = Shell_NotifyIconW(NimAdd, &data) != 0;
            data.Version = 4; // NOTIFYICON_VERSION_4: hover arrives as NIN_POPUPOPEN (no NIF_SHOWTIP)
            Shell_NotifyIconW(NimSetVersion, &data);
        }
        else
        {
            Shell_NotifyIconW(NimModify, &data);
        }
    }

    private void RemoveIcon()
    {
        if (_iconAdded)
        {
            var data = new NotifyIconData { CbSize = (uint)sizeof(NotifyIconData), Hwnd = _owner, Id = 1 };
            Shell_NotifyIconW(NimDelete, &data);
            _iconAdded = false;
        }

        if (_icon != 0)
        {
            DestroyIcon(_icon);
            _icon = 0;
        }
    }

    private void ShowMenu(int x, int y)
    {
        if (_shown is not { Menu.Count: > 0 } view)
        {
            return;
        }

        _menuIds = [];
        var menu = BuildMenu(view.Menu);
        try
        {
            SetForegroundWindow(_owner); // required so the menu closes when the user clicks elsewhere
            var id = TrackPopupMenuEx(menu, 0x100 | 0x2 | 0x20, x, y, _owner, 0); // TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN
            PostMessageW(_owner, WmNull, 0, 0);
            if (id > 0 && id <= _menuIds.Count && _menuIds[id - 1].Action is not null)
            {
                MenuChosen?.Invoke(_menuIds[id - 1]);
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private nint BuildMenu(IReadOnlyList<TrayMenuItem> items)
    {
        var menu = CreatePopupMenu();
        foreach (var item in items)
        {
            if (item.IsSeparator)
            {
                AppendMenuW(menu, 0x800, 0, null); // MF_SEPARATOR
                continue;
            }

            fixed (char* text = item.Label)
            {
                if (item.Children is { Count: > 0 } children)
                {
                    AppendMenuW(menu, 0x10 | (item.Enabled ? 0u : 0x1u), (nuint)BuildMenu(children), text); // MF_POPUP
                }
                else
                {
                    _menuIds.Add(item);
                    AppendMenuW(menu, item.Enabled ? 0u : 0x1u, (nuint)_menuIds.Count, text); // MF_STRING | MF_GRAYED
                }
            }
        }

        return menu;
    }

    private static nint CreateIcon(uint[] pixels, int size)
    {
        if (size <= 0 || pixels.Length != size * size)
        {
            return 0;
        }

        var color = CreateBitmap32(pixels, size, size, out _);
        var mask = CreateBitmap(size, size, 1, 1, null);
        try
        {
            var info = new IconInfo { Icon = 1, Mask = mask, Color = color };
            return CreateIconIndirect(&info);
        }
        finally
        {
            DeleteObject(color);
            DeleteObject(mask);
        }
    }

    private void DrawEdge(TrayEdgeImage edge)
    {
        var (w, h) = (edge.Bounds.Width, edge.Bounds.Height);
        if (w <= 0 || h <= 0 || edge.Pixels.Length != w * h)
        {
            return;
        }

        var screen = GetDC(0);
        var mem = CreateCompatibleDC(screen);
        var bitmap = CreateBitmap32(edge.Pixels, w, h, out _);
        var old = SelectObject(mem, bitmap);
        try
        {
            var dst = new Point { X = edge.Bounds.X, Y = edge.Bounds.Y };
            var size = new Size { Cx = w, Cy = h };
            var src = default(Point);
            var blend = new BlendFunction { SourceConstantAlpha = 255, AlphaFormat = 1 }; // AC_SRC_OVER, AC_SRC_ALPHA
            UpdateLayeredWindow(_edge, screen, &dst, &size, mem, &src, 0, &blend, 0x2); // ULW_ALPHA
            SetWindowPos(_edge, -1, 0, 0, 0, 0, 0x1 | 0x2 | 0x10); // HWND_TOPMOST; SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE
            ShowWindow(_edge, 4); // SW_SHOWNOACTIVATE
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(bitmap);
            DeleteDC(mem);
            ReleaseDC(0, screen);
        }
    }

    private static nint CreateBitmap32(uint[] pixels, int width, int height, out nint bits)
    {
        var header = new BitmapInfoHeader { Size = (uint)sizeof(BitmapInfoHeader), Width = width, Height = -height, Planes = 1, BitCount = 32 };
        nint b;
        var bitmap = CreateDIBSection(0, &header, 0, &b, 0, 0);
        bits = b;
        if (bitmap != 0 && b != 0)
        {
            fixed (uint* src = pixels)
            {
                Buffer.MemoryCopy(src, (void*)b, (long)pixels.Length * 4, (long)pixels.Length * 4);
            }
        }

        return bitmap;
    }

    private void RaiseDisplay()
    {
        var monitor = MonitorFromPoint(default, 0x1); // MONITOR_DEFAULTTOPRIMARY
        var info = new MonitorInfo { CbSize = (uint)sizeof(MonitorInfo) };
        if (monitor == 0 || GetMonitorInfoW(monitor, &info) == 0)
        {
            return;
        }

        uint dpiX = 96, dpiY = 96;
        _ = GetDpiForMonitor(monitor, 0, &dpiX, &dpiY); // MDT_EFFECTIVE_DPI
        var iconSize = GetSystemMetricsForDpi(49, dpiX); // SM_CXSMICON
        var light = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is int v && v != 0;
        DisplayChanged?.Invoke(new TrayDisplay(info.Monitor.ToRect(), info.Work.ToRect(), dpiX / 96.0, iconSize > 0 ? iconSize : 16, light));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WndClassEx
    {
        public uint CbSize;
        public uint Style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> WndProc;
        public int ClsExtra;
        public int WndExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public nint MenuName;
        public nint ClassName;
        public nint IconSmall;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint CbSize;
        public nint Hwnd;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        public fixed char Tip[128];
        public uint State;
        public uint StateMask;
        public fixed char Info[256];
        public uint Version;
        public fixed char InfoTitle[64];
        public uint InfoFlags;
        public Guid Item;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackMouseEventData
    {
        public uint CbSize;
        public uint Flags;
        public nint Track;
        public uint HoverTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Size
    {
        public int Cx;
        public int Cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly TrayRect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint CbSize;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int Icon;
        public uint HotspotX;
        public uint HotspotY;
        public nint Mask;
        public nint Color;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
        public uint Colors; // BITMAPINFO's first RGBQUAD (unused at 32 bpp)
    }

    [LibraryImport("user32.dll")]
    private static partial ushort RegisterClassExW(WndClassEx* wc);

    [LibraryImport("user32.dll")]
    private static partial int UnregisterClassW(nint className, nint instance);

    [LibraryImport("user32.dll")]
    private static partial nint CreateWindowExW(uint exStyle, nint className, nint windowName, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    private static partial int DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(Msg* msg, nint hwnd, uint min, uint max);

    [LibraryImport("user32.dll")]
    private static partial int TranslateMessage(Msg* msg);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(Msg* msg);

    [LibraryImport("user32.dll")]
    private static partial int PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial void PostQuitMessage(int code);

    [LibraryImport("user32.dll")]
    private static partial uint RegisterWindowMessageW(char* name);

    [LibraryImport("user32.dll")]
    private static partial nint LoadCursorW(nint instance, nint name);

    [LibraryImport("user32.dll")]
    private static partial int ShowWindow(nint hwnd, int cmd);

    [LibraryImport("user32.dll")]
    private static partial int SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    private static partial int SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int TrackMouseEvent(TrackMouseEventData* data);

    [LibraryImport("user32.dll")]
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll")]
    private static partial int AppendMenuW(nint menu, uint flags, nuint id, char* text);

    [LibraryImport("user32.dll")]
    private static partial int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint parameters);

    [LibraryImport("user32.dll")]
    private static partial int DestroyMenu(nint menu);

    [LibraryImport("user32.dll")]
    private static partial nint CreateIconIndirect(IconInfo* info);

    [LibraryImport("user32.dll")]
    private static partial int DestroyIcon(nint icon);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint hwnd, nint dc);

    [LibraryImport("user32.dll")]
    private static partial int UpdateLayeredWindow(nint hwnd, nint dst, Point* pos, Size* size, nint src, Point* srcPos, uint key, BlendFunction* blend, uint flags);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromPoint(Point point, uint flags);

    [LibraryImport("user32.dll")]
    private static partial int GetMonitorInfoW(nint monitor, MonitorInfo* info);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetricsForDpi(int index, uint dpi);

    [LibraryImport("user32.dll")]
    private static partial nint SetThreadDpiAwarenessContext(nint context);

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(nint monitor, int type, uint* dpiX, uint* dpiY);

    [LibraryImport("shell32.dll")]
    private static partial int Shell_NotifyIconW(uint message, NotifyIconData* data);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial int DeleteDC(nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint dc, nint obj);

    [LibraryImport("gdi32.dll")]
    private static partial int DeleteObject(nint obj);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateBitmap(int width, int height, uint planes, uint bitCount, void* bits);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateDIBSection(nint dc, BitmapInfoHeader* info, uint usage, nint* bits, nint section, uint offset);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? name);
}
