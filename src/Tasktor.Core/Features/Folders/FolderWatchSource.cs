using Tasktor.Contracts.Platform;

namespace Tasktor.Core.Features.Folders;

/// <summary>
/// I12's debounce: a new file fires only once its size has been stable for 2 s, it is not locked, and its name is not a
/// partial download. Pure: the caller probes the file system. Loop-only.
/// </summary>
public sealed class FileSettle
{
    public static readonly TimeSpan Stable = TimeSpan.FromSeconds(2);
    public static readonly string[] Partial = [".crdownload", ".part", ".tmp", ".partial", ".download", ".opdownload"];

    private readonly Dictionary<string, (string Folder, long Size, DateTimeOffset Since)> _pending = new(StringComparer.OrdinalIgnoreCase);

    public int Pending => _pending.Count;

    public static bool IsPartial(string path) =>
        Partial.Any(p => path.EndsWith(p, StringComparison.OrdinalIgnoreCase)) || Path.GetFileName(path).StartsWith("~$", StringComparison.Ordinal)
        || Path.GetFileName(path).StartsWith('.');

    public void Seen(string folder, string path, DateTimeOffset now)
    {
        if (!IsPartial(path))
        {
            _pending[path] = (folder, -1, now);
        }
    }

    public void Gone(string path) => _pending.Remove(path);

    /// <summary>Probe each pending file (null = gone; otherwise size and locked) and return the ones that settled.</summary>
    public List<(string Folder, string Path)> Check(DateTimeOffset now, Func<string, (long Size, bool Locked)?> probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var ready = new List<(string, string)>();
        foreach (var (path, p) in _pending.ToList())
        {
            if (probe(path) is not { } f)
            {
                _pending.Remove(path);
                continue;
            }

            if (f.Size != p.Size)
            {
                _pending[path] = (p.Folder, f.Size, now); // still growing: restart the 2 s
                continue;
            }

            if (now - p.Since >= Stable && !f.Locked)
            {
                _pending.Remove(path);
                ready.Add((p.Folder, path));
            }
        }

        return ready;
    }
}

/// <summary>
/// U2 <c>FolderWatchSource</c> (I12): one non-recursive watch per chosen folder (≤ 8), events only, no polling of the
/// listing. New and renamed-into files go through <see cref="FileSettle"/>; while some are settling, a 1 s check runs.
/// </summary>
public sealed class FolderWatchSource : IEventSource<FileEvent>, IDisposable
{
    public const int MaxFolders = 8;

    private readonly IReadOnlyList<string> _folders;
    private readonly Func<DateTimeOffset> _now;
    private readonly FileSettle _settle = new();
    private readonly Lock _gate = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private Timer? _timer;
    private Action<FileEvent>? _post;

    public FolderWatchSource(IEnumerable<string> folders, Func<DateTimeOffset> now)
    {
        ArgumentNullException.ThrowIfNull(folders);
        _folders = [.. folders.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxFolders)];
        _now = now;
    }

    public string Name => "folder-watch";

    public IReadOnlyList<string> Folders => _folders;

    public void Start(Action<FileEvent> post)
    {
        _post = post;
        foreach (var folder in _folders.Where(Directory.Exists))
        {
            var w = new FileSystemWatcher(folder) { NotifyFilter = NotifyFilters.FileName, IncludeSubdirectories = false, InternalBufferSize = 8192 };
            w.Created += (_, e) => Seen(folder, e.FullPath);
            w.Renamed += (_, e) =>
            {
                lock (_gate)
                {
                    _settle.Gone(e.OldFullPath);
                }

                Seen(folder, e.FullPath);
            };
            w.Deleted += (_, e) =>
            {
                lock (_gate)
                {
                    _settle.Gone(e.FullPath);
                }
            };
            w.EnableRaisingEvents = true;
            _watchers.Add(w);
        }

        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Stop() => Dispose();

    private void Seen(string folder, string path)
    {
        lock (_gate)
        {
            _settle.Seen(folder, path, _now());
            _timer?.Change(1000, 1000);
        }
    }

    private void Tick()
    {
        List<(string Folder, string Path)> ready;
        lock (_gate)
        {
            ready = _settle.Check(_now(), Probe);
            if (_settle.Pending == 0)
            {
                _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }

        foreach (var (folder, path) in ready)
        {
            _post?.Invoke(new FileEvent(folder, Sensitive.Of(path), _now()));
        }
    }

    /// <summary>Size, and whether another process has it open for writing (a share-none open fails), or null when gone.</summary>
    public static (long Size, bool Locked)? Probe(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return (info.Length, false);
            }
            catch (IOException)
            {
                return (info.Length, true);
            }
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var w in _watchers)
        {
            w.Dispose();
        }

        _watchers.Clear();
        _timer?.Dispose();
        _timer = null;
    }
}
