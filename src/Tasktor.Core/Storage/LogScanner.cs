using System.Globalization;

namespace Tasktor.Core.Storage;

/// <summary>
/// Reads Tasktor's own log lines back (<c>time LEVEL event k=v …</c>) for the <c>actions_log</c> / <c>alerts_log</c> export
/// tables (U2 D-S7: "from H6; names only"). The log holds no personal data, so neither do these tables.
/// </summary>
public static class LogScanner
{
    public static IEnumerable<(DateTimeOffset At, string Event, Dictionary<string, string> Fields)> Read(string logDir, DateTimeOffset? since)
    {
        if (!Directory.Exists(logDir))
        {
            yield break;
        }

        foreach (var file in Directory.GetFiles(logDir, "tasktor-*.log").Order(StringComparer.Ordinal))
        {
            foreach (var line in File.ReadLines(file))
            {
                if (Parse(line) is { } entry && (since is null || entry.At >= since))
                {
                    yield return entry;
                }
            }
        }
    }

    public static (DateTimeOffset At, string Event, Dictionary<string, string> Fields)? Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var parts = Split(line);
        if (parts.Count < 3 || !DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
        {
            return null;
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in parts.Skip(3))
        {
            var eq = p.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                fields[p[..eq]] = p[(eq + 1)..];
            }
        }

        return (at, parts[2], fields);
    }

    private static List<string> Split(string line)
    {
        var parts = new List<string>();
        var sb = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '\\' && i + 1 < line.Length)
                {
                    sb.Append(line[++i]);
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    sb.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ' ')
            {
                if (sb.Length > 0)
                {
                    parts.Add(sb.ToString());
                    sb.Clear();
                }
            }
            else
            {
                sb.Append(c);
            }
        }

        if (sb.Length > 0)
        {
            parts.Add(sb.ToString());
        }

        return parts;
    }
}
