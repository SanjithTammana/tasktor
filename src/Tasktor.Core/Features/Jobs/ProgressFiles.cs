using System.Text.Json;
using Tasktor.Contracts;
using Tasktor.Contracts.Jobs;
using Tasktor.Contracts.Platform;
using Tasktor.Core.Storage;

namespace Tasktor.Core.Features.Jobs;

/// <summary>
/// B4 progress files are untrusted input (any local process can write one): size cap before parse, source-generated JSON
/// with a depth limit, strings length-capped, text only (no markup), <c>log</c> must be a local path (URLs rejected), never
/// executed. An invalid file becomes one row that says why, never a toast.
/// </summary>
public static class ProgressParser
{
    public const int MaxBytes = 4096;

    public static ProgressRow Parse(string stem, ReadOnlySpan<byte> bytes, DateTimeOffset now)
    {
        if (bytes.Length > MaxBytes)
        {
            return Invalid(stem, "file is over 4 KB", now);
        }

        ProgressFile? f;
        try
        {
            f = JsonSerializer.Deserialize(bytes, ContractsJsonContext.Default.ProgressFile);
        }
        catch (JsonException ex)
        {
            return Invalid(stem, "not valid: " + ex.Message.Split('\n')[0], now);
        }

        if (f is null)
        {
            return Invalid(stem, "empty file", now);
        }

        if (f.Schema != 1)
        {
            return Invalid(stem, "schema must be 1", now);
        }

        if (f.Id != stem || !StoreLayout.IsValidId(f.Id))
        {
            return Invalid(stem, "id must equal the file name and be [A-Za-z0-9._-]{1,64}", now);
        }

        string? log = null;
        if (f.Log is { } l)
        {
            if (l.Contains("://", StringComparison.Ordinal) || l.StartsWith("\\\\", StringComparison.Ordinal))
            {
                return Invalid(stem, "log must be a local file path", now);
            }

            log = Cap(l, 260);
        }

        double? pct = f.Pct is { } p ? Math.Clamp(p, 0, 100) : null;
        if (f.Done is { } d && f.Total is { } t && t > 0)
        {
            pct = Math.Clamp(100.0 * d / t, 0, 100); // done / total wins over pct
        }

        var updated = f.Updated > now ? now : f.Updated; // a clock-skewed future time counts as now
        return new ProgressRow
        {
            Id = f.Id,
            Pid = f.Pid,
            Label = Cap(f.Label, 120) ?? f.Id,
            Pct = f.State == ProgressState.Indeterminate ? null : pct,
            Done = f.Done,
            Total = f.Total,
            Unit = Cap(f.Unit, 32),
            Stage = Cap(f.Stage, 64),
            Msg = Cap(f.Msg, 160),
            EtaS = f.EtaS is { } e && e >= 0 ? e : null,
            LogPath = log,
            State = f.State ?? ProgressState.Running,
            HeartbeatS = f.HeartbeatS is { } h ? Math.Clamp(h, 5, 3600) : null,
            Paused = f.Paused == true,
            UpdatedUtc = updated,
            Source = ProgressSource.File,
        };
    }

    private static ProgressRow Invalid(string stem, string why, DateTimeOffset now) =>
        new() { Id = stem, Label = "Invalid progress file " + stem, Invalid = why, UpdatedUtc = now, State = ProgressState.Warning, Source = ProgressSource.File };

    private static string? Cap(string? s, int max)
    {
        if (s is null)
        {
            return null;
        }

        var clean = new string(s.Where(c => !char.IsControl(c)).ToArray());
        return clean.Length <= max ? clean : clean[..max];
    }
}

