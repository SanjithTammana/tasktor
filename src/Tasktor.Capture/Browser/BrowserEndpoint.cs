using System.Globalization;
using Tasktor.Capture.Deep;
using Tasktor.Contracts.Capture;
using Tasktor.Contracts.Platform;

namespace Tasktor.Capture.Browser;

/// <summary>What capture may use about one tab right now. <see cref="Secure"/> is true until an explicit focus says otherwise.</summary>
/// <remarks><see cref="Url"/> and <see cref="Title"/> come with T1 (C D-C22); <see cref="PageRef"/> is a hash of host + path that keys the page's view.</remarks>
public sealed record TabContext(Sensitive<string>? Host, bool Secure, bool Visible, bool Active, Sensitive<string>? Url = null, Sensitive<string>? Title = null, ulong PageRef = 0, bool Private = false);

/// <summary>
/// The capture host's side of <c>tasktor.browser/1</c> (D-C18): one session per connected browser. Contract check on
/// hello; tab state keyed by (browser, tab id) that starts <b>secure</b> on a new tab, a navigation to another host, or a
/// reconnect, and leaves secure only on an explicit focus message; block requests from the popup (the user) applied to
/// <see cref="CaptureBlocks"/>; and each session's <see cref="BlockState"/>, pushed after every change from any source.
/// Thread-safe: the pipe server calls it from several connections.
/// </summary>
public sealed class BrowserEndpoint
{
    public const int Contract = 1;
    public static readonly string[] Browsers = ["chrome", "edge"];
    private static readonly Dictionary<string, string> BrowserExe = new(StringComparer.Ordinal) { ["chrome"] = "chrome.exe", ["edge"] = "msedge.exe" };

    private readonly CaptureBlocks _blocks;
    private readonly Func<DateTimeOffset> _now;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _sessions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _private = new(StringComparer.Ordinal); // sessions whose hello opted in to private windows
    private readonly Dictionary<(string Browser, int Tab), TabContext> _tabs = [];

    public BrowserEndpoint(CaptureBlocks blocks, Func<DateTimeOffset> now)
    {
        _blocks = blocks;
        _now = now;
    }

    /// <summary>Raised after any block change; the server pushes <see cref="StateFor"/> to every session.</summary>
    public event Action? BlocksChanged;

    /// <summary>A tab's context changed (browser, tab, context): the recorder writes it unless a block applies.</summary>
    public event Action<string, int, TabContext>? ContextChanged;

    /// <summary>
    /// A tab's visible text (C18 / §5.5) as a walk for <see cref="DeepCapture.Walk"/> with
    /// <see cref="DiffSource.Extension"/>: (app = the browser exe, tab, walk, host). Only for a known, unblocked tab.
    /// </summary>
    public event Action<string, int, UiaWalk, Sensitive<string>?>? TextSeen;

    /// <summary>A tab's video playing / paused and where (C20.2): (app, tab, playing, position in seconds).</summary>
    public event Action<string, int, bool, double?>? MediaSeen;

    public long Rejected { get; private set; }

    public long Accepted { get; private set; }

    /// <summary>A block changed outside the browser (the agent's card, a rule, expiry): push the new state.</summary>
    public void NotifyBlocksChanged() => BlocksChanged?.Invoke();

    public string? BrowserOf(string session)
    {
        lock (_gate)
        {
            return _sessions.GetValueOrDefault(session);
        }
    }

