using System.Runtime.InteropServices;
using Tasktor.Contracts.Windows;
using Tasktor.Core.Windows;
using static Tasktor.Shell.Native;

namespace Tasktor.Shell;

/// <summary>
/// F2's window (U3 D-W2 / D-W3 / D-W4): created hidden at start so it can appear within ~100 ms, shown by the switcher
/// hotkey (RegisterHotKey, default Alt+`, no hook) on the monitor with the cursor. While open it has keyboard focus: the
/// hotkey or Tab moves on (Shift back), arrows move, Up / Down cycle the views (all, app, workflow), typing searches, Enter or releasing
/// Alt chooses, Esc or losing focus closes. Live thumbnails are DWM's (drawn by the compositor, ≤ 24). Titles live in
/// this process's memory only and are never logged. Blocked in GetMessage while hidden: no wake-ups.
/// </summary>
internal sealed unsafe class SwitcherWindow : IDisposable
{
    private const int HotkeyId = 1;
    private const uint WmResumed = WmApp + 1;

    private static SwitcherWindow? s_window;

    private readonly SwitcherSession _session;
    private readonly Func<SwitcherFeed?> _feed;
    private readonly Func<string, Task<bool>> _resume;
    private readonly Action<string, bool> _reportHotkey;
    private readonly List<nint> _thumbnails = [];
    private nint _hwnd;
    private nint _font;
    private string? _hotkeyText;
    private Hotkey? _hotkey;
    private SwitcherLayout? _layout;
    private int _first; // the first tile of the drawn page (more than 24 windows page, D-W4)
    private double _scale = 1.0;
    private bool _suppressChar;
    private KeyChordHook? _altTab;

    /// <param name="includePid">Which processes' windows may be listed: others are skipped before their title is read.</param>
    /// <param name="reportHotkey">Tells the agent whether Windows accepted the hotkey (Settings shows a note when it did not).</param>
    public SwitcherWindow(Func<SwitcherFeed?> feed, Func<string, Task<bool>> resume, Func<uint, bool> includePid, Action<string, bool> reportHotkey)
    {
        _feed = feed;
        _resume = resume;
        _reportHotkey = reportHotkey;
        _session = new SwitcherSession(() => [.. Windows.Shell.Win32Windows.Enumerate(include: includePid).Where(w => w.Hwnd != _hwnd)]);
    }

    public bool HotkeyRegistered { get; private set; }

