using System.Globalization;
using Tasktor.Contracts.Calendar;
using Tasktor.Contracts.Platform;

namespace Tasktor.Core.Calendar;

/// <summary>The markers that make a calendar event a deadline (E1) or an event-mode window (I13).</summary>
public sealed record CalendarMarkers(string DeadlineMarker = "Due", string EventMarker = "[focus]")
{
    public static CalendarMarkers Default { get; } = new();
}

/// <summary>Meeting links (U2): Teams, Zoom, Meet and Webex hosts, looked for in the location and URL fields only.</summary>
public static class MeetingLinks
{
    public static string? Find(params string?[] fields)
    {
        foreach (var field in fields)
        {
            if (string.IsNullOrEmpty(field))
            {
                continue;
            }

            foreach (var token in field.Split([' ', '\n', '\r', '\t', '<', '>', '"', '(', ')', ','], StringSplitOptions.RemoveEmptyEntries))
            {
                if ((token.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || token.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                    && Uri.TryCreate(token.TrimEnd('.', ';'), UriKind.Absolute, out var uri) && IsMeetingHost(uri.Host))
                {
                    return uri.ToString();
                }
            }
        }

        return null;
    }

    public static bool IsMeetingHost(string host)
    {
        var h = host.ToLowerInvariant();
        return h is "teams.microsoft.com" or "teams.live.com" or "zoom.us" or "meet.google.com"
            || h.EndsWith(".zoom.us", StringComparison.Ordinal) || h.EndsWith(".webex.com", StringComparison.Ordinal);
    }
}

/// <summary>
/// ICS → expanded instances in a window: recurrences in each event's own zone, EXDATE removed, RECURRENCE-ID overrides
/// applied (also when they move an instance into the window), cancelled instances dropped, at most
/// <see cref="MaxInstances"/> kept (the earliest). Instances that started before the window but have not ended are kept.
/// </summary>
public static class CalendarExpansion
{
    public const int MaxInstances = 200;
    public static readonly TimeSpan Window = TimeSpan.FromDays(14);

    public static List<CalEvent> Expand(IcsCalendar cal, DateTimeOffset from, DateTimeOffset to, TimeZoneInfo local, CalendarMarkers markers, string? calendar = null)
    {
        ArgumentNullException.ThrowIfNull(cal);
        ArgumentNullException.ThrowIfNull(markers);
        var zones = new IcsZones(cal.Zones, local);
        var list = new List<CalEvent>();
        var overrides = cal.Events.Where(e => e.RecurrenceId is not null).ToList();
        var overridden = overrides.GroupBy(e => e.Uid, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(o => zones.Resolve(o.RecurrenceId!.Value).UtcDateTime).ToHashSet(), StringComparer.Ordinal);

        foreach (var ev in cal.Events)
        {
            if (ev.RecurrenceId is not null || ev.Cancelled)
            {
                continue;
            }

            var length = Length(ev, zones);
            var tz = ev.Start.DateOnly || ev.Start.Utc ? null : ev.Start.TzId;
            var exdates = ev.ExDates.Select(x => zones.Resolve(x).UtcDateTime).ToHashSet();
            var exDays = ev.ExDates.Where(x => x.DateOnly).Select(x => x.Value.Date).ToHashSet();
            var skip = overridden.GetValueOrDefault(ev.Uid);
            var until = ev.Rule?.Until is { } u ? (u.Utc ? zones.Resolve(u) : zones.At(u.Value, tz)) : (DateTimeOffset?)null;
            var walls = ev.Rule is null ? [ev.Start.Value] : RecurrenceExpander.Expand(ev.Start.DateOnly ? ev.Start.Value.Date : ev.Start.Value, ev.Rule);
            var n = 0;
            foreach (var wall in walls)
            {
                if (++n > 50_000)
                {
                    break;
                }

                var start = ev.Start.Utc ? new DateTimeOffset(DateTime.SpecifyKind(wall, DateTimeKind.Unspecified), TimeSpan.Zero) : zones.At(ev.Start.DateOnly ? wall.Date : wall, tz);
                if (start >= to || (until is { } un && start > un))
                {
                    break;
                }

                if (exdates.Contains(start.UtcDateTime) || (ev.Start.DateOnly && exDays.Contains(wall.Date)) || (skip?.Contains(start.UtcDateTime) ?? false))
                {
                    continue;
                }

                var end = start + length;
                if (end > from || (length == TimeSpan.Zero && start >= from))
                {
                    list.Add(Row(ev, start, length, markers, calendar));
                }
            }
        }

        foreach (var o in overrides)
        {
            if (o.Cancelled)
            {
                continue;
            }

            var start = zones.Resolve(o.Start);
            var length = Length(o, zones);
            if (start < to && (start + length > from || (length == TimeSpan.Zero && start >= from)))
            {
                list.Add(Row(o, start, length, markers, calendar));
            }
        }

        return Cap(list);
    }

