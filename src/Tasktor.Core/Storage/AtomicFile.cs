namespace Tasktor.Core.Storage;

/// <summary>Atomic replace: write <c>&lt;path&gt;.tmp</c>, then rename over the target, so readers never see a half-written file.</summary>
public static class AtomicFile
{
    public static void Write(string path, ReadOnlySpan<byte> bytes)
    {
        ArgumentNullException.ThrowIfNull(path);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes);
            fs.Flush(flushToDisk: false);
        }

        File.Move(tmp, path, overwrite: true);
    }

    public static void WriteText(string path, string text) => Write(path, System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>Write with a <c>.bak</c> of the previous version (settings migration, D-H5).</summary>
    public static void WriteWithBackup(string path, ReadOnlySpan<byte> bytes)
    {
        if (File.Exists(path))
        {
            File.Copy(path, path + ".bak", overwrite: true);
        }

        Write(path, bytes);
    }
}

/// <summary>
/// "The loop takes a byte copy; a worker writes it" (D-A5 §7). Keeps only the newest bytes per path and writes them on
/// the thread pool, so a burst of updates costs one write. Thread-safe.
/// </summary>
public sealed class CoalescingWriter
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, byte[]> _pending = new(StringComparer.Ordinal);
    private readonly Action<Exception>? _onError;
    private Task _running = Task.CompletedTask;
    private bool _scheduled;

    public CoalescingWriter(Action<Exception>? onError = null) => _onError = onError;

    public long Writes { get; private set; }

    public void Enqueue(string path, byte[] bytes)
    {
        lock (_gate)
        {
            _pending[path] = bytes;
            if (_scheduled)
            {
                return;
            }

            _scheduled = true;
            _running = Task.Run(Drain);
        }
    }

    /// <summary>Wait for pending writes (shutdown, tests).</summary>
    public Task FlushAsync()
    {
        lock (_gate)
        {
            return _running;
        }
    }

    private void Drain()
    {
        while (true)
        {
            KeyValuePair<string, byte[]>[] batch;
            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    _scheduled = false;
                    return;
                }

                batch = [.. _pending];
                _pending.Clear();
            }

            foreach (var kv in batch)
            {
                try
                {
                    AtomicFile.Write(kv.Key, kv.Value);
                    Writes++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _onError?.Invoke(ex);
                }
            }
        }
    }
}
