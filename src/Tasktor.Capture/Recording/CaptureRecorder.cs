using System.Text.Json;
using System.Text.Json.Serialization;
using Tasktor.Capture.Browser;
using Tasktor.Capture.Deep;

namespace Tasktor.Capture.Recording;

/// <summary>Vault record kinds (the vault stores bytes; the kind byte says what they are).</summary>
public static class RecordKind
{
    public const byte BrowserContext = 1;
    public const byte KeyRun = 2;
    public const byte ViewDiff = 3;
    public const byte Media = 4;
    public const byte Document = 5;
    public const byte Command = 6;
}

/// <summary>
/// What the vault keeps about a browser tab: its host and field kind, and in T1 (D-C22) the full address and title, both
/// scrubbed for secrets (the address only in its query and fragment: its own path is the page, §5.3). Never field values.
/// Page text is a separate view diff (D-C18).
/// </summary>
/// <summary>A logged command line (C22), scrubbed for secrets; <c>App</c> is null when only shell history saw it.</summary>
public sealed record CommandRecord(string? App, string Text, string Source, string Actor, int Redactions = 0);

public sealed record BrowserContextRecord(string Browser, int Tab, string? Host, string Field, bool Visible, bool Active, bool Queued, string? Url = null, string? Title = null, bool Private = false);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BrowserContextRecord))]
[JsonSerializable(typeof(KeyRun))]
[JsonSerializable(typeof(ViewDiff))]
[JsonSerializable(typeof(MediaState))]
[JsonSerializable(typeof(ActivityDocument))]
[JsonSerializable(typeof(CommandRecord))]
internal sealed partial class RecordJsonContext : JsonSerializerContext;

/// <summary>
/// Writes capture records into the <see cref="RawVault"/> (AES-GCM per record, 7-day retention), honouring the capture
/// blocks at write time: nothing is written while paused, for an excluded app or site, or for a paused tab. Counts today's
/// records for the card and forgets ranges on request. Thread-safe (the pipe sessions call it).
/// </summary>
public sealed class CaptureRecorder
{
    private readonly RawVault _vault;
    private readonly CaptureBlocks _blocks;
    private readonly Func<DateTimeOffset> _now;
    private readonly Lock _gate = new();
    private DateOnly _day;
    private long _today;

    public CaptureRecorder(RawVault vault, CaptureBlocks blocks, Func<DateTimeOffset> now)
    {
        _vault = vault;
        _blocks = blocks;
        _now = now;
        var n = now();
        _day = DateOnly.FromDateTime(n.UtcDateTime);
        _today = vault.Read(new DateTimeOffset(n.UtcDateTime.Date, TimeSpan.Zero), n.AddDays(1)).LongCount();
    }

    public long Skipped { get; private set; }

    /// <summary>The blocks this recorder applies (the key-run suppressor checks the same ones).</summary>
    public CaptureBlocks Blocks => _blocks;

    /// <summary>Scrubs tab titles and address queries (the user's own patterns come from settings).</summary>
    public SecretRedactor Redactor { get; init; } = new();

    public long RecordsToday
    {
        get
        {
            lock (_gate)
            {
                Roll(_now());
                return _today;
            }
        }
    }

    private void Roll(DateTimeOffset now)
    {
        var d = DateOnly.FromDateTime(now.UtcDateTime);
        if (d != _day)
        {
            (_day, _today) = (d, 0);
        }
    }

