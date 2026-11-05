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
}

/// <summary>What the vault keeps about a browser tab: host-only context, never paths, values or page text (D-C18).</summary>
public sealed record BrowserContextRecord(string Browser, int Tab, string? Host, string Field, bool Visible, bool Active, bool Queued);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BrowserContextRecord))]
[JsonSerializable(typeof(KeyRun))]
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
        var rec = new BrowserContextRecord(browser, tab, host, ctx.Secure ? "secure" : "other", ctx.Visible, ctx.Active, queued);
        return Write(exe, host, BrowserEndpoint.TabKey(browser, tab), RecordKind.BrowserContext, JsonSerializer.SerializeToUtf8Bytes(rec, RecordJsonContext.Default.BrowserContextRecord));
    }

    /// <summary>A closed key run (already suppressed and redacted by the Deep pipeline).</summary>
    public bool KeyRun(KeyRun run, string app, string? host)
    {
        ArgumentNullException.ThrowIfNull(run);
        return Write(app, host, null, RecordKind.KeyRun, JsonSerializer.SerializeToUtf8Bytes(run, RecordJsonContext.Default.KeyRun));
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
