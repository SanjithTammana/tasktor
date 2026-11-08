using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Tasktor.Capture.UIAutomation.Com;
using Tasktor.Contracts.Platform;

namespace Tasktor.Capture.UIAutomation;

/// <summary>UIA control types as walk roles (control type − 50000, 0–40) and their names for click targets.</summary>
public static class UiaRoles
{
    public const byte Edit = 4, Image = 6, ProgressBar = 12, ScrollBar = 14, Thumb = 27, Document = 30, Separator = 38;

    private static readonly string[] Names =
    [
        "button", "calendar", "check box", "combo box", "edit", "link", "image", "list item", "list", "menu", "menu bar",
        "menu item", "progress bar", "radio button", "scroll bar", "slider", "spinner", "status bar", "tab", "tab item",
        "text", "tool bar", "tool tip", "tree", "tree item", "custom", "group", "thumb", "data grid", "data item",
        "document", "split button", "window", "pane", "header", "header item", "table", "title bar", "separator",
        "semantic zoom", "app bar",
    ];

    /// <summary>The role of a control type id, or null outside UIA's known range.</summary>
    public static byte? FromControlType(int controlType) => controlType is >= 50000 and <= 50040 ? (byte)(controlType - 50000) : null;

    public static string Name(byte role) => role < Names.Length ? Names[role] : "element";

    /// <summary>C §5.4: scroll bars, separators, thumbs and unnamed images are decorative; an unknown type is not read.</summary>
    public static UiaNodeKind Kind(byte? role, string? name) => role switch
    {
        null => UiaNodeKind.Unknown,
        ScrollBar or Separator or Thumb => UiaNodeKind.Decorative,
        Image when string.IsNullOrWhiteSpace(name) => UiaNodeKind.Decorative,
        _ => UiaNodeKind.Content,
    };

    /// <summary>"role: name" for a click target, or the role alone.</summary>
    public static string Target(byte role, string? name) => string.IsNullOrWhiteSpace(name) ? Name(role) : Name(role) + ": " + name;
}

/// <summary>
/// The native <see cref="IUiaClient"/>: <c>CUIAutomation8</c> through source-generated COM (<see cref="StrategyBasedComWrappers"/>),
/// created on the worker's MTA thread with 1 s connection and transaction timeouts. One focus handler (cached identity
/// and password state, no value) and one event handler group on the watched window's root (structure changes, text
/// changes, name changes) only queue. Walks use one cached subtree request filtered to the control view and on-screen
/// elements; values and text bodies are read live, and only for non-password elements (the walk logic decides).
/// </summary>
public sealed unsafe partial class Win32UiaClient : IUiaClient
{
    public const uint TimeoutMs = 1000;
    private static readonly StrategyBasedComWrappers Wrappers = new();
    private readonly IUIAutomation6 _uia;
    private readonly IUIAutomationCacheRequest _walk, _focus, _hit;
    private readonly IUIAutomationEventHandlerGroup _group;
    private readonly FocusHandler _focusHandler;
    private IUIAutomationElement? _watchedRoot;
    private long _watched;

    private Win32UiaClient(IUIAutomation6 uia)
    {
        _uia = uia;
        _uia.SetConnectionTimeout(TimeoutMs);
        _uia.SetTransactionTimeout(TimeoutMs);
        _uia.SetCoalesceEvents(1);

        _walk = Request(UiaIds.TreeScopeSubtree, UiaIds.PropControlType, UiaIds.PropName, UiaIds.PropAutomationId, UiaIds.PropIsPassword,
            UiaIds.PropIsOffscreen, UiaIds.PropIsValuePatternAvailable, UiaIds.PropIsTextPatternAvailable);
        _walk.SetTreeFilter(_uia.CreateAndCondition(_uia.GetControlViewCondition(), _uia.CreatePropertyCondition(UiaIds.PropIsOffscreen, UiaVariant.Bool(false))));
        _focus = Request(UiaIds.TreeScopeElement, UiaIds.PropControlType, UiaIds.PropName, UiaIds.PropAutomationId, UiaIds.PropIsPassword,
            UiaIds.PropIsOffscreen, UiaIds.PropIsValuePatternAvailable, UiaIds.PropProcessId);
        _hit = Request(UiaIds.TreeScopeElement, UiaIds.PropControlType, UiaIds.PropName);

        _group = _uia.CreateEventHandlerGroup();
        var changes = new ChangeHandler(this);
        _group.AddStructureChangedEventHandler(UiaIds.TreeScopeSubtree, null, changes);
        _group.AddAutomationEventHandler(UiaIds.EventTextChanged, UiaIds.TreeScopeSubtree, null, changes);
        var names = stackalloc int[] { UiaIds.PropName };
        _group.AddPropertyChangedEventHandler(UiaIds.TreeScopeSubtree, null, changes, names, 1);
        _focusHandler = new FocusHandler(this);
        _uia.AddFocusChangedEventHandler(_focus, _focusHandler);
    }