    /// <summary>A tab's context changed: record it unless a block applies. The browser's own exe is checked as an app.</summary>
    public bool Browser(string browser, int tab, TabContext ctx, bool queued = false)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var host = ctx.Host?.Reveal();
        var exe = browser == "edge" ? "msedge.exe" : "chrome.exe";
        var title = ctx.Title is { } t ? Redactor.Scrub(t.Reveal()).Text : null;
        var rec = new BrowserContextRecord(browser, tab, host, ctx.Secure ? "secure" : "other", ctx.Visible, ctx.Active, queued, ScrubUrl(ctx.Url?.Reveal()), title, ctx.Private);
        return Write(exe, host, BrowserEndpoint.TabKey(browser, tab), RecordKind.BrowserContext, JsonSerializer.SerializeToUtf8Bytes(rec, RecordJsonContext.Default.BrowserContextRecord));
    }

    /// <summary>Secrets in the query and fragment are redacted; the scheme, host and path (the page itself) are kept.</summary>
    private string? ScrubUrl(string? url)
    {
        if (url is null)
        {
            return null;
        }

        var cut = url.IndexOfAny(['?', '#']);
        return cut < 0 ? url : url[..cut] + Redactor.Scrub(url[cut..]).Text;
    }

    /// <summary>A closed key run (already suppressed and redacted by the Deep pipeline).</summary>
    public bool KeyRun(KeyRun run, string app, string? host)
    {
        ArgumentNullException.ThrowIfNull(run);
        return Write(app, host, null, RecordKind.KeyRun, JsonSerializer.SerializeToUtf8Bytes(run, RecordJsonContext.Default.KeyRun));
    }

    /// <summary>A view diff (lines already redacted), for the app (and site) it was read from.</summary>
    public bool View(ViewDiff diff, string app, string? host)
    {
        ArgumentNullException.ThrowIfNull(diff);
        return Write(app, host, null, RecordKind.ViewDiff, JsonSerializer.SerializeToUtf8Bytes(diff, RecordJsonContext.Default.ViewDiff));
    }

    /// <summary>A now-playing change; the playing app's blocks apply.</summary>
    public bool Media(MediaState media)
    {
        ArgumentNullException.ThrowIfNull(media);
        return Write(media.App, null, null, RecordKind.Media, JsonSerializer.SerializeToUtf8Bytes(media, RecordJsonContext.Default.MediaState));
    }

    /// <summary>A closed activity document. Its parts already passed their own blocks; a pause or schedule still stops it.</summary>
    public bool Document(ActivityDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return Write(null, null, null, RecordKind.Document, JsonSerializer.SerializeToUtf8Bytes(doc, RecordJsonContext.Default.ActivityDocument));
    }

    /// <summary>
    /// A command line (C22). Blocked when any of <paramref name="apps"/> is: the terminal, the shell, or the foreground app
    /// it was seen under, so excluding any one of them keeps its commands out.
    /// </summary>
    public bool Command(CommandRecord command, IReadOnlyList<string> apps)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(apps);
        return WriteAny(apps, null, null, RecordKind.Command, JsonSerializer.SerializeToUtf8Bytes(command, RecordJsonContext.Default.CommandRecord));
    }

    private bool _secure;

    /// <summary>True while nothing may be written: a secure desktop, a locked or disconnected session, or not yet known.</summary>
    public bool Secure
    {
        get
        {
            lock (_gate)
            {
                return _secure;
            }
        }
    }

    /// <summary>
    /// The secure gate (C §5, D-C22: never the secure desktop). Taken under the write lock, so it is ordered with every
    /// write: once <c>SetSecure(true)</c> returns, no record lands until it is cleared. Any thread may close it (a session
    /// notice, first); only the capture loop clears it, after the desktop probe proves the user's own desktop.
    /// </summary>
    public void SetSecure(bool secure)
    {
        lock (_gate)
        {
            _secure = secure;
        }
    }

    private bool Write(string? app, string? host, string? tab, byte kind, byte[] body) => WriteAny([app], host, tab, kind, body);

    private bool WriteAny(IReadOnlyList<string?> apps, string? host, string? tab, byte kind, byte[] body)
    {
        lock (_gate)
        {
            if (_secure)
            {
                Skipped++;
                return false;
            }

            var now = _now();
            _blocks.Expire(now);
            if (apps.Count == 0 ? _blocks.Blocking(now, null, host, tab) is not null : apps.Any(a => _blocks.Blocking(now, a, host, tab) is not null))
            {
                Skipped++;
                return false;
            }

            _vault.Append(now, kind, kind == RecordKind.Document ? body : RecordSource.Tag(body, apps, host)); // a document lists its own sources
            Roll(now);
            _today++;
            return true;
        }
    }

    /// <summary>
    /// C21.4 <c>capture.forget</c> by app (exe glob) or site (and its subdomains): every record from it, and every activity
    /// document that included it, whole; returns how many. Records written before sources were tagged name none and stay
    /// until the 7-day retention drops them.
    /// </summary>
    public long ForgetMatching(string? app, string? host)
    {
        if (app is null && host is null)
        {
            return 0;
        }

        lock (_gate)
        {
            var now = _now();
            var removed = _vault.ForgetWhere(r => RecordSource.Matches(r.Body, app, host));
            Roll(now);
            var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
            _today = _vault.Read(today, now.AddSeconds(1)).LongCount();
            return removed;
        }
    }

    /// <summary><c>capture.forget</c>: remove every record from <paramref name="since"/> to now; returns how many.</summary>
    public long ForgetSince(DateTimeOffset since)
    {
        lock (_gate)
        {
            var now = _now();
            var removed = _vault.Forget(since, now.AddSeconds(1));
            Roll(now);
            _today = Math.Max(0, _today - removed);
            return removed;
        }
    }
}
