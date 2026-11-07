using System.Globalization;

namespace Tasktor.Core.Calendar;

public enum RecurrenceFrequency : byte
{
    Daily,
    Weekly,
    Monthly,
    Yearly,
}

/// <summary><c>BYDAY</c> entry: <c>MO</c> (every Monday) or <c>-1SU</c> (the last Sunday of the period).</summary>
public readonly record struct ByDay(int Ordinal, DayOfWeek Day);

/// <summary>
/// An <c>RRULE</c> subset (U2 CalendarSource): FREQ DAILY / WEEKLY / MONTHLY / YEARLY, INTERVAL, COUNT, UNTIL, BYDAY, plus
/// BYMONTH and BYMONTHDAY, which VTIMEZONE rules and common exports use. Weeks start on Monday.
/// </summary>
public sealed record RecurrenceRule
{
    public required RecurrenceFrequency Frequency { get; init; }

    public int Interval { get; init; } = 1;

    public int? Count { get; init; }

    public IcsTime? Until { get; init; }

    public IReadOnlyList<ByDay> ByDay { get; init; } = [];

    public IReadOnlyList<int> ByMonth { get; init; } = [];

    public IReadOnlyList<int> ByMonthDay { get; init; } = [];

    public static bool TryParse(string text, out RecurrenceRule? rule)
    {
        rule = null;
        RecurrenceFrequency? freq = null;
        int interval = 1;
        int? count = null;
        IcsTime? until = null;
        var byDay = new List<ByDay>();
        var byMonth = new List<int>();
        var byMonthDay = new List<int>();
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var key = part[..eq].Trim().ToUpperInvariant();
            var val = part[(eq + 1)..].Trim();
            switch (key)
            {
                case "FREQ":
                    freq = val.ToUpperInvariant() switch
                    {
                        "DAILY" => RecurrenceFrequency.Daily,
                        "WEEKLY" => RecurrenceFrequency.Weekly,
                        "MONTHLY" => RecurrenceFrequency.Monthly,
                        "YEARLY" => RecurrenceFrequency.Yearly,
                        _ => null,
                    };
                    if (freq is null)
                    {
                        return false; // HOURLY and finer are not supported: skip the rule rather than guess
                    }

                    break;
                case "INTERVAL" when int.TryParse(val, NumberStyles.None, CultureInfo.InvariantCulture, out var iv) && iv is > 0 and < 1000:
                    interval = iv;
                    break;
                case "COUNT" when int.TryParse(val, NumberStyles.None, CultureInfo.InvariantCulture, out var c) && c > 0:
                    count = c;
                    break;
                case "UNTIL" when IcsReader.TryParseDateTime(val, out var u, out var utc, out var date):
                    until = new IcsTime(date ? u.AddDays(1).AddTicks(-1) : u, utc, null, date);
                    break;
                case "BYDAY":
                    foreach (var d in val.Split(','))
                    {
                        if (ParseByDay(d.Trim()) is { } bd)
                        {
                            byDay.Add(bd);
                        }
                    }

                    break;
                case "BYMONTH":
                    byMonth.AddRange(Ints(val).Where(m => m is >= 1 and <= 12));
                    break;
                case "BYMONTHDAY":
                    byMonthDay.AddRange(Ints(val).Where(d => d is >= -31 and <= 31 and not 0));
                    break;
            }
        }

        if (freq is null)
        {
            return false;
        }

