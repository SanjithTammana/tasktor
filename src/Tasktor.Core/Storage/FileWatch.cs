namespace Tasktor.Core.Storage;

/// <summary>
/// Watches files or folders for changes (Windows: <c>ReadDirectoryChangesW</c> under <see cref="FileSystemWatcher"/>): no
/// polling. <c>changed</c> runs on a thread-pool thread; callers post to the loop and debounce. Paths whose folder does not
/// exist are skipped (watching starts when the feature is re-enabled or the paths change).
/// </summary>
public sealed class FileWatch : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];

    public FileWatch(IEnumerable<string> paths, Action changed, bool folders = false)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(changed);
        foreach (var group in paths.Where(p => !string.IsNullOrWhiteSpace(p)).GroupBy(p => folders ? p : Path.GetDirectoryName(Path.GetFullPath(p)) ?? "", StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(group.Key))
            {
                continue;
            }

            var names = folders ? null : group.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var w = new FileSystemWatcher(group.Key) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size, InternalBufferSize = 8192 };
            void On(object? s, FileSystemEventArgs e)
            {
                if (names is null || names.Contains(e.Name ?? "") || (e is RenamedEventArgs r && names.Contains(r.OldName ?? "")))
                {
                    changed();
                }
            }

            w.Changed += On;
            w.Created += On;
            w.Deleted += On;
            w.Renamed += (s, e) => On(s, e);
            w.Error += (_, _) => changed(); // an overflow: re-read everything
            w.EnableRaisingEvents = true;
            _watchers.Add(w);
        }
    }

    public int Count => _watchers.Count;

    public void Dispose()
    {
        foreach (var w in _watchers)
        {
            w.Dispose();
        }

        _watchers.Clear();
    }
}
