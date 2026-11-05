namespace Tasktor.Core.Calendar;

/// <summary>
/// Turns ICS times into instants. A <c>TZID</c> resolves to the system zone with that id when there is one, else to the
/// file's own <c>VTIMEZONE</c> (the agent runs with invariant globalization, so IANA ids cannot be mapped to Windows ids on
/// Windows), else to the local zone. Floating times and dates are local.
/// </summary>
public sealed class IcsZones
{
    private readonly IReadOnlyDictionary<string, IcsTimeZone> _file;
    private readonly TimeZoneInfo _local;
    private readonly Dictionary<string, TimeZoneInfo?> _system = new(StringComparer.Ordinal);

    public IcsZones(IReadOnlyDictionary<string, IcsTimeZone> fileZones, TimeZoneInfo local)
    {
        _file = fileZones;
        _local = local;
    }

    public TimeZoneInfo Local => _local;

    public DateTimeOffset Resolve(IcsTime t)
    {
        if (t.Utc)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(t.Value, DateTimeKind.Unspecified), TimeSpan.Zero);
        }

        return At(t.DateOnly ? t.Value.Date : t.Value, t.DateOnly ? null : t.TzId);
    }

    /// <summary>The instant of wall-clock time <paramref name="wall"/> in zone <paramref name="tzid"/> (null = local).</summary>
    public DateTimeOffset At(DateTime wall, string? tzid)
    {
        var w = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        if (tzid is null)
        {
            return new DateTimeOffset(w, Offset(_local, w));
        }

        if (System(tzid) is { } tz)
        {
            return new DateTimeOffset(w, Offset(tz, w));
        }

        if (_file.TryGetValue(tzid, out var fz))
        {
            return new DateTimeOffset(w, FileOffset(fz, w));
        }

        return new DateTimeOffset(w, Offset(_local, w));
    }

    private static TimeSpan Offset(TimeZoneInfo tz, DateTime wall)
    {
        // A wall time inside a spring-forward gap does not exist; move it forward by the gap, as calendars do.
        var w = wall;
        for (var i = 0; i < 4 && tz.IsInvalidTime(w); i++)
        {
            w = w.AddMinutes(30);
        }

        return tz.GetUtcOffset(w);
    }

    private TimeZoneInfo? System(string id)
    {
        if (_system.TryGetValue(id, out var tz))
        {
            return tz;
        }

        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            tz = null;
        }

        _system[id] = tz;
        return tz;
    }

    /// <summary>The offset of the observance with the latest onset at or before <paramref name="wall"/>.</summary>
    public static TimeSpan FileOffset(IcsTimeZone zone, DateTime wall)
    {
        ArgumentNullException.ThrowIfNull(zone);
        DateTime? best = null;
        var offset = zone.Observances.MinBy(o => o.Start)!.OffsetFrom;
        foreach (var ob in zone.Observances)
        {
            var onsets = ob.Rule is null ? [ob.Start] : RecurrenceExpander.Expand(ob.Start, ob.Rule).TakeWhile(t => t <= wall);
            foreach (var onset in onsets.Concat(ob.RDates))
            {
                if (onset <= wall && (best is null || onset > best))
                {
                    best = onset;
                    offset = ob.OffsetTo;
                }
            }
        }

        return offset;
    }
}