    /// <summary>Create the hidden window, register the hotkey and pump messages until the process is told to stop.</summary>
    public int Run()
    {
        s_window = this;
        SetThreadDpiAwarenessContext(DpiAwarePerMonitorV2);
        var module = GetModuleHandleW(null);
        var className = Marshal.StringToHGlobalUni("TasktorSwitcher");
        try
        {
            var wc = new WndClassEx { CbSize = (uint)sizeof(WndClassEx), WndProc = &WndProc, Instance = module, ClassName = className, Cursor = LoadCursorW(0, 32512) };
            RegisterClassExW(&wc);
            _hwnd = CreateWindowExW(WsExToolWindow | WsExTopmost, className, 0, WsPopup, 0, 0, 1, 1, 0, 0, module, 0);
            if (_hwnd == 0)
            {
                return 2;
            }

            _font = CreateFontW(-16, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
            var start = _feed();
            ApplyHotkey(start?.Hotkey);
            if (start?.ReplaceAltTab == true)
            {
                _altTab = new KeyChordHook(_hwnd, () => _session.IsOpen); // opt-in (D-W3); a settings change restarts the shell
                _altTab.Start();
            }
            Msg msg;
            while (GetMessageW(&msg, 0, 0, 0) > 0)
            {
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }

            return 0;
        }
        finally
        {
            Dispose();
            Marshal.FreeHGlobal(className);
        }
    }

    public void Dispose()
    {
        _altTab?.Dispose();
        _altTab = null;
        ClearThumbnails();
        if (_hwnd != 0)
        {
            if (HotkeyRegistered)
            {
                UnregisterHotKey(_hwnd, HotkeyId);
            }

            DestroyWindow(_hwnd);
            _hwnd = 0;
        }

        if (_font != 0)
        {
            DeleteObject(_font);
            _font = 0;
        }
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            if (s_window is { } w && w.Handle(hwnd, msg, wParam, lParam))
            {
                return 0;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            s_window?.Hide(); // never throw across the native boundary; the next hotkey starts afresh
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private bool Handle(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WmHotKey when (int)wParam == HotkeyId:
                if (_session.IsOpen)
                {
                    Step(Shift() ? -1 : +1);
                }
                else
                {
                    Open(reverse: Shift());
                }

                return true;
            case WmKeyDown or WmSysKeyDown:
                return Key((int)wParam);
            case WmKeyUp or WmSysKeyUp when (int)wParam == VkMenu:
                Finish(_session.AltReleased());
                return true;
            case WmChar or WmSysChar:
                if (_suppressChar)
                {
                    _suppressChar = false;
                }
                else if (_session.IsOpen)
                {
                    _session.Type((char)wParam);
                    Relayout(); // the tiles changed: new grid and thumbnails, or a filtered tile would show another window
                }

                return true;
            case WmActivate when (wParam & 0xFFFF) == 0 && _session.IsOpen:
                _session.Cancel(); // focus went elsewhere: close without switching
                Hide();
                return true;
            case KeyChordHook.WmAltTab:
                AltTab((AltTabCommand)(int)wParam);
                return true;
            case WmResumed:
                if (wParam != 0)
                {
                    Activate(wParam);
                }

                return true;
            case WmEraseBkgnd:
                return true;
            case WmPaint:
                Paint(hwnd);
                return true;
            case WmDestroy:
                PostQuitMessage(0);
                return true;
            default:
                return false;
        }
    }

    private static bool Shift() => (GetKeyState(VkShift) & 0x8000) != 0;

    private static bool Control() => (GetKeyState(VkControl) & 0x8000) != 0;

    /// <summary>A command from the opt-in Alt+Tab hook (the hook swallowed the key; Alt's release still reaches this window).</summary>
    private void AltTab(AltTabCommand command)
    {
        switch (command)
        {
            case AltTabCommand.Open or AltTabCommand.OpenReverse when !_session.IsOpen:
                Open(reverse: command == AltTabCommand.OpenReverse);
                break;
            case AltTabCommand.Next or AltTabCommand.Open:
                Step(+1);
                break;
            case AltTabCommand.Previous or AltTabCommand.OpenReverse:
                Step(-1);
                break;
            case AltTabCommand.Cancel:
                _session.Cancel();
                Hide();
                break;
        }
    }

    private bool Key(int vk)
    {
        if (!_session.IsOpen)
        {
            return false;
        }

        _suppressChar = true; // a key handled here never also types its character
        switch (vk)
        {
            case VkTab:
                Step(Shift() ? -1 : +1);
                break;
            case VkLeft:
                Step(-1);
                break;
            case VkRight:
                Step(+1);
                break;
            case VkUp or VkDown:
                _session.CycleView();
                Relayout();
                break;
            case VkReturn when Control():
                Finish(_session.BringHere()); // null (not offered here): the switcher stays as it is
                break;
            case VkReturn:
                Finish(_session.Commit());
                break;
            case VkEscape:
                _session.Cancel();
                Hide();
                break;
            case VkBack:
                _session.Backspace();
                Relayout();
                break;
            default:
                if (_hotkey is { } h && vk == (int)h.VirtualKey)
                {
                    Step(Shift() ? -1 : +1);
                    break;
                }

                _suppressChar = false; // an ordinary key: its WM_CHAR searches
                return vk != VkMenu;
        }

        return true;
    }

    private void ApplyHotkey(string? text)
    {
        if (text == _hotkeyText && HotkeyRegistered)
        {
            return;
        }

        if (HotkeyRegistered)
        {
            UnregisterHotKey(_hwnd, HotkeyId);
            HotkeyRegistered = false;
        }

        _hotkeyText = text;
        _hotkey = Hotkey.Parse(text ?? "Alt+`", out _) ?? Hotkey.Parse("Alt+`", out _);
        if (_hotkey is { } h)
        {
            HotkeyRegistered = RegisterHotKey(_hwnd, HotkeyId, h.Modifiers | ModNoRepeat, h.VirtualKey) != 0;
            _reportHotkey(h.ToString(), HotkeyRegistered);
        }
    }

    private void Open(bool reverse)
    {
        var feed = _feed() ?? new SwitcherFeed(); // the agent is slow or gone: z-order, no badges
        ApplyHotkey(feed.Hotkey);
        _session.Open(feed, reverse);
        if (!_session.IsOpen)
        {
            return;
        }

        Relayout();
        ShowWindow(_hwnd, SwShow);
        SetForegroundWindow(_hwnd); // allowed: this process received the hotkey
        if ((GetAsyncKeyState(VkMenu) & 0x8000) == 0)
        {
            Finish(_session.AltReleased()); // Alt already up (a quick tap): choose the previous window at once
        }
    }

    private void Step(int by)
    {
        if (by > 0)
        {
            _session.Next();
        }
        else
        {
            _session.Previous();
        }

        if (SwitcherLayout.Page(_session.Selected, _session.Tiles.Count).First != _first)
        {
            Relayout(); // onto another page: new tiles and thumbnails
        }
        else
        {
            Redraw();
        }
    }

    private void Finish(SwitcherOutcome? outcome)
    {
        switch (outcome)
        {
            case SwitcherOutcome.Activate a:
                Hide();
                Activate((nint)a.Hwnd);
                break;
            case SwitcherOutcome.BringHere b:
                Hide();
                Windows.Shell.VirtualDesktopInfo.TryBringHere(b.Hwnd, _hwnd); // refused (W2): activating may still switch desktops
                Activate((nint)b.Hwnd);
                break;
            case SwitcherOutcome.ResumeThenActivate r:
                Hide();
                var hwnd = _hwnd;
                _ = _resume(r.JobId).ContinueWith(t => PostMessageW(hwnd, WmResumed, t.IsCompletedSuccessfully && t.Result ? (nint)r.Hwnd : 0, 0), TaskScheduler.Default);
                break;
            default:
                if (_session.IsOpen)
                {
                    Redraw(); // a question (Resume first) or the window stays open for Enter
                }
                else
                {
                    Hide();
                }

                break;
        }
    }

    private static void Activate(nint target)
    {
        if (IsIconic(target) != 0)
        {
            ShowWindow(target, SwRestore);
        }

        SetForegroundWindow(target);
    }

    private void Hide()
    {
        ClearThumbnails();
        if (_hwnd != 0)
        {
            ShowWindow(_hwnd, SwHide);
        }
    }

    private void Relayout()
    {
        Point cursor;
        GetCursorPos(&cursor);
        var info = new MonitorInfoW { CbSize = (uint)sizeof(MonitorInfoW) };
        GetMonitorInfoW(MonitorFromPoint(cursor, MonitorDefaultToNearest), &info);
        var work = new RectPx(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
        var dpi = GetDpiForWindow(_hwnd);
        _scale = dpi == 0 ? 1.0 : dpi / 96.0;
        var (first, count) = SwitcherLayout.Page(_session.Selected, _session.Tiles.Count);
        _first = first;
        _layout = SwitcherLayout.For(count, work, _scale);
        SetWindowPos(_hwnd, -1, _layout.Window.X, _layout.Window.Y, _layout.Window.W, _layout.Window.H, SwpNoActivate);
        RegisterThumbnails();
        Redraw();
    }

    private void Redraw()
    {
        if (_hwnd != 0)
        {
            InvalidateRect(_hwnd, null, 0);
        }
    }

    private void RegisterThumbnails()
    {
        ClearThumbnails();
        if (_layout is not { } layout)
        {
            return;
        }

        for (var i = 0; i < layout.Tiles.Count && _first + i < _session.Tiles.Count; i++)
        {
            nint thumb;
            if (DwmRegisterThumbnail(_hwnd, (nint)_session.Tiles[_first + i].Window.Hwnd, &thumb) != 0)
            {
                continue; // a window that went away or refuses: the tile shows its caption only
            }

            _thumbnails.Add(thumb);
            Size size;
            DwmQueryThumbnailSourceSize(thumb, &size);
            var r = SwitcherLayout.Fit(SwitcherLayout.Thumbnail(layout.Tiles[i], _scale), size.Cx, size.Cy);
            var props = new ThumbnailProperties
            {
                Flags = TnpRectDestination | TnpVisible | TnpOpacity | TnpSourceClientAreaOnly,
                Destination = new Rect(r.X, r.Y, r.W, r.H),
                Opacity = 255,
                Visible = 1,
                SourceClientAreaOnly = 0,
            };
            DwmUpdateThumbnailProperties(thumb, &props);
        }
    }

    private void ClearThumbnails()
    {
        foreach (var t in _thumbnails)
        {
            DwmUnregisterThumbnail(t);
        }

        _thumbnails.Clear();
    }

    private void Paint(nint hwnd)
    {
        PaintStruct ps;
        var hdc = BeginPaint(hwnd, &ps);
        try
        {
            if (_layout is not { } layout)
            {
                return;
            }

            var background = CreateSolidBrush(Rgb(0x20, 0x20, 0x20));
            var tile = CreateSolidBrush(Rgb(0x2C, 0x2C, 0x2C));
            var accent = CreateSolidBrush(Rgb(0x4C, 0xA0, 0xE0));
            try
            {
                var all = new Rect(0, 0, layout.Window.W, layout.Window.H);
                FillRect(hdc, &all, background);
                var oldFont = SelectObject(hdc, _font);
                SetBkMode(hdc, 1);
                SetTextColor(hdc, Rgb(0xF0, 0xF0, 0xF0));
                var header = _session.Prompt
                    ?? (_session.Query.Length > 0 ? "Search: " + _session.Query
                        : _session.View switch
                        {
                            SwitcherView.App => "This app's windows (Up / Down: next view)",
                            SwitcherView.Workflow => "By workflow (Up / Down: all windows)",
                            _ => "Switch to (type to search, Up / Down: this app's windows)",
                        });
                if (_session.Tiles.Count > SwitcherFilter.MaxTiles)
                {
                    header += "   (" + (_first + 1) + "–" + (_first + layout.Tiles.Count) + " of " + _session.Tiles.Count + ")";
                }

                Text(hdc, header, layout.Header);
                for (var i = 0; i < layout.Tiles.Count && _first + i < _session.Tiles.Count; i++)
                {
                    var cell = layout.Tiles[i];
                    if (_first + i == _session.Selected)
                    {
                        var ring = new Rect(cell.X - 3, cell.Y - 3, cell.W + 6, cell.H + 6);
                        FillRect(hdc, &ring, accent);
                    }

                    var body = new Rect(cell.X, cell.Y, cell.W, cell.H);
                    FillRect(hdc, &body, tile);
                    Text(hdc, _session.Describe(_session.Tiles[_first + i]), SwitcherLayout.Caption(cell, _scale));
                }

                SelectObject(hdc, oldFont);
            }
            finally
            {
                DeleteObject(background);
                DeleteObject(tile);
                DeleteObject(accent);
            }
        }
        finally
        {
            EndPaint(hwnd, &ps);
        }
    }

    private static void Text(nint hdc, string text, RectPx where)
    {
        var r = new Rect(where.X, where.Y, where.W, where.H);
        fixed (char* p = text)
        {
            DrawTextW(hdc, p, text.Length, &r, DtLeft | DtVCenter | DtSingleLine | DtEndEllipsis | DtNoPrefix);
        }
    }
}
