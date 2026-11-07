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
}

/// <summary>
/// What the vault keeps about a browser tab: its host and field kind, and in T1 (D-C22) the full address and title, both
/// scrubbed for secrets (the address only in its query and fragment: its own path is the page, §5.3). Never field values.
/// Page text is a separate view diff (D-C18).
/// </summary>
public sealed record BrowserContextRecord(string Browser, int Tab, string? Host, string Field, bool Visible, bool Active, bool Queued, string? Url = null, string? Title = null, bool Private = false);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BrowserContextRecord))]
[JsonSerializable(typeof(KeyRun))]
[JsonSerializable(typeof(ViewDiff))]
[JsonSerializable(typeof(MediaState))]
[JsonSerializable(typeof(ActivityDocument))]
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

    private bool Write(string? app, string? host, string? tab, byte kind, byte[] body)
    {
        lock (_gate)
        {
            var now = _now();
            _blocks.Expire(now);
            if (_blocks.Blocking(now, app, host, tab) is not null)
            {
                Skipped++;
                return false;
            }

            _vault.Append(now, kind, body);
            Roll(now);
            _today++;
            return true;
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
