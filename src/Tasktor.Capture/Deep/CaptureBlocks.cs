using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tasktor.Core.Apps;

namespace Tasktor.Capture.Deep;

/// <summary>U4 D-G6 block kinds.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BlockKind>))]
public enum BlockKind
{
    [JsonStringEnumMemberName("pause")] Pause,
    [JsonStringEnumMemberName("schedule")] Schedule,
    [JsonStringEnumMemberName("app")] App,
    [JsonStringEnumMemberName("host")] Host,
    [JsonStringEnumMemberName("tab")] Tab,
}

/// <summary>
/// One capture block. Pause: <see cref="Until"/> (null = until resumed). Schedule: blocked weekly ranges
/// (<see cref="Days"/> like <c>Mon-Fri</c>, <see cref="From"/> / <see cref="To"/> <c>HH:mm</c>, local time; a range may
/// cross midnight). App: exe glob. Host: hostname (and its subdomains). Tab: a tab id until the tab closes.
/// </summary>
public sealed record CaptureBlock
{
    public required string Id { get; init; }

    public required BlockKind Kind { get; init; }

    public DateTimeOffset? Until { get; init; }

    public string? Days { get; init; }

    public string? From { get; init; }

    public string? To { get; init; }

    public string? Value { get; init; }

    /// <summary><c>user</c> or <c>rule:&lt;id&gt;</c>: who added it (rules may add, never lift).</summary>
    public required string AddedBy { get; init; }

    public DateTimeOffset AddedAt { get; init; }
}

public sealed record CaptureBlocksFile
{
    public int SchemaVersion { get; init; } = 1;

    public List<CaptureBlock> Blocks { get; init; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CaptureBlocksFile))]
internal sealed partial class CaptureJsonContext : JsonSerializerContext;

/// <summary>
/// D-G6 capture blocks: pause for a while, a weekly schedule, this app, this site, this tab. Every block is shown in the
/// Capture card's Blocks list. A rule may add a block (e.g. pause while in a call) but only the user removes one: a rule
/// lifting a privacy block would be a silent way to turn recording back on. Expired pauses fall off by themselves.
/// Persisted to <c>state\capture-blocks.json</c>.
/// </summary>
public sealed class CaptureBlocks
{
    private readonly string? _path;
    private readonly TimeZoneInfo _local;
    private CaptureBlocksFile _file = new();
    private int _next = 1;

    public CaptureBlocks(string? path = null, TimeZoneInfo? local = null)
    {
        _path = path;
        _local = local ?? TimeZoneInfo.Local;
        if (path is not null && File.Exists(path))
        {
            try
            {
                _file = JsonSerializer.Deserialize(File.ReadAllBytes(path), CaptureJsonContext.Default.CaptureBlocksFile) ?? new();
            }
            catch (JsonException)
            {
                _file = new(); // an unreadable file never lifts anything silently: the caller shows "blocks reset"
                Reset = true;
            }

            _next = _file.Blocks.Count + 1;
        }
    }

    public bool Reset { get; }

    public IReadOnlyList<CaptureBlock> Blocks => _file.Blocks;

    public CaptureBlock Add(CaptureBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        var b = block with { Id = block.Id.Length > 0 ? block.Id : "b" + (_next++).ToString(CultureInfo.InvariantCulture) };
        _file.Blocks.RemoveAll(x => x.Id == b.Id);
        _file.Blocks.Add(b);
        Save();
        return b;
    }

    /// <summary>Remove a block. Only the user may; a rule's request is refused (returns false).</summary>
    public bool Remove(string id, bool byUser)
    {
        if (!byUser)
        {
            return false;
        }

        var removed = _file.Blocks.RemoveAll(b => b.Id == id) > 0;
        if (removed)
        {
            Save();
        }

        return removed;
    }

    /// <summary>A browser tab closed: its tab pause ends.</summary>
    public void TabClosed(string tab)
    {
        if (_file.Blocks.RemoveAll(b => b.Kind == BlockKind.Tab && b.Value == tab) > 0)
        {
            Save();
        }
    }

    /// <summary>Drop pauses whose time has passed.</summary>
    public void Expire(DateTimeOffset now)
    {
        if (_file.Blocks.RemoveAll(b => b.Kind is BlockKind.Pause or BlockKind.Tab && b.Until is { } u && u <= now) > 0)
        {
            Save();
        }
    }

    /// <summary>The kind of the first block that applies now, or null when capture may run.</summary>
    public BlockKind? Blocking(DateTimeOffset now, string? app, string? host, string? tab)
    {
        foreach (var b in _file.Blocks)
        {
            var hit = b.Kind switch
            {
                BlockKind.Pause => b.Until is not { } u || now < u,
                BlockKind.Schedule => InSchedule(b, now),
                BlockKind.App => app is not null && b.Value is { } g && Glob.IsMatch(g, app),
                BlockKind.Host => host is not null && b.Value is { } h && (host.Equals(h, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase)),
                BlockKind.Tab => tab is not null && b.Value == tab && (b.Until is not { } tu || now < tu),
                _ => false,
            };
            if (hit)
            {
                return b.Kind;
            }
        }

        return null;
    }

    /// <summary>True when the process should idle entirely (a pause or schedule; app / site / tab blocks are per run).</summary>
    public bool Paused(DateTimeOffset now) => Blocking(now, null, null, null) is BlockKind.Pause or BlockKind.Schedule;

    private bool InSchedule(CaptureBlock b, DateTimeOffset now)
    {
        if (b.From is null || b.To is null || !TimeSpan.TryParseExact(b.From, @"hh\:mm", CultureInfo.InvariantCulture, out var from)
            || !TimeSpan.TryParseExact(b.To, @"hh\:mm", CultureInfo.InvariantCulture, out var to))
        {
            return false;
        }

        var local = TimeZoneInfo.ConvertTime(now, _local);
        var t = local.TimeOfDay;
        if (from <= to)
        {
            return DayMatches(b.Days, local.DayOfWeek) && t >= from && t < to;
        }

        // crosses midnight: the part after midnight belongs to the previous day's range
        return (DayMatches(b.Days, local.DayOfWeek) && t >= from) || (DayMatches(b.Days, local.AddDays(-1).DayOfWeek) && t < to);
    }

    private static readonly string[] DayNames = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];

    /// <summary><c>Mon-Fri</c>, <c>Sat,Sun</c>, <c>daily</c> or null (every day).</summary>
    public static bool DayMatches(string? days, DayOfWeek day)
    {
        if (string.IsNullOrWhiteSpace(days) || days.Equals("daily", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var part in days.ToLowerInvariant().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var range = part.Split('-');
            var a = Array.IndexOf(DayNames, range[0][..Math.Min(3, range[0].Length)]);
            var z = range.Length > 1 ? Array.IndexOf(DayNames, range[1][..Math.Min(3, range[1].Length)]) : a;
            if (a < 0 || z < 0)
            {
                continue;
            }

            var d = (int)day;
            if (a <= z ? d >= a && d <= z : d >= a || d <= z)
            {
                return true;
            }
        }

        return false;
    }

    private void Save()
    {
        if (_path is not null)
        {
            Core.Storage.AtomicFile.Write(_path, JsonSerializer.SerializeToUtf8Bytes(_file, CaptureJsonContext.Default.CaptureBlocksFile));
        }
    }
}