    public event Action<long>? WindowDirty;

    public event Action<UiaFocusChange>? FocusChanged;

    /// <summary>Create the client on the calling (MTA) thread; null off Windows or when UIA cannot be created.</summary>
    public static Win32UiaClient? Create()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var clsid = UiaIds.ClsidCuiAutomation8;
        var iid = typeof(IUIAutomation6).GUID;
        if (CoCreateInstance(&clsid, 0, 1 /* CLSCTX_INPROC_SERVER */, &iid, out var ptr) < 0 || ptr == 0) return null;
        try
        {
            return new Win32UiaClient((IUIAutomation6)Wrappers.GetOrCreateObjectForComInstance(ptr, CreateObjectFlags.None));
        }
        finally
        {
            Marshal.Release(ptr);
        }
    }

    public void Watch(long window)
    {
        if (window == _watched) return;
        if (_watchedRoot is { } old)
        {
            try
            {
                _uia.RemoveEventHandlerGroup(old, _group);
            }
            catch (Exception)
            {
                // the window is gone: its handlers went with it
            }
        }

        _watchedRoot = null;
        Volatile.Write(ref _watched, window);
        if (window == 0) return;
        try
        {
            var root = _uia.ElementFromHandle((nint)window);
            _uia.AddEventHandlerGroup(root, _group);
            _watchedRoot = root;
        }
        catch (Exception)
        {
            Volatile.Write(ref _watched, 0); // elevated (UIPI) or gone: not watched; focus moves try again
        }
    }

    public IUiaWalkNode? Root(long window, bool textBodies)
    {
        try
        {
            return new Node(_uia.ElementFromHandleBuildCache((nint)window, _walk), textBodies);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public string? HitTest(int x, int y)
    {
        try
        {
            var e = _uia.ElementFromPointBuildCache(new UiaPoint { X = x, Y = y }, _hit);
            return UiaRoles.FromControlType(UiaVariant.Int(e.GetCachedPropertyValue(UiaIds.PropControlType)) ?? 0) is { } role
                ? UiaRoles.Target(role, UiaWalkLogic.Cap(UiaVariant.String(e.GetCachedPropertyValue(UiaIds.PropName)), ClickEventMaxTarget))
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private const int ClickEventMaxTarget = Deep.ClickEvent.MaxTarget;

    public void Dispose()
    {
        Watch(0);
        try
        {
            _uia.RemoveFocusChangedEventHandler(_focusHandler);
        }
        catch (Exception)
        {
            // already gone with the connection
        }
    }

    private IUIAutomationCacheRequest Request(int scope, params int[] properties)
    {
        var r = _uia.CreateCacheRequest();
        foreach (var p in properties) r.AddProperty(p);
        r.SetTreeScope(scope);
        return r;
    }

    private void OnChange() => WindowDirty?.Invoke(Volatile.Read(ref _watched));

    private void OnFocus(IUIAutomationElement sender)
    {
        var node = new Node(sender, textBodies: false);
        if (UiaVariant.Int(sender.GetCachedPropertyValue(UiaIds.PropProcessId)) == Environment.ProcessId) return; // our own windows
        var window = GetForegroundWindow();
        if (window != 0) FocusChanged?.Invoke(new UiaFocusChange(window, node));
    }

    /// <summary>One cached element. Cached reads are local; values and text bodies are live reads under the timeout.</summary>
    private sealed class Node : IUiaWalkNode
    {
        private readonly IUIAutomationElement _e;
        private readonly bool _textBodies;
        private readonly int? _controlType;
        private IReadOnlyList<IUiaWalkNode>? _children;

        public Node(IUIAutomationElement e, bool textBodies)
        {
            _e = e;
            _textBodies = textBodies;
            _controlType = UiaVariant.Int(e.GetCachedPropertyValue(UiaIds.PropControlType));
            var name = UiaWalkLogic.Cap(UiaVariant.String(e.GetCachedPropertyValue(UiaIds.PropName)), UiaWalkLogic.MaxNameChars);
            Name = name is null ? null : Sensitive.Of(name);
            var id = UiaWalkLogic.Cap(UiaVariant.String(e.GetCachedPropertyValue(UiaIds.PropAutomationId)), UiaWalkLogic.MaxAutomationIdChars);
            AutomationId = id is null ? null : Sensitive.Of(id);
            Password = UiaVariant.Bool(e.GetCachedPropertyValue(UiaIds.PropIsPassword));
            Offscreen = UiaVariant.Bool(e.GetCachedPropertyValue(UiaIds.PropIsOffscreen));
            Kind = UiaRoles.Kind(_controlType is { } ct ? UiaRoles.FromControlType(ct) : null, name);
        }

        public UiaNodeKind Kind { get; }

        public byte Role => _controlType is { } ct && UiaRoles.FromControlType(ct) is { } r ? r : (byte)0xFF;

        public bool? Offscreen { get; }

        public bool? Password { get; }

        public Sensitive<string>? Name { get; }

        public Sensitive<string>? AutomationId { get; }

        public IReadOnlyList<IUiaWalkNode> Children => _children ??= ReadChildren();

        public Sensitive<string>? ReadValue(int maxUtf8Bytes)
        {
            if (Password != false || UiaVariant.Bool(_e.GetCachedPropertyValue(UiaIds.PropIsValuePatternAvailable)) != true) return null;
            var value = UiaVariant.String(_e.GetCurrentPropertyValue(UiaIds.PropValueValue));
            return value is null ? null : Sensitive.Of(Fit(value, maxUtf8Bytes));
        }

        public Sensitive<string>? ReadVisibleText(int maxUtf8Bytes)
        {
            if (!_textBodies || Password != false || Role is not (UiaRoles.Document or UiaRoles.Edit)
                || UiaVariant.Bool(_e.GetCachedPropertyValue(UiaIds.PropIsTextPatternAvailable)) != true)
            {
                return null;
            }

            var iid = UiaIds.IidTextPattern;
            var ptr = _e.GetCurrentPatternAs(UiaIds.PatternText, in iid);
            if (ptr == 0) return null;
            IUIAutomationTextPattern pattern;
            try
            {
                pattern = (IUIAutomationTextPattern)Wrappers.GetOrCreateObjectForComInstance(ptr, CreateObjectFlags.None);
            }
            finally
            {
                Marshal.Release(ptr);
            }

            var ranges = pattern.GetVisibleRanges();
            var text = new System.Text.StringBuilder();
            var n = ranges.GetLength();
            for (var i = 0; i < n && text.Length < maxUtf8Bytes; i++)
            {
                if (ranges.GetElement(i).GetText(maxUtf8Bytes - text.Length) is { } part)
                {
                    if (text.Length > 0) text.Append('\n');
                    text.Append(part);
                }
            }

            return text.Length == 0 ? null : Sensitive.Of(Fit(text.ToString(), maxUtf8Bytes));
        }

        private List<IUiaWalkNode> ReadChildren()
        {
            var list = new List<IUiaWalkNode>();
            if (_e.GetCachedChildren() is not { } array) return list;
            var n = Math.Min(array.GetLength(), UiaWalkLogic.MaxNodes);
            for (var i = 0; i < n; i++) list.Add(new Node(array.GetElement(i), _textBodies));
            return list;
        }

        private static string Fit(string value, int maxUtf8Bytes)
        {
            if (System.Text.Encoding.UTF8.GetByteCount(value) <= maxUtf8Bytes) return value;
            var cut = Math.Min(value.Length, maxUtf8Bytes);
            while (cut > 0 && System.Text.Encoding.UTF8.GetByteCount(value.AsSpan(0, cut)) > maxUtf8Bytes) cut = cut * 3 / 4;
            return UiaWalkLogic.Cap(value, cut)!;
        }
    }

    [GeneratedComClass]
    private sealed partial class ChangeHandler(Win32UiaClient owner) : IUIAutomationEventHandler, IUIAutomationPropertyChangedEventHandler, IUIAutomationStructureChangedEventHandler
    {
        public void HandleAutomationEvent(IUIAutomationElement sender, int eventId) => owner.OnChange();

        public void HandlePropertyChangedEvent(IUIAutomationElement sender, int propertyId, RawVariant newValue) => owner.OnChange(); // the value is not read

        public void HandleStructureChangedEvent(IUIAutomationElement sender, int changeType, nint runtimeId) => owner.OnChange();
    }

    [GeneratedComClass]
    private sealed partial class FocusHandler(Win32UiaClient owner) : IUIAutomationFocusChangedEventHandler
    {
        public void HandleFocusChangedEvent(IUIAutomationElement sender)
        {
            try
            {
                owner.OnFocus(sender);
            }
            catch (Exception)
            {
                // never thrown back into UIA; never logged
            }
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, out nint instance);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();
}
