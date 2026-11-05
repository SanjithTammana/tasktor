using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tasktor.Contracts;
using Tasktor.Contracts.Capture;
using Tasktor.Hosts.Common.Pipe;

namespace Tasktor.Capture.Browser;

/// <summary>
/// JSON lines with a 1 MB cap per line (the capture pipe's contract): an over-long line is dropped and counted and the
/// connection stays open, unlike the agent pipe's reader.
/// </summary>
public sealed class CappedLineReader(Stream stream, int maxBytes = CappedLineReader.MaxLineBytes)
{
    public const int MaxLineBytes = 1024 * 1024;
    private readonly byte[] _buf = new byte[16 * 1024];
    private readonly MemoryStream _line = new();
    private int _pos;
    private int _len;
    private bool _skipping;

    public long Dropped { get; private set; }

    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        while (true)
        {
            if (_pos < _len)
            {
                var nl = Array.IndexOf(_buf, (byte)'\n', _pos, _len - _pos);
                var end = nl >= 0 ? nl : _len;
                if (!_skipping)
                {
                    _line.Write(_buf, _pos, end - _pos);
                    if (_line.Length > maxBytes)
                    {
                        _skipping = true;
                        _line.SetLength(0);
                        Dropped++;
                    }
                }

                _pos = nl >= 0 ? nl + 1 : _len;
                if (nl >= 0)
                {
                    if (_skipping)
                    {
                        _skipping = false;
                        continue;
                    }

                    var text = Encoding.UTF8.GetString(_line.GetBuffer(), 0, (int)_line.Length).TrimEnd('\r');
                    _line.SetLength(0);
                    return text;
                }
            }

            _len = await stream.ReadAsync(_buf, ct).ConfigureAwait(false);
            _pos = 0;
            if (_len == 0)
            {
                return null;
            }
        }
    }
}

/// <summary>
/// The nmh's offline queue (<c>capture\nmh-queue.jsonl</c>), drained by the capture host at start under the writer's own
/// named mutex (<c>tasktor-nmh-queue-</c> + upper-case hex SHA-256 of the full path), so a concurrent append cannot be lost
/// between the read and the delete. Only context messages are accepted from it.
/// </summary>
public static class QueueDrain
{
    public static string MutexName(string path) =>
        "tasktor-nmh-queue-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path))));

    public static (List<BrowserMessage> Messages, int Invalid) Drain(string path)
    {
        var messages = new List<BrowserMessage>();
        var invalid = 0;
        string[] lines;
        using (var mutex = new Mutex(false, MutexName(path)))
        {
            try
            {
                mutex.WaitOne();
            }
            catch (AbandonedMutexException)
            {
                // a writer died holding it: the file is still whole (it is replaced atomically)
            }

            try
            {
                if (!File.Exists(path))
                {
                    return (messages, 0);
                }

                lines = File.ReadAllLines(path);
                File.Delete(path);
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }

        foreach (var line in lines.Where(l => l.Length > 0))
        {
            try
            {
                var m = JsonSerializer.Deserialize(line, ContractsJsonContext.Default.BrowserMessage);
                if (m is BrowserTab or BrowserFocus or BrowserVisibility or BrowserTabClosed)
                {
                    messages.Add(m);
                }
                else
                {
                    invalid++;
                }
            }
            catch (JsonException)
            {
                invalid++;
            }
        }

        return (messages, invalid);
    }
}

/// <summary>
/// <c>tasktor-capture</c>'s browser pipe (<c>PipeName.ForCapture()</c>, current user only): one session per tasktor-nmh
/// connection; replies in order; every session gets its own <see cref="BlockState"/> pushed when blocks change. A refused
/// hello is answered, then the connection is closed.
/// </summary>
public sealed class CapturePipeServer : IAsyncDisposable
{
    private readonly BrowserEndpoint _endpoint;
    private readonly string _name;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (Stream Stream, SemaphoreSlim Write)> _sessions = new(StringComparer.Ordinal);
    private Task? _accept;
    private int _next;

    public CapturePipeServer(BrowserEndpoint endpoint, string? name = null)
    {
        _endpoint = endpoint;
        _name = name ?? PipeName.ForCapture();
        _endpoint.BlocksChanged += () => _ = PushAllAsync();
    }

    public long DroppedLines { get; private set; }

    public long InvalidLines { get; private set; }

    public int Sessions
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Count;
            }
        }
    }

    public void Start() => _accept = AcceptLoopAsync(_cts.Token);

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(_name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return;
            }

            var id = "s" + Interlocked.Increment(ref _next);
            _ = ServeAsync(id, pipe, ct);
        }
    }

    private async Task ServeAsync(string id, NamedPipeServerStream pipe, CancellationToken ct)
    {
        var write = new SemaphoreSlim(1, 1);
        lock (_gate)
        {
            _sessions[id] = (pipe, write);
        }

        var reader = new CappedLineReader(pipe);
        try
        {
            while (!ct.IsCancellationRequested && await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                DroppedLines = reader.Dropped;
                BrowserMessage? message;
                try
                {
                    message = JsonSerializer.Deserialize(line, ContractsJsonContext.Default.BrowserMessage);
                }
                catch (JsonException)
                {
                    InvalidLines++;
                    continue;
                }

                if (message is null)
                {
                    InvalidLines++;
                    continue;
                }

                var replies = _endpoint.Handle(id, message);
                foreach (var r in replies)
                {
                    await SendAsync(pipe, write, r, ct).ConfigureAwait(false);
                }

                if (message is BrowserHello && _endpoint.BrowserOf(id) is null)
                {
                    break; // refused hello (wrong contract or client): answered, now closed
                }
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // the browser closed or we are stopping
        }
        finally
        {
            lock (_gate)
            {
                _sessions.Remove(id);
            }

            await pipe.DisposeAsync().ConfigureAwait(false);
            write.Dispose();
        }
    }

    private static async Task SendAsync(Stream pipe, SemaphoreSlim write, BrowserMessage m, CancellationToken ct)
    {
        var line = JsonSerializer.Serialize(m, ContractsJsonContext.Default.BrowserMessage);
        await write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await PipeFraming.WriteAsync(pipe, line, ct).ConfigureAwait(false);
        }
        finally
        {
            write.Release();
        }
    }

    private async Task PushAllAsync()
    {
        List<(string Id, Stream Stream, SemaphoreSlim Write)> open;
        lock (_gate)
        {
            open = [.. _sessions.Select(kv => (kv.Key, kv.Value.Stream, kv.Value.Write))];
        }

        foreach (var (id, stream, write) in open)
        {
            if (_endpoint.BrowserOf(id) is not { } browser)
            {
                continue;
            }

            try
            {
                await SendAsync(stream, write, new BrowserBlocks { At = DateTimeOffset.UtcNow, State = _endpoint.StateFor(browser) }, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // that session is closing
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_accept is not null)
        {
            await _accept.ConfigureAwait(false);
        }

        _cts.Dispose();
    }
}
