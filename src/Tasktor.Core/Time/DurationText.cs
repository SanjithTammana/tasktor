using System.Globalization;

namespace Tasktor.Core.Time;

/// <summary>Parses and formats the duration strings used in rules and manifests: <c>500ms</c>, <c>30s</c>, <c>5m</c>, <c>2h</c>, <c>1d</c>, <c>1h30m</c>.</summary>
public static class DurationText
{
    public static bool TryParse(string? text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim().AsSpan();
        var total = TimeSpan.Zero;
        var any = false;
        while (!s.IsEmpty)
        {
            var i = 0;
            while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] == '.'))
            {
                i++;
            }

            if (i == 0 || !double.TryParse(s[..i], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n))
            {
                return false;
            }

            s = s[i..];
            var j = 0;
            while (j < s.Length && char.IsAsciiLetter(s[j]))
            {
                j++;
            }

            var unit = s[..j];
            s = s[j..];
            TimeSpan part;
            if (unit.SequenceEqual("ms"))
            {
                part = TimeSpan.FromMilliseconds(n);
            }
            else if (unit.SequenceEqual("s"))
            {
                part = TimeSpan.FromSeconds(n);
            }
            else if (unit.SequenceEqual("m") || unit.SequenceEqual("min"))
            {
                part = TimeSpan.FromMinutes(n);
            }
            else if (unit.SequenceEqual("h"))
            {
                part = TimeSpan.FromHours(n);
            }
            else if (unit.SequenceEqual("d"))
            {
                part = TimeSpan.FromDays(n);
            }
            else
            {
                return false;
            }

            total += part;
            any = true;
        }

        value = total;
        return any;
    }

    public static TimeSpan Parse(string text) =>
        TryParse(text, out var v) ? v : throw new FormatException("Not a duration: " + text);

    /// <summary>Short human text: "45 s", "12 min", "1 h 10 m", "2 d".</summary>
    public static string Humanize(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        if (span.TotalSeconds < 60)
        {
            return ((int)span.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " s";
        }

        if (span.TotalMinutes < 60)
        {
            return ((int)span.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min";
        }

        if (span.TotalHours < 48)
        {
            var h = (int)span.TotalHours;
            var m = span.Minutes;
            return m == 0
                ? h.ToString(CultureInfo.InvariantCulture) + " h"
                : h.ToString(CultureInfo.InvariantCulture) + " h " + m.ToString(CultureInfo.InvariantCulture) + " m";
        }

        return ((int)span.TotalDays).ToString(CultureInfo.InvariantCulture) + " d";
    }
}
