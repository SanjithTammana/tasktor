using System.Globalization;
using System.Text;

namespace Tasktor.Capture.Deep;

public enum MediaStatus : byte
{
    Stopped,
    Playing,
    Paused,
}

/// <summary>C §3: now-playing for one app (SMTC), with <see cref="Audible"/> from the session's peak meter (§5.6).</summary>
public sealed record MediaState(string App, string? Title, string? Artist, MediaStatus Status, long PositionMs, bool Audible);

/// <summary>C §3: a click, its target's role + name (≤ 80 chars, filled asynchronously), and who clicked.</summary>
public sealed record ClickEvent(DateTimeOffset At, byte Button, int X, int Y, string? Target, Actor Actor)
{
    public const int MaxTarget = 80;
}

/// <summary>One visible top-level window in a <see cref="VisibleSet"/>.</summary>
public readonly record struct VisibleWindow(long Window, string App, long VisiblePx, bool IsForeground);

/// <summary>C §3: what is on screen, recomputed on foreground / move-size end / minimize. <see cref="ScreenPx"/> = all monitors.</summary>
public sealed record VisibleSet(DateTimeOffset At, long ScreenPx, IReadOnlyList<VisibleWindow> Windows);

/// <summary>A side track opened (from <see cref="At"/>, backdated to when it began to qualify) or closed (at its last qualifying moment).</summary>
public readonly record struct TrackChange(int Track, string App, bool Opened, DateTimeOffset At);

/// <summary>
/// C §4 <c>TrackManager</c> (C20.3): track 1 is the foreground. An app that is not in front and either shows a window
/// covering ≥ 25 % of the screen or is audible, continuously for ≥ 60 s, gets a side track (2, 3, … lowest free), which
/// closes 30 s after it stops qualifying. Side tracks are keyed by app: two activities of one app (a YouTube tab behind
/// a chess tab of the same browser) share track 1 until the extension reports tab media. Logic thread only.
/// </summary>
public sealed class TrackManager
{
    public const double ScreenShare = 0.25;
    public static readonly TimeSpan OpenAfter = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan CloseAfter = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, Candidate> _apps = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _audible = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _large = new(StringComparer.OrdinalIgnoreCase);
    private string? _foreground;

    private sealed class Candidate
    {
        public DateTimeOffset? QualifiedSince { get; set; }

        public DateTimeOffset? LastQualified { get; set; }

        public int Track { get; set; }
    }

    /// <summary>The foreground app (track 1).</summary>
    public string? Foreground => _foreground;

    /// <summary>Open side tracks: track → app.</summary>
    public IReadOnlyDictionary<int, string> SideTracks => _apps.Where(p => p.Value.Track > 0).ToDictionary(p => p.Value.Track, p => p.Key);

    public IReadOnlyList<TrackChange> OnForeground(string app, DateTimeOffset at)
    {
        _foreground = app;
        return Evaluate(at);
    }

    public IReadOnlyList<TrackChange> On(VisibleSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        _large.Clear();
        if (set.ScreenPx > 0)
        {
            foreach (var w in set.Windows)
            {
                if (w.IsForeground)
                {
                    _foreground = w.App;
                }
                else if (w.VisiblePx >= ScreenShare * set.ScreenPx)
                {
                    _large.Add(w.App);
                }
            }
        }

        return Evaluate(set.At);
    }

    public IReadOnlyList<TrackChange> On(MediaState media, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(media);
        if (media.Audible)
        {
            _audible.Add(media.App);
        }
        else
        {
            _audible.Remove(media.App);
        }

        return Evaluate(at);
    }

    /// <summary>Time passes (the host's 1 Hz tick): opens and closes happen on time without a new event.</summary>
    public IReadOnlyList<TrackChange> Tick(DateTimeOffset at) => Evaluate(at);

    private IReadOnlyList<TrackChange> Evaluate(DateTimeOffset at)
    {
        var changes = new List<TrackChange>();
        foreach (var app in _large.Concat(_audible))
        {
            if (!_apps.ContainsKey(app))
            {
                _apps[app] = new Candidate();
            }
        }

        foreach (var (app, c) in _apps.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToArray())
        {
            var inFront = string.Equals(app, _foreground, StringComparison.OrdinalIgnoreCase);
            var qualifies = !inFront && (_large.Contains(app) || _audible.Contains(app));
            if (qualifies)
            {
                c.QualifiedSince ??= at;
                c.LastQualified = at;
                if (c.Track == 0 && at - c.QualifiedSince >= OpenAfter)
                {
                    c.Track = FreeTrack();
                    changes.Add(new TrackChange(c.Track, app, true, c.QualifiedSince.Value));
                }

                continue;
            }

            if (c.QualifiedSince is not null)
            {
                c.LastQualified = at; // state only changes at events: it qualified right up to this one
            }

            c.QualifiedSince = null;
            var closeNow = inFront || c.LastQualified is not { } last || at - last >= CloseAfter;
            if (c.Track > 0 && closeNow)
            {
                changes.Add(new TrackChange(c.Track, app, false, inFront ? at : c.LastQualified ?? at));
                c.Track = 0;
            }

            if (c.Track == 0)
            {
                _apps.Remove(app);
            }
        }

        return changes;
    }