        rule = new RecurrenceRule { Frequency = freq.Value, Interval = interval, Count = count, Until = until, ByDay = byDay, ByMonth = byMonth, ByMonthDay = byMonthDay };
        return true;
    }

    private static IEnumerable<int> Ints(string val)
    {
        foreach (var p in val.Split(','))
        {
            if (int.TryParse(p.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
            {
                yield return n;
            }
        }
    }

    private static ByDay? ParseByDay(string s)
    {
        if (s.Length < 2)
        {
            return null;
        }

        DayOfWeek? day = s[^2..].ToUpperInvariant() switch
        {
            "MO" => DayOfWeek.Monday,
            "TU" => DayOfWeek.Tuesday,
            "WE" => DayOfWeek.Wednesday,
            "TH" => DayOfWeek.Thursday,
            "FR" => DayOfWeek.Friday,
            "SA" => DayOfWeek.Saturday,
            "SU" => DayOfWeek.Sunday,
            _ => null,
        };
        if (day is null)
        {
            return null;
        }

        var ord = 0;
        if (s.Length > 2 && !int.TryParse(s[..^2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out ord))
        {
            return null;
        }

        return new ByDay(ord, day.Value);
    }
}

/// <summary>
/// Expands a rule in the event's own wall-clock time (so a 09:00 meeting stays at 09:00 across a DST change). Yields
/// DTSTART first (RFC 5545: it is always the first instance), then the rule's instances in order; COUNT includes DTSTART.
/// The caller stops reading at its window end; a guard bounds pathological rules.
/// </summary>
public static class RecurrenceExpander
{
    public const int MaxPeriods = 100_000;

    public static IEnumerable<DateTime> Expand(DateTime start, RecurrenceRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        yield return start;
        var emitted = 1;
        if (rule.Count is { } c0 && emitted >= c0)
        {
            yield break;
        }

        for (var p = 0; p < MaxPeriods; p++)
        {
            DateTime period;
            try
            {
                period = PeriodStart(start, rule, p);
            }
            catch (ArgumentOutOfRangeException)
            {
                yield break;
            }

            if (period.Year > 9000)
            {
                yield break;
            }

            foreach (var day in Candidates(period, start, rule).Distinct().Order())
            {
                var t = day + start.TimeOfDay;
                if (t <= start)
                {
                    continue;
                }

                yield return t;
                emitted++;
                if (rule.Count is { } c && emitted >= c)
                {
                    yield break;
                }
            }
        }
    }

    private static DateTime PeriodStart(DateTime start, RecurrenceRule r, int p) => r.Frequency switch
    {
        RecurrenceFrequency.Daily => start.Date.AddDays((long)p * r.Interval),
        RecurrenceFrequency.Weekly => WeekStart(start.Date).AddDays(7L * p * r.Interval),
        RecurrenceFrequency.Monthly => new DateTime(start.Year, start.Month, 1).AddMonths(p * r.Interval),
        _ => new DateTime(start.Year, 1, 1).AddYears(p * r.Interval),
    };

    private static DateTime WeekStart(DateTime d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    private static IEnumerable<DateTime> Candidates(DateTime period, DateTime start, RecurrenceRule r)
    {
        switch (r.Frequency)
        {
            case RecurrenceFrequency.Daily:
                if (MonthOk(period, r) && (r.ByDay.Count == 0 || r.ByDay.Any(b => b.Day == period.DayOfWeek))
                    && (r.ByMonthDay.Count == 0 || MonthDays(period, r.ByMonthDay).Contains(period)))
                {
                    yield return period;
                }

                break;
            case RecurrenceFrequency.Weekly:
                var days = r.ByDay.Count == 0 ? [start.DayOfWeek] : r.ByDay.Select(b => b.Day);
                foreach (var d in days)
                {
                    var day = period.AddDays(((int)d + 6) % 7);
                    if (MonthOk(day, r))
                    {
                        yield return day;
                    }
                }

                break;
            case RecurrenceFrequency.Monthly:
                if (MonthOk(period, r))
                {
                    foreach (var d in InMonth(period, start, r))
                    {
                        yield return d;
                    }
                }

                break;
            default:
                if (r.ByMonth.Count == 0 && r.ByDay.Count > 0 && r.ByMonthDay.Count == 0)
                {
                    foreach (var d in InSpan(period, period.AddYears(1), r.ByDay))
                    {
                        yield return d;
                    }

                    break;
                }

                foreach (var m in r.ByMonth.Count > 0 ? r.ByMonth : [start.Month])
                {
                    foreach (var d in InMonth(new DateTime(period.Year, m, 1), start, r))
                    {
                        yield return d;
                    }
                }

                break;
        }
    }

    private static bool MonthOk(DateTime d, RecurrenceRule r) => r.ByMonth.Count == 0 || r.ByMonth.Contains(d.Month);

    private static IEnumerable<DateTime> InMonth(DateTime first, DateTime start, RecurrenceRule r)
    {
        if (r.ByDay.Count > 0)
        {
            var byDay = InSpan(first, first.AddMonths(1), r.ByDay);
            return r.ByMonthDay.Count > 0 ? byDay.Intersect(MonthDays(first, r.ByMonthDay)) : byDay;
        }

        if (r.ByMonthDay.Count > 0)
        {
            return MonthDays(first, r.ByMonthDay);
        }

        return start.Day <= DateTime.DaysInMonth(first.Year, first.Month) ? [first.AddDays(start.Day - 1)] : [];
    }

    private static List<DateTime> MonthDays(DateTime anyDay, IReadOnlyList<int> monthDays)
    {
        var first = new DateTime(anyDay.Year, anyDay.Month, 1);
        var n = DateTime.DaysInMonth(first.Year, first.Month);
        var list = new List<DateTime>();
        foreach (var md in monthDays)
        {
            var day = md > 0 ? md : n + md + 1;
            if (day >= 1 && day <= n)
            {
                list.Add(first.AddDays(day - 1));
            }
        }

        return list;
    }

    /// <summary>Days in [from, to) matching BYDAY; an ordinal picks the n-th (or n-th from the end) such weekday in the span.</summary>
    private static IEnumerable<DateTime> InSpan(DateTime from, DateTime to, IReadOnlyList<ByDay> byDay)
    {
        foreach (var bd in byDay)
        {
            var all = new List<DateTime>();
            var d = from.AddDays((((int)bd.Day - (int)from.DayOfWeek) + 7) % 7);
            for (; d < to; d = d.AddDays(7))
            {
                all.Add(d);
            }

            if (bd.Ordinal == 0)
            {
                foreach (var x in all)
                {
                    yield return x;
                }
            }
            else
            {
                var i = bd.Ordinal > 0 ? bd.Ordinal - 1 : all.Count + bd.Ordinal;
                if (i >= 0 && i < all.Count)
                {
                    yield return all[i];
                }
            }
        }
    }
}