    /// <summary>The browsers with an open, accepted session (the extension is installed and connected), sorted.</summary>
    public IReadOnlyList<string> Connected()
    {
        lock (_gate)
        {
            return [.. _sessions.Values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        }
    }

    /// <summary>A session's connection closed (the browser exited or the extension was removed).</summary>
    public void Closed(string session)
    {
        lock (_gate)
        {
            _sessions.Remove(session);
            _private.Remove(session);
        }
    }

    /// <summary>Handle one message from a session; returns the replies (in order). Unknown or invalid input is counted.</summary>
    public List<BrowserMessage> Handle(string session, BrowserMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var replies = new List<BrowserMessage>();
        var changed = false;
        (string Browser, int Tab, TabContext Ctx)? context = null;
        (string App, int Tab, UiaWalk Walk, Sensitive<string>? Host)? text = null;
        (string App, int Tab, bool Playing, double? Position)? media = null;
        lock (_gate)
        {
            var now = _now();
            _blocks.Expire(now);
            if (message is BrowserHello hello)
            {
                if (hello.Contract != Contract || !Browsers.Contains(hello.Browser) || hello.Client != "nmh")
                {
                    Rejected++;
                    replies.Add(new BrowserWelcome { At = now, Contract = Contract, Blocks = new BlockState { Paused = true, ExcludedHosts = [], PausedTabs = [] } });
                    return replies; // the server closes the connection after a refused hello
                }

                _sessions[session] = hello.Browser;
                if (hello.Incognito)
                {
                    _private.Add(session);
                }
                else
                {
                    _private.Remove(session); // fail closed: any hello without the opt-in ends this browser's private tabs
                    foreach (var k in _tabs.Where(kv => kv.Key.Browser == hello.Browser && kv.Value.Private).Select(kv => kv.Key).ToList())
                    {
                        _tabs.Remove(k); // private windows turned off: forget their tabs at once
                    }
                }

                foreach (var k in _tabs.Keys.Where(k => k.Browser == hello.Browser).ToList())
                {
                    _tabs[k] = _tabs[k] with { Secure = true }; // a reconnect: secure until focus arrives again
                }

                Accepted++;
                replies.Add(new BrowserWelcome { At = now, Contract = Contract, Blocks = State(hello.Browser, now) });
                return replies;
            }

            if (!_sessions.TryGetValue(session, out var browser))
            {
                Rejected++; // nothing before hello
                return replies;
            }

            switch (message)
            {
                case BrowserTab { Incognito: true } when !_private.Contains(session):
                    Rejected++;
                    return replies; // a private tab without the profile's opt-in: nothing is kept
                case BrowserTab t:
                    var key = (browser, t.Tab);
                    var host = t.Host is null ? (Sensitive<string>?)null : Sensitive.Of(t.Host);
                    var known = _tabs.TryGetValue(key, out var prev);
                    var sameHost = known && prev!.Host == host;
                    var url = t.Url is null || t.Host is null ? (Sensitive<string>?)null : Sensitive.Of(t.Url);
                    var title = t.Title is null ? (Sensitive<string>?)null : Sensitive.Of(t.Title);
                    _tabs[key] = new TabContext(host, !sameHost || prev!.Secure, known ? prev!.Visible : t.Active, t.Active, url, title, PageRef(t.Host, t.Url), t.Incognito);
                    context = (browser, t.Tab, _tabs[key]);
                    break;
                case BrowserFocus f when f.Field is "none" or "text" or "secure":
                    if (_tabs.TryGetValue((browser, f.Tab), out var tc))
                    {
                        _tabs[(browser, f.Tab)] = tc with { Secure = f.Field == "secure" };
                        context = (browser, f.Tab, _tabs[(browser, f.Tab)]);
                    }

                    break;
                case BrowserVisibility v:
                    if (_tabs.TryGetValue((browser, v.Tab), out var tv))
                    {
                        _tabs[(browser, v.Tab)] = tv with { Visible = v.Visible };
                        context = (browser, v.Tab, _tabs[(browser, v.Tab)]);
                    }

                    break;
                case BrowserText x when x.Lines.Count <= BrowserText.MaxLines && x.Lines.Sum(l => l?.Length ?? 0) <= BrowserText.MaxChars:
                    if (!_tabs.TryGetValue((browser, x.Tab), out var page) || Blocked(browser, x.Tab, page, now))
                    {
                        Rejected++;
                        return replies; // an unknown or blocked tab's text is dropped before it is diffed
                    }

                    text = (BrowserExe[browser], x.Tab, ToWalk(browser, x.Tab, page.PageRef, x.Lines), page.Host);
                    break;
                case BrowserMedia m:
                    if (!_tabs.TryGetValue((browser, m.Tab), out var playing) || Blocked(browser, m.Tab, playing, now))
                    {
                        Rejected++;
                        return replies;
                    }

                    media = (BrowserExe[browser], m.Tab, m.Playing, m.PositionS);
                    break;
                case BrowserTabClosed c:
                    _tabs.Remove((browser, c.Tab));
                    var before = _blocks.Blocks.Count;
                    _blocks.TabClosed(TabKey(browser, c.Tab));
                    changed = _blocks.Blocks.Count != before;
                    break;
                case BrowserBlockRequest r:
                    var (ok, reason) = Apply(browser, r, now);
                    replies.Add(new BrowserAck { At = now, RequestId = r.RequestId, Ok = ok, Reason = reason });
                    changed = ok;
                    break;
                default:
                    Rejected++;
                    return replies;
            }

            Accepted++;
        }

        if (changed)
        {
            BlocksChanged?.Invoke();
        }

        if (context is { } changedTab)
        {
            ContextChanged?.Invoke(changedTab.Browser, changedTab.Tab, changedTab.Ctx);
        }

        if (text is { } seen)
        {
            TextSeen?.Invoke(seen.App, seen.Tab, seen.Walk, seen.Host);
        }

        if (media is { } m2)
        {
            MediaSeen?.Invoke(m2.App, m2.Tab, m2.Playing, m2.Position);
        }

        return replies;
    }

    /// <summary>The block state one browser must honour now.</summary>
    public BlockState StateFor(string browser)
    {
        lock (_gate)
        {
            var now = _now();
            _blocks.Expire(now);
            return State(browser, now);
        }
    }

    /// <summary>The context capture may use for a tab, or null when the tab is unknown (unknown = secure, use nothing).</summary>
    public TabContext? Context(string browser, int tab)
    {
        lock (_gate)
        {
            return _tabs.GetValueOrDefault((browser, tab));
        }
    }

    /// <summary>The block key of a tab: the browser that reported this id, or the active visible tab when none is given.</summary>
    public string? ResolveTab(int? tab)
    {
        lock (_gate)
        {
            var hit = tab is { } t
                ? _tabs.Keys.Where(k => k.Tab == t).Select(k => ((string, int)?)k).FirstOrDefault()
                : _tabs.Where(kv => kv.Value.Active && kv.Value.Visible).Select(kv => ((string, int)?)kv.Key).FirstOrDefault();
            return hit is { } k2 ? TabKey(k2.Item1, k2.Item2) : null;
        }
    }

    /// <summary>
    /// A tab's visible text as a walk: one line per text block, its ref the line's hash (numbered when it repeats, so
    /// equal lines stay distinct), in the view of this tab's page (tab id + a hash of host and path; a new page is a new
    /// view, and the path itself is never kept).
    /// </summary>
    public static UiaWalk ToWalk(string browser, int tab, ulong pageRef, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var snap = new List<SnapLine>(lines.Count);
        var strings = new List<string>(lines.Count);
        foreach (var raw in lines)
        {
            var line = raw?.Trim() ?? string.Empty;
            if (line.Length == 0)
            {
                continue;
            }

            var n = seen.GetValueOrDefault(line);
            seen[line] = n + 1;
            strings.Add(line);
            snap.Add(new SnapLine(XxHash64.Hash(line, (ulong)n), Depth: 0, Role: 0, NameId: strings.Count - 1, ValueId: -1));
        }

        var window = ((long)Array.IndexOf(Browsers, browser) + 1 << 40) | (uint)tab;
        return new UiaWalk(new ViewKey(window, pageRef), snap, strings);
    }

    /// <summary>A page's identity for its view: host + path (no query or fragment), hashed.</summary>
    public static ulong PageRef(string? host, string? url)
    {
        var path = url is not null && Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath : "/";
        return XxHash64.Hash((host ?? string.Empty) + path);
    }

    private bool Blocked(string browser, int tab, TabContext page, DateTimeOffset now) =>
        _blocks.Blocking(now, BrowserExe[browser], page.Host?.Reveal(), TabKey(browser, tab)) is not null;

    public static string TabKey(string browser, int tab) => browser + ":" + tab.ToString(CultureInfo.InvariantCulture);

    private BlockState State(string browser, DateTimeOffset now)
    {
        var exe = BrowserExe[browser];
        var appBlocked = _blocks.Blocks.Any(b => b.Kind == BlockKind.App && b.Value is { } g && Core.Apps.Glob.IsMatch(g, exe));
        var paused = _blocks.Paused(now) || appBlocked;
        var until = paused && !appBlocked && _blocks.Blocks.Where(b => b.Kind == BlockKind.Pause).All(b => b.Until is not null)
            ? _blocks.Blocks.Where(b => b.Kind == BlockKind.Pause && b.Until > now).Select(b => b.Until).Max()
            : null;
        var prefix = browser + ":";
        return new BlockState
        {
            Paused = paused,
            PausedUntil = until,
            ExcludedHosts = [.. _blocks.Blocks.Where(b => b.Kind == BlockKind.Host && b.Value is not null).Select(b => b.Value!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            PausedTabs = [.. _blocks.Blocks.Where(b => b.Kind == BlockKind.Tab && b.Value?.StartsWith(prefix, StringComparison.Ordinal) == true && (b.Until is not { } u || now < u))
                .Select(b => int.Parse(b.Value![prefix.Length..], CultureInfo.InvariantCulture)).Distinct().Order()],
        };
    }

    /// <summary>A popup click is the user's own request, so it may add and remove site and tab blocks.</summary>
    private (bool Ok, string? Reason) Apply(string browser, BrowserBlockRequest r, DateTimeOffset now)
    {
        if (r.RequestId.Length is 0 or > 64 || r.Op is not ("add" or "remove") || r.Kind is not ("site" or "tab")
            || (r.Kind == "site" && r.Host is null) || (r.Kind == "tab" && r.Tab is null)
            || (r.Minutes is { } m && (r.Kind != "tab" || m is < 1 or > 1440)))
        {
            return (false, "invalid");
        }

        if (r.Kind == "tab" && r.Op == "add" && !_tabs.ContainsKey((browser, r.Tab!.Value)))
        {
            return (false, "unknown_tab");
        }

        var kind = r.Kind == "site" ? BlockKind.Host : BlockKind.Tab;
        var value = r.Kind == "site" ? r.Host! : TabKey(browser, r.Tab!.Value);
        if (r.Op == "add")
        {
            if (!_blocks.Blocks.Any(b => b.Kind == kind && b.Value == value))
            {
                _blocks.Add(new CaptureBlock { Id = "", Kind = kind, Value = value, Until = r.Minutes is { } mins ? now.AddMinutes(mins) : null, AddedBy = "user", AddedAt = now });
            }

            return (true, null);
        }

        var ids = _blocks.Blocks.Where(b => b.Kind == kind && b.Value == value).Select(b => b.Id).ToList();
        foreach (var id in ids)
        {
            _blocks.Remove(id, byUser: true);
        }

        return (true, null);
    }
}