    private int FreeTrack()
    {
        var used = _apps.Values.Select(c => c.Track).ToHashSet();
        var t = 2;
        while (used.Contains(t))
        {
            t++;
        }

        return t;
    }
}

/// <summary>One section of an activity document: a heading and text, ≤ 4 KB of UTF-8.</summary>
public sealed record DocSection(string Heading, string Text);

/// <summary>The unstructured representation of one activity on one track (C21 indexes it). Stays in the capture process and the vault.</summary>
public sealed record ActivityDocument(int Track, DateTimeOffset Start, DateTimeOffset End, IReadOnlyList<DocSection> Sections)
{
    /// <summary>Bytes cut by the 4 KB section cap.</summary>
    public long Truncated { get; init; }

    /// <summary>The apps whose text, runs, commands or media are in it (C21.4 forgets the whole document by any of them).</summary>
    public IReadOnlyList<string> Apps { get; set; } = [];

    /// <summary>The sites whose text or runs are in it.</summary>
    public IReadOnlyList<string> Hosts { get; set; } = [];

    /// <summary>Web pages it showed (scrubbed addresses), first seen first: a search hit can reopen them (C21).</summary>
    public IReadOnlyList<string> Links { get; set; } = [];

    /// <summary>C19.3: the activity's pointer features (kept in the index after the raw path's 7 days); null without pointer input.</summary>
    public PointerFeatures? Pointer { get; init; }
}

/// <summary>
/// C §4 <c>ActivityDocumentBuilder</c>: an open document per track. Typing, view text, clicks and media become sections
/// (≤ 4 KB each, the rest counted as truncated). View text is compressed to "first seen" (the first diff of a view in
/// this document) plus new lines only: a line already in the document is never repeated, and removals are not text.
/// Inputs are already redacted (the Redactor runs before any vault write). Logic thread only.
/// </summary>
public sealed class ActivityDocumentBuilder
{
    public const int MaxSectionBytes = 4 * 1024;

    /// <summary>Links kept per document (the first pages seen; a hit offers the first).</summary>
    public const int MaxLinks = 8;

    private readonly Dictionary<int, Doc> _open = [];

    private sealed class Doc(int track, DateTimeOffset start)
    {
        public int Track { get; } = track;

        public DateTimeOffset Start { get; } = start;

        public DateTimeOffset Last { get; set; } = start;

        public List<(string Heading, StringBuilder Text, int Bytes)> Sections { get; } = [];

        public HashSet<ulong> SeenLines { get; } = [];

        public HashSet<ViewKey> SeenViews { get; } = [];

        public string? LastMedia { get; set; }

        public long Truncated { get; set; }

        public SortedSet<string> Apps { get; } = new(StringComparer.OrdinalIgnoreCase);

        public SortedSet<string> Hosts { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Links { get; } = [];

        public PointerFeatureBuilder Pointer { get; } = new();
    }

    public bool IsOpen(int track) => _open.ContainsKey(track);

    /// <summary>When <paramref name="track"/>'s open document started, or null when none is open (tells documents apart).</summary>
    public DateTimeOffset? StartOf(int track) => _open.TryGetValue(track, out var doc) ? doc.Start : null;

    /// <summary>Pointer input, already accepted by the recorder: it feeds the document's features (C19.3), not its text.</summary>
    public void Add(int track, PointerEvent pointer, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(pointer);
        var doc = DocFor(track, at);
        if (at > doc.Last)
        {
            doc.Last = at;
        }

        doc.Pointer.Add(pointer);
    }

    /// <summary>A web page the open document showed: kept once, http(s) only, at most <see cref="MaxLinks"/>.</summary>
    public void Link(int track, string? url)
    {
        if (!_open.TryGetValue(track, out var doc) || doc.Links.Count >= MaxLinks || !IsWebLink(url) || doc.Links.Contains(url!, StringComparer.Ordinal))
        {
            return;
        }

        doc.Links.Add(url!);
    }

    /// <summary>An absolute http or https address, nothing else (a hit must never launch a file, script or other scheme).</summary>
    public static bool IsWebLink(string? url) =>
        url is { Length: > 0 and <= 2048 } && Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) && u.Host.Length > 0;

