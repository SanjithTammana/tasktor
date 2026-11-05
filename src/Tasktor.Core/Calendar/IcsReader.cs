using System.Globalization;
using System.Text;

namespace Tasktor.Core.Calendar;

/// <summary>A date-time as written in an ICS file: UTC (<c>Z</c>), zoned (<c>TZID</c>), floating, or a date.</summary>
public readonly record struct IcsTime(DateTime Value, bool Utc, string? TzId, bool DateOnly);

/// <summary>A <c>VEVENT</c>: only the fields U2 reads (no description, no attendees: D-S5).</summary>
public sealed record IcsEvent
{
    public required string Uid { get; init; }

    public required IcsTime Start { get; init; }

    public IcsTime? End { get; init; }

    public TimeSpan? Duration { get; init; }

    public string Summary { get; init; } = "";

    public string? Location { get; init; }

    public string? Url { get; init; }

    public RecurrenceRule? Rule { get; init; }

    public IReadOnlyList<IcsTime> ExDates { get; init; } = [];

    public IcsTime? RecurrenceId { get; init; }

    public bool Cancelled { get; init; }
}

/// <summary>One <c>STANDARD</c> / <c>DAYLIGHT</c> observance of a <c>VTIMEZONE</c>.</summary>
public sealed record IcsObservance(DateTime Start, TimeSpan OffsetFrom, TimeSpan OffsetTo, RecurrenceRule? Rule, IReadOnlyList<DateTime> RDates);

public sealed record IcsTimeZone(string Id, IReadOnlyList<IcsObservance> Observances);

public sealed record IcsCalendar(IReadOnlyList<IcsEvent> Events, IReadOnlyDictionary<string, IcsTimeZone> Zones, int Skipped);

/// <summary>
/// A small RFC 5545 reader for the subset U2 needs (OQ-S2 fallback: no third-party parser under Native AOT): line unfolding,
/// parameters, text unescaping, <c>VEVENT</c> and <c>VTIMEZONE</c>. Malformed lines are skipped and counted, never thrown.
/// </summary>
public static class IcsReader
{
    public const int MaxBytes = 8 * 1024 * 1024;
    public const int MaxEvents = 20_000;

    public static IcsCalendar Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var events = new List<IcsEvent>();
        var zones = new Dictionary<string, IcsTimeZone>(StringComparer.Ordinal);
        var skipped = 0;
        var stack = new Stack<string>();
        Dictionary<string, List<(Dictionary<string, string> Params, string Value)>>? props = null;
        var observances = new List<IcsObservance>();
        string? zoneId = null;

        foreach (var line in Unfold(text))
        {
            if (!TrySplit(line, out var name, out var ps, out var value))
            {
                skipped++;
                continue;
            }

            if (name == "BEGIN")
            {
                stack.Push(value.ToUpperInvariant());
                if (value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase) || value.Equals("STANDARD", StringComparison.OrdinalIgnoreCase) || value.Equals("DAYLIGHT", StringComparison.OrdinalIgnoreCase))
                {
                    props = new(StringComparer.Ordinal);
                }
                else if (value.Equals("VTIMEZONE", StringComparison.OrdinalIgnoreCase))
                {
                    observances = [];
                    zoneId = null;
                    props = new(StringComparer.Ordinal);
                }

                continue;
            }

            if (name == "END")
            {
                var kind = stack.Count > 0 ? stack.Pop() : "";
                switch (kind)
                {
                    case "VEVENT" when props is not null:
                        if (events.Count < MaxEvents && ToEvent(props) is { } ev)
                        {
                            events.Add(ev);
                        }
                        else
                        {
                            skipped++;
                        }

                        props = null;
                        break;
                    case "STANDARD" or "DAYLIGHT" when props is not null:
                        if (ToObservance(props) is { } ob)
                        {
                            observances.Add(ob);
                        }

                        props = stack.Count > 0 && stack.Peek() == "VTIMEZONE" ? new(StringComparer.Ordinal) : null;
                        break;
                    case "VTIMEZONE":
                        if (zoneId is not null && observances.Count > 0)
                        {
                            zones[zoneId] = new IcsTimeZone(zoneId, observances);
                        }

                        props = null;
                        break;
                }

                continue;
            }

            if (stack.Count > 0 && stack.Peek() == "VTIMEZONE" && name == "TZID")
            {
                zoneId = value;
                continue;
            }

