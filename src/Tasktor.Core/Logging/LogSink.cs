using System.Globalization;
using System.Text;
using Tasktor.Contracts.Platform;

namespace Tasktor.Core.Logging;

public enum LogLevel
{
    Verbose = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}

/// <summary>
/// One structured field. Only primitives and <see cref="ISensitive"/> values are accepted; sensitive values always
/// print <c>&lt;redacted&gt;</c> (G-redact). Plain strings are for exe names, ids, actions and results, never titles,
/// command lines, profile paths, URLs or typed text.
/// </summary>
public readonly struct LogField
{
    private LogField(string name, string text)
    {
        Name = name;
        Text = text;
    }

    public string Name { get; }

    public string Text { get; }

    public static LogField Of(string name, string? value) => new(name, Quote(value ?? "-"));

    public static LogField Of(string name, long value) => new(name, value.ToString(CultureInfo.InvariantCulture));

    public static LogField Of(string name, double value) => new(name, value.ToString("0.###", CultureInfo.InvariantCulture));

    public static LogField Of(string name, bool value) => new(name, value ? "true" : "false");

    public static LogField Of<T>(string name, Sensitive<T> value) => new(name, value.ToString());

    public static LogField Redacted(string name) => new(name, Sensitive<string>.Redacted);

    private static string Quote(string s)
    {
        var needs = false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c) || c == '"' || c == '=' || char.IsControl(c))
            {
                needs = true;
                break;
            }
        }

        if (!needs)
        {
            return s;
        }

        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (var c in s)
        {
            if (c == '"' || c == '\\')
            {
                sb.Append('\\').Append(c);
            }
            else if (char.IsControl(c))
            {
                sb.Append(' ');
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.Append('"').ToString();
    }
}

/// <summary>Where log lines go. The file writer appends off the logic thread.</summary>
public interface ILogWriter
{
    void Write(DateTimeOffset at, string line);
}

/// <summary>
/// The agent's local log (U1 H6): <c>time LEVEL event k=v …</c>, <c>info</c> by default; verbose mode reverts by itself
/// after 24 h. No personal data at any level (<see cref="LogField"/>).
/// </summary>
public sealed class LogSink
{
    public static readonly TimeSpan VerboseLifetime = TimeSpan.FromHours(24);

    private readonly IClock _clock;
    private readonly ILogWriter _writer;
    private DateTimeOffset? _verboseUntil;

    public LogSink(IClock clock, ILogWriter writer)
    {
        _clock = clock;
        _writer = writer;
    }

    public LogLevel MinLevel => _verboseUntil is { } until && _clock.UtcNow < until ? LogLevel.Verbose : LogLevel.Info;

    public bool VerboseOn => MinLevel == LogLevel.Verbose;

    public DateTimeOffset? VerboseUntil => VerboseOn ? _verboseUntil : null;

    /// <summary><c>tasktor log verbose on|off</c>: on lasts 24 h, then reverts to info by itself.</summary>
    public void SetVerbose(bool on) => _verboseUntil = on ? _clock.UtcNow + VerboseLifetime : null;

    public void Log(LogLevel level, string evt, params ReadOnlySpan<LogField> fields)
    {
        if (level < MinLevel)
        {
            return;
        }

        var now = _clock.UtcNow;
        var sb = new StringBuilder(64);
        sb.Append(now.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(level switch { LogLevel.Verbose => "VERB", LogLevel.Info => "INFO", LogLevel.Warn => "WARN", _ => "ERROR" })
            .Append(' ')
            .Append(evt);
        foreach (var f in fields)
        {
            sb.Append(' ').Append(f.Name).Append('=').Append(f.Text);
        }

        _writer.Write(now, sb.ToString());
    }

    public void Info(string evt, params ReadOnlySpan<LogField> fields) => Log(LogLevel.Info, evt, fields);

    public void Warn(string evt, params ReadOnlySpan<LogField> fields) => Log(LogLevel.Warn, evt, fields);

    public void Error(string evt, params ReadOnlySpan<LogField> fields) => Log(LogLevel.Error, evt, fields);

    public void Verbose(string evt, params ReadOnlySpan<LogField> fields) => Log(LogLevel.Verbose, evt, fields);
}

/// <summary>Collects lines in memory (tests, the self-test, log bundle previews).</summary>
public sealed class MemoryLogWriter : ILogWriter
{
    private readonly List<string> _lines = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_gate)
            {
                return [.. _lines];
            }
        }
    }

    public void Write(DateTimeOffset at, string line)
    {
        lock (_gate)
        {
            _lines.Add(line);
        }
    }
}

/// <summary>
/// Daily files <c>log\tasktor-yyyyMMdd.log</c>; keeps 7 days and at most 20 MB in total (oldest deleted first).
/// Thread-safe; callers on the loop pay one append.
/// </summary>
public sealed class FileLogWriter : ILogWriter
{
    public const int KeepDays = 7;
    public const long MaxTotalBytes = 20L * 1024 * 1024;

    private readonly string _dir;
    private readonly Lock _gate = new();
    private DateOnly _day;
    private long _written;

    public FileLogWriter(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
    }

    public void Write(DateTimeOffset at, string line)
    {
        lock (_gate)
        {
            var day = DateOnly.FromDateTime(at.UtcDateTime);
            if (day != _day)
            {
                _day = day;
                Prune(day);
            }

            var path = Path.Combine(_dir, "tasktor-" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
            File.AppendAllText(path, line + "\n");
            _written += line.Length + 1;
            if (_written > MaxTotalBytes / 8)
            {
                _written = 0;
                Prune(day);
            }
        }
    }

    private void Prune(DateOnly today)
    {
        var files = new DirectoryInfo(_dir).GetFiles("tasktor-*.log").OrderByDescending(f => f.Name, StringComparer.Ordinal).ToList();
        long total = 0;
        var cutoff = today.AddDays(-(KeepDays - 1)).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        foreach (var f in files)
        {
            var stamp = f.Name.Length >= 16 ? f.Name.Substring(8, 8) : string.Empty;
            total += f.Length;
            if (string.CompareOrdinal(stamp, cutoff) < 0 || total > MaxTotalBytes)
            {
                try
                {
                    f.Delete();
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