    public static List<CalEvent> Cap(List<CalEvent> list)
    {
        list.Sort((a, b) => a.Start.CompareTo(b.Start));
        if (list.Count > MaxInstances)
        {
            list.RemoveRange(MaxInstances, list.Count - MaxInstances);
        }

        return list;
    }

    private static TimeSpan Length(IcsEvent ev, IcsZones zones)
    {
        if (ev.End is { } end)
        {
            var d = zones.Resolve(end) - zones.Resolve(ev.Start);
            return d > TimeSpan.Zero ? d : TimeSpan.Zero;
        }

        if (ev.Duration is { } dur && dur > TimeSpan.Zero)
        {
            return dur;
        }

        return ev.Start.DateOnly ? TimeSpan.FromDays(1) : TimeSpan.Zero;
    }

    private static CalEvent Row(IcsEvent ev, DateTimeOffset start, TimeSpan length, CalendarMarkers markers, string? calendar)
    {
        var url = MeetingLinks.Find(ev.Location, ev.Url);
        return new CalEvent
        {
            Key = new EventKey(ev.Uid, start),
            Title = Sensitive.Of(ev.Summary),
            End = length > TimeSpan.Zero ? start + length : null,
            AllDay = ev.Start.DateOnly,
            Location = ev.Location is null ? null : Sensitive.Of(ev.Location),
            MeetingUrl = url is null ? null : Sensitive.Of(url),
            Origin = CalendarOrigin.IcsFile,
            Calendar = calendar,
            Deadline = ev.Start.DateOnly && IsDeadline(ev.Summary, markers),
            EventWindow = IsEventWindow(ev.Summary, markers),
        };
    }

    public static bool IsDeadline(string title, CalendarMarkers m) => m.DeadlineMarker.Length > 0 && title.TrimStart().StartsWith(m.DeadlineMarker, StringComparison.OrdinalIgnoreCase);

    public static bool IsEventWindow(string title, CalendarMarkers m) => m.EventMarker.Length > 0 && title.Contains(m.EventMarker, StringComparison.OrdinalIgnoreCase);

    /// <summary>Rows from the plugin's <c>tasktor.calendar/1</c> file (already expanded by the plugin).</summary>
    public static List<CalEvent> FromPlugin(CalendarFile file, DateTimeOffset from, DateTimeOffset to, CalendarMarkers markers)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(markers);
        var list = new List<CalEvent>();
        foreach (var e in file.Events)
        {
            var end = e.End ?? (e.AllDay ? e.Start.AddDays(1) : e.Start);
            if (e.Start >= to || (end <= from && !(end == e.Start && e.Start >= from)))
            {
                continue;
            }

            var url = MeetingLinks.Find(e.Location, e.Url);
            list.Add(new CalEvent
            {
                Key = new EventKey(e.Uid, e.Start),
                Title = Sensitive.Of(e.Title),
                End = e.End ?? (e.AllDay ? e.Start.AddDays(1) : null),
                AllDay = e.AllDay,
                Location = e.Location is null ? null : Sensitive.Of(e.Location),
                MeetingUrl = url is null ? null : Sensitive.Of(url),
                Origin = CalendarOrigin.Plugin,
                Calendar = e.Calendar,
                Deadline = e.AllDay && IsDeadline(e.Title, markers),
                EventWindow = IsEventWindow(e.Title, markers),
            });
        }

        return list;
    }
}

/// <summary>
/// E1's plain deadlines file: one per line, <c>2026-11-20 17:00  Project report</c> or <c>2026-11-20  Report</c> (due at
/// the end of that day). Blank lines and <c>#</c> comments are skipped; bad lines are counted.
/// </summary>
public static class DeadlinesFile
{
    public static (List<CalEvent> Rows, int Bad) Parse(string text, TimeZoneInfo local)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(local);
        var rows = new List<CalEvent>();
        var bad = 0;
        var lineNo = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !DateTime.TryParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            {
                bad++;
                continue;
            }

            DateTime wall;
            string title;
            if (TimeSpan.TryParseExact(parts[1], @"h\:mm", CultureInfo.InvariantCulture, out var time) && parts.Length == 3)
            {
                wall = day + time;
                title = parts[2].Trim();
            }
            else
            {
                wall = day.AddDays(1).AddMinutes(-1);
                title = line[parts[0].Length..].Trim();
            }

            var at = new DateTimeOffset(wall, local.GetUtcOffset(wall));
            rows.Add(new CalEvent
            {
                Key = new EventKey("deadline-" + lineNo.ToString(CultureInfo.InvariantCulture), at),
                Title = Sensitive.Of(title),
                Origin = CalendarOrigin.DeadlinesFile,
                Deadline = true,
            });
        }

        return (rows, bad);
    }
}