/// <summary>
/// <c>ITableSource&lt;JobId, ProgressRow&gt;</c> over <c>jobs\</c> (B4.4): a push watch (FileSystemWatcher =
/// <c>ReadDirectoryChangesW</c> on Windows), each changed file re-read at most once a second (debounce), files over 4 KB
/// rejected unread, overflow → one re-enumeration that loses no file, the folder recreated if deleted. Only enqueues.
/// </summary>
public sealed class JobProgressSource : IDisposable
{
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1);

    private readonly string _dir;
    private readonly Func<DateTimeOffset> _now;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _lastRead = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private Action<IReadOnlyList<ProgressRow>, IReadOnlyList<string>>? _post;
    private Timer? _timer;
    private volatile bool _disposed;
    private volatile bool _broken;

    public JobProgressSource(string dir, Func<DateTimeOffset> now)
    {
        _dir = dir;
        _now = now;
    }

    public long Reads { get; private set; }

    public long Overflows { get; private set; }

    /// <summary>Start watching. <paramref name="post"/> gets changed rows and removed ids (thread-safe; it should enqueue).</summary>
    public void Start(Action<IReadOnlyList<ProgressRow>, IReadOnlyList<string>> post)
    {
        _post = post;
        _timer = new Timer(_ => Tick(), null, Debounce, Debounce);
        Rearm();
    }

    public void Dispose()
    {
        _disposed = true; // callbacks already queued see this and do nothing
        lock (_gate)
        {
            _watcher?.Dispose();
            _watcher = null;
        }

        if (_timer is { } timer)
        {
            using var done = new ManualResetEvent(false);
            if (timer.Dispose(done))
            {
                done.WaitOne(TimeSpan.FromSeconds(5)); // a running tick finishes before the folder may go
            }
        }
    }

    /// <summary>
    /// The timer: retry a folder that could not be watched (deleted, or blocked for now), then read what is due. Never
    /// throws: it runs on a pool thread, where an exception would end the process.
    /// </summary>
    public void Tick()
    {
        if (_disposed)
        {
            return;
        }

        if (_broken)
        {
            Rearm();
        }

        Flush();
    }

    /// <summary>
    /// Re-read every file (start, after a watch overflow, after the folder came back). False when the folder cannot be
    /// listed now (deleted under the watch, or not creatable): the timer retries; nothing is thrown.
    /// </summary>
    public bool Rescan(bool overflow)
    {
        if (_disposed)
        {
            return false;
        }

        if (overflow)
        {
            Overflows++;
        }

        try
        {
            Directory.CreateDirectory(_dir); // the folder is recreated if deleted
            var files = Directory.GetFiles(_dir, "*.json");
            lock (_gate)
            {
                foreach (var f in files)
                {
                    _pending.Add(Path.GetFileName(f));
                }

                _lastRead.Clear();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _broken = true; // e.g. deleted between create and list; the next tick re-arms the watch and rescans
            return false;
        }

        Flush();
        return true;
    }

    /// <summary>The watch's own error (overflow, or its folder gone): rescan; re-arm on the next tick if that failed.</summary>
    private void OnError()
    {
        if (!_disposed && !Rescan(overflow: true))
        {
            _broken = true;
        }
    }

    /// <summary>(Re)create the folder and its watch, then rescan. A watch whose folder was deleted reports nothing more.</summary>
    private void Rearm()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_dir);
            var watcher = new FileSystemWatcher(_dir, "*.json") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size, InternalBufferSize = 8192 };
            watcher.Changed += (_, e) => Touch(e.Name);
            watcher.Created += (_, e) => Touch(e.Name);
            watcher.Renamed += (_, e) => Touch(e.Name);
            watcher.Deleted += (_, e) => Touch(e.Name);
            watcher.Error += (_, _) => OnError();
            watcher.EnableRaisingEvents = true;
            FileSystemWatcher? old;
            lock (_gate)
            {
                if (_disposed)
                {
                    watcher.Dispose();
                    return;
                }

                old = _watcher;
                _watcher = watcher;
            }

            old?.Dispose();
            _broken = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _broken = true; // FileSystemWatcher throws ArgumentException for a missing folder
            return;
        }

        Rescan(overflow: false);
    }

    private void Touch(string? name)
    {
        if (name is null || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".control.json", StringComparison.OrdinalIgnoreCase))
        {
            return; // half-written files are never read; the control file is Tasktor's own
        }

        lock (_gate)
        {
            _pending.Add(name);
        }
    }

    /// <summary>Read pending files whose debounce has passed (called by the timer; tests call it directly).</summary>
    public void Flush()
    {
        if (_disposed)
        {
            return;
        }

        var now = _now();
        List<string> due;
        lock (_gate)
        {
            due = _pending.Where(n => !_lastRead.TryGetValue(n, out var last) || now - last >= Debounce).ToList();
            foreach (var n in due)
            {
                _pending.Remove(n);
                _lastRead[n] = now;
            }
        }

        if (due.Count == 0)
        {
            return;
        }

        var rows = new List<ProgressRow>();
        var removed = new List<string>();
        foreach (var name in due)
        {
            if (name.EndsWith(".control.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var path = Path.Combine(_dir, name);
            var stem = Path.GetFileNameWithoutExtension(name);
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    removed.Add(stem);
                    continue;
                }

                Reads++;
                if (info.Length > ProgressParser.MaxBytes)
                {
                    rows.Add(ProgressParser.Parse(stem, new byte[ProgressParser.MaxBytes + 1], now)); // rejected unread
                    continue;
                }

                rows.Add(ProgressParser.Parse(stem, File.ReadAllBytes(path), now));
            }
            catch (IOException)
            {
                lock (_gate)
                {
                    _pending.Add(name); // locked by the writer: try again next flush
                }
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        if (rows.Count > 0 || removed.Count > 0)
        {
            _post?.Invoke(rows, removed);
        }
    }
}