    /// <summary>The source of what was just added to <paramref name="track"/>'s open document (no-op when none is open).</summary>
    public void Note(int track, string? app, string? host)
    {
        if (!_open.TryGetValue(track, out var doc))
        {
            return;
        }

        if (app is { Length: > 0 })
        {
            doc.Apps.Add(app);
        }

        if (host is { Length: > 0 })
        {
            doc.Hosts.Add(host);
        }
    }

    public void Add(int track, KeyRun run, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(run);
        var who = run.Actor.Kind == ActorKind.Human ? string.Empty : " (" + run.Actor + ")";
        var line = run.Text is { } text && run.Suppressed == Suppression.None
            ? text
            : "[" + run.Suppressed.ToString().ToLowerInvariant() + ", " + run.Keys.ToString(CultureInfo.InvariantCulture) + " keys]";
        Append(DocFor(track, at), "typed", line + who, at);
    }

    /// <summary>A command line (C22), already scrubbed: one line under "ran", inner lines of a multi-line command joined.</summary>
    public void Add(int track, string command, Actor actor, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(command);
        var who = actor.Kind is ActorKind.Human or ActorKind.Unknown ? string.Empty : " (" + actor + ")";
        Append(DocFor(track, at), "ran", command.ReplaceLineEndings(" ⏎ ") + who, at);
    }

    public void Add(int track, ViewDiff diff)
    {
        ArgumentNullException.ThrowIfNull(diff);
        var doc = DocFor(track, diff.At);
        var heading = (doc.SeenViews.Add(diff.View) ? "first seen " : "new in ") + "view " + diff.View.Window.ToString("x", CultureInfo.InvariantCulture);
        foreach (var l in diff.Added)
        {
            var text = l.Value is { Length: > 0 } v ? (l.Name is { Length: > 0 } n ? n + ": " + v : v) : l.Name;
            if (text is { Length: > 0 } && doc.SeenLines.Add(XxHash64.Hash(text)))
            {
                Append(doc, heading, text, diff.At);
            }
        }
    }

    public void Add(int track, ClickEvent click)
    {
        ArgumentNullException.ThrowIfNull(click);
        if (click.Target is not { Length: > 0 } target)
        {
            return;
        }

        var name = target.Length > ClickEvent.MaxTarget ? target[..ClickEvent.MaxTarget] : target;
        Append(DocFor(track, click.At), "clicked", name + (click.Actor.Kind == ActorKind.Human ? string.Empty : " (" + click.Actor + ")"), click.At);
    }

    public void Add(int track, MediaState media, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(media);
        var doc = DocFor(track, at);
        var symbol = media.Status switch { MediaStatus.Playing => "▶", MediaStatus.Paused => "⏸", _ => "■" };
        var line = symbol + " " + (media.Title ?? "(untitled)") + (media.Artist is { Length: > 0 } a ? " · " + a : string.Empty) + " (" + media.App + ")";
        if (line != doc.LastMedia)
        {
            doc.LastMedia = line;
            Append(doc, "media", line, at);
        }
    }

    /// <summary>The activity (or the track) ended: the finished document, or null if the track had none open.</summary>
    public ActivityDocument? Close(int track, DateTimeOffset at)
    {
        if (!_open.Remove(track, out var doc))
        {
            return null;
        }

        var end = at < doc.Last ? doc.Last : at;
        return new ActivityDocument(track, doc.Start, end, [.. doc.Sections.Select(s => new DocSection(s.Heading, s.Text.ToString()))]) { Truncated = doc.Truncated, Apps = [.. doc.Apps], Hosts = [.. doc.Hosts], Links = [.. doc.Links], Pointer = doc.Pointer.Build(end - doc.Start) };
    }

    private Doc DocFor(int track, DateTimeOffset at)
    {
        if (!_open.TryGetValue(track, out var doc))
        {
            _open[track] = doc = new Doc(track, at);
        }

        return doc;
    }

    private static void Append(Doc doc, string heading, string line, DateTimeOffset at)
    {
        if (at > doc.Last)
        {
            doc.Last = at;
        }

        var i = doc.Sections.FindIndex(s => s.Heading == heading);
        if (i < 0)
        {
            doc.Sections.Add((heading, new StringBuilder(), 0));
            i = doc.Sections.Count - 1;
        }

        var (h, text, bytes) = doc.Sections[i];
        var add = (text.Length > 0 ? "\n" : string.Empty) + line;
        var addBytes = Encoding.UTF8.GetByteCount(add);
        if (bytes + addBytes > MaxSectionBytes)
        {
            doc.Truncated += addBytes;
            return;
        }

        text.Append(add);
        doc.Sections[i] = (h, text, bytes + addBytes);
    }
}
