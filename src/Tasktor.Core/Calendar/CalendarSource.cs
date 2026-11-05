using Tasktor.Contracts;
using System.Text.Json;
using Tasktor.Contracts.Calendar;
using Tasktor.Contracts.Config;
using Tasktor.Contracts.Platform;
using Tasktor.Core.Storage;

namespace Tasktor.Core.Calendar;

/// <summary>One input's last read: a file name or plugin calendar id, ok or the reason it failed (never contents).</summary>
public sealed record CalendarFeedStatus(string Name, bool Ok, string? Error);

/// <summary>
/// U2 <c>CalendarSource</c> (E1, E2, I3, I13): local <c>.ics</c> files, the <c>tasktor.calendar</c> plugin's
/// <c>events.json</c> and the deadlines file → the next 14 days, ≤ 200 instances, in memory only. No network (D-S2).
/// <see cref="Read"/> runs off the loop.
/// </summary>
public sealed class CalendarSource : ITableSource<EventKey, CalEvent>
{
    public const string PluginId = "tasktor.calendar";

    private readonly Func<CalendarSettings?> _settings;
    private readonly StoreLayout _store;
    private readonly IClock _clock;
    private readonly TimeZoneInfo _local;
    private volatile IReadOnlyList<CalendarFeedStatus> _feeds = [];

    public CalendarSource(Func<CalendarSettings?> settings, StoreLayout store, IClock clock, TimeZoneInfo? local = null)
    {
        _settings = settings;
        _store = store;
        _clock = clock;
        _local = local ?? TimeZoneInfo.Local;
    }

    public string Name => "calendar";

    public IReadOnlyList<CalendarFeedStatus> Feeds => _feeds;

    public string PluginEvents => Path.Combine(_store.PluginData(PluginId), "events.json");

    public string DeadlinesPath => _settings()?.DeadlinesFile is { Length: > 0 } p ? p : Path.Combine(_store.Root, "deadlines.txt");

    public CalendarMarkers Markers
    {
        get
        {
            var s = _settings();
            return new CalendarMarkers(s?.DeadlineMarker ?? CalendarMarkers.Default.DeadlineMarker, s?.EventMarker ?? CalendarMarkers.Default.EventMarker);
        }
    }

    /// <summary>Every file this source reads, for the change watch.</summary>
    public IReadOnlyList<string> Inputs => [.. _settings()?.IcsFiles ?? [], PluginEvents, DeadlinesPath];

    public void Read(ICollection<KeyValuePair<EventKey, CalEvent>> into, ColumnSet columns)
    {
        ArgumentNullException.ThrowIfNull(into);
        var now = _clock.UtcNow;
        var to = now + CalendarExpansion.Window;
        var markers = Markers;
        var rows = new List<CalEvent>();
        var feeds = new List<CalendarFeedStatus>();
        foreach (var path in _settings()?.IcsFiles ?? [])
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var text = ReadText(path, IcsReader.MaxBytes, out var error);
            if (text is null)
            {
                feeds.Add(new(name, false, error));
                continue;
            }

            rows.AddRange(CalendarExpansion.Expand(IcsReader.Parse(text), now, to, _local, markers, name));
            feeds.Add(new(name, true, null));
        }

        if (File.Exists(PluginEvents))
        {
            var text = ReadText(PluginEvents, IcsReader.MaxBytes, out var error);
            try
            {
                if (text is not null && JsonSerializer.Deserialize(text, ContractsJsonContext.Default.CalendarFile) is { } file)
                {
                    rows.AddRange(CalendarExpansion.FromPlugin(file, now, to, markers));
                    feeds.AddRange((file.Calendars ?? []).Select(c => new CalendarFeedStatus(c.Name, c.Status == "ok", c.Error)));
                }
                else
                {
                    feeds.Add(new(PluginId, false, error ?? "unreadable"));
                }
            }
            catch (JsonException)
            {
                feeds.Add(new(PluginId, false, "not a tasktor.calendar/1 file"));
            }
        }

        if (File.Exists(DeadlinesPath) && ReadText(DeadlinesPath, 1024 * 1024, out _) is { } dl)
        {
            var (deadlines, bad) = DeadlinesFile.Parse(dl, _local);
            rows.AddRange(deadlines.Where(d => d.Start > now - TimeSpan.FromDays(1) && d.Start < to));
            feeds.Add(new("deadlines", bad == 0, bad == 0 ? null : bad + " lines not understood"));
        }

        foreach (var r in CalendarExpansion.Cap(rows))
        {
            into.Add(new(r.Key, r));
        }

        _feeds = feeds;
    }

    private static string? ReadText(string path, int max, out string? error)
    {
        error = null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                error = "not found";
                return null;
            }

            if (info.Length > max)
            {
                error = "too big";
                return null;
            }

            return File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = "cannot be read";
            return null;
        }
    }
}