            if (props is not null)
            {
                if (!props.TryGetValue(name, out var list))
                {
                    props[name] = list = [];
                }

                list.Add((ps, value));
            }
        }

        return new IcsCalendar(events, zones, skipped);
    }

    private static IcsEvent? ToEvent(Dictionary<string, List<(Dictionary<string, string> Params, string Value)>> p)
    {
        if (!p.TryGetValue("DTSTART", out var ds) || TryTime(ds[0]) is not { } start)
        {
            return null;
        }

        var uid = First(p, "UID") ?? ("noid-" + start.Value.Ticks.ToString(CultureInfo.InvariantCulture));
        RecurrenceRule? rule = null;
        if (First(p, "RRULE") is { } rr && !RecurrenceRule.TryParse(rr, out rule))
        {
            rule = null;
        }

        var ex = new List<IcsTime>();
        foreach (var (ps, v) in p.GetValueOrDefault("EXDATE") ?? [])
        {
            foreach (var part in v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (TryTime((ps, part)) is { } t)
                {
                    ex.Add(t);
                }
            }
        }

        return new IcsEvent
        {
            Uid = uid,
            Start = start,
            End = p.TryGetValue("DTEND", out var de) ? TryTime(de[0]) : null,
            Duration = First(p, "DURATION") is { } d ? ParseDuration(d) : null,
            Summary = Unescape(First(p, "SUMMARY") ?? ""),
            Location = First(p, "LOCATION") is { } l ? Unescape(l) : null,
            Url = First(p, "URL"),
            Rule = rule,
            ExDates = ex,
            RecurrenceId = p.TryGetValue("RECURRENCE-ID", out var rid) ? TryTime(rid[0]) : null,
            Cancelled = string.Equals(First(p, "STATUS"), "CANCELLED", StringComparison.OrdinalIgnoreCase),
        };
    }

    private static IcsObservance? ToObservance(Dictionary<string, List<(Dictionary<string, string> Params, string Value)>> p)
    {
        if (First(p, "DTSTART") is not { } ds || !TryParseDateTime(ds, out var start, out _, out _)
            || ParseOffset(First(p, "TZOFFSETFROM")) is not { } from || ParseOffset(First(p, "TZOFFSETTO")) is not { } to)
        {
            return null;
        }

        RecurrenceRule? rule = null;
        if (First(p, "RRULE") is { } rr && !RecurrenceRule.TryParse(rr, out rule))
        {
            rule = null;
        }

        var rdates = new List<DateTime>();
        foreach (var (_, v) in p.GetValueOrDefault("RDATE") ?? [])
        {
            foreach (var part in v.Split(','))
            {
                if (TryParseDateTime(part.Trim(), out var r, out _, out _))
                {
                    rdates.Add(r);
                }
            }
        }

        return new IcsObservance(start, from, to, rule, rdates);
    }

    private static string? First(Dictionary<string, List<(Dictionary<string, string> Params, string Value)>> p, string name)
        => p.TryGetValue(name, out var l) && l.Count > 0 ? l[0].Value : null;

    private static IcsTime? TryTime((Dictionary<string, string> Params, string Value) prop)
    {
        var dateOnly = prop.Params.TryGetValue("VALUE", out var vt) && vt.Equals("DATE", StringComparison.OrdinalIgnoreCase);
        if (!TryParseDateTime(prop.Value, out var dt, out var utc, out var isDate))
        {
            return null;
        }

        prop.Params.TryGetValue("TZID", out var tz);
        return new IcsTime(dt, utc, utc ? null : tz?.Trim('"'), dateOnly || isDate);
    }

    /// <summary><c>20261120</c>, <c>20261120T170000</c> or <c>20261120T170000Z</c>.</summary>
    public static bool TryParseDateTime(string s, out DateTime value, out bool utc, out bool dateOnly)
    {
        value = default;
        utc = s.EndsWith('Z');
        dateOnly = s.Length == 8;
        var core = utc ? s[..^1] : s;
        return core.Length == 8
            ? DateTime.TryParseExact(core, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out value)
            : DateTime.TryParseExact(core, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    /// <summary>RFC 5545 durations: <c>P1W</c>, <c>P1DT2H</c>, <c>PT15M</c>, <c>-PT5M</c>.</summary>
    public static TimeSpan? ParseDuration(string s)
    {
        var neg = s.StartsWith('-');
        var i = s.IndexOf('P', StringComparison.Ordinal);
        if (i < 0)
        {
            return null;
        }

        var total = TimeSpan.Zero;
        var num = 0;
        var any = false;
        foreach (var ch in s[(i + 1)..])
        {
            if (char.IsAsciiDigit(ch))
            {
                num = checked((num * 10) + (ch - '0'));
                any = true;
                continue;
            }

            total += ch switch
            {
                'W' => TimeSpan.FromDays(7 * num),
                'D' => TimeSpan.FromDays(num),
                'H' => TimeSpan.FromHours(num),
                'M' => TimeSpan.FromMinutes(num),
                'S' => TimeSpan.FromSeconds(num),
                _ => TimeSpan.Zero,
            };
            num = 0;
        }

        return any ? (neg ? -total : total) : null;
    }

    private static TimeSpan? ParseOffset(string? s)
    {
        if (s is null || s.Length < 5 || (s[0] != '+' && s[0] != '-')
            || !int.TryParse(s.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var h)
            || !int.TryParse(s.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var m))
        {
            return null;
        }

        var t = new TimeSpan(h, m, 0);
        return s[0] == '-' ? -t : t;
    }

    public static string Unescape(string s)
    {
        if (!s.Contains('\\', StringComparison.Ordinal))
        {
            return s;
        }

        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                i++;
                sb.Append(s[i] is 'n' or 'N' ? '\n' : s[i]);
            }
            else
            {
                sb.Append(s[i]);
            }
        }

        return sb.ToString();
    }

    private static IEnumerable<string> Unfold(string text)
    {
        var sb = new StringBuilder();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                sb.Append(line, 1, line.Length - 1);
                continue;
            }

            if (sb.Length > 0)
            {
                yield return sb.ToString();
            }

            sb.Clear().Append(line);
        }

        if (sb.Length > 0)
        {
            yield return sb.ToString();
        }
    }

    /// <summary><c>NAME;P1=v;P2="a:b":value</c> → name, params, value (colons inside quoted params are not separators).</summary>
    private static bool TrySplit(string line, out string name, out Dictionary<string, string> ps, out string value)
    {
        ps = new(StringComparer.OrdinalIgnoreCase);
        name = "";
        value = "";
        var quoted = false;
        var colon = -1;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                quoted = !quoted;
            }
            else if (line[i] == ':' && !quoted)
            {
                colon = i;
                break;
            }
        }

        if (colon <= 0)
        {
            return false;
        }

        var head = line[..colon].Split(';');
        name = head[0].Trim().ToUpperInvariant();
        foreach (var p in head.Skip(1))
        {
            var eq = p.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                ps[p[..eq].Trim()] = p[(eq + 1)..].Trim().Trim('"');
            }
        }

        value = line[(colon + 1)..];
        return name.Length > 0;
    }
}
