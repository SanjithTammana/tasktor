using System.Collections.Concurrent;
using System.IO.Pipes;
using Tasktor.Contracts;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Pipe;

namespace Tasktor.Hosts.Common.Pipe;

/// <summary>
/// The agent is not running or the pipe dropped (CLI exit code 3). An <see cref="IOException"/>, so every caller that
/// already treats pipe I/O failure as "try again later" handles agent loss the same way instead of letting it escape.
/// </summary>
public sealed class AgentNotRunningException(string message) : IOException(message);

/// <summary>
/// A pipe client: connects, says hello, then sends requests and matches replies by id. Pushed <c>delta</c> messages go to
/// <see cref="Pushed"/>. Current-user only on both ends (<see cref="PipeOptions.CurrentUserOnly"/>).
/// </summary>
public sealed class PipeClient : IAsyncDisposable
{
    public static readonly IReadOnlyList<string> Contracts = [ContractIds.Snapshot, ContractIds.Action];

    private readonly NamedPipeClientStream _stream;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<PipeMessage>> _waiting = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private Task? _reader;
    private long _nextId;

    private PipeClient(NamedPipeClientStream stream) => _stream = stream;

    public WelcomeMessage? Welcome { get; private set; }

    /// <summary>Raised on the reader thread for each pushed message (deltas).</summary>
    public event Action<PipeMessage>? Pushed;

    public bool IsConnected => _stream.IsConnected;

    public static async Task<PipeClient> ConnectAsync(string clientName, string? pipeName = null, int timeoutMs = 1500, CancellationToken ct = default)
    {
        var stream = new NamedPipeClientStream(".", pipeName ?? PipeName.ForCurrentUser(), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await stream.ConnectAsync(timeoutMs, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw new AgentNotRunningException("Tasktor is not running");
        }

        var client = new PipeClient(stream);
        client._reader = client.ReadLoop();
        var reply = await client.RequestAsync(new HelloMessage { Client = clientName, Contracts = Contracts }, ct).ConfigureAwait(false);
        if (reply is ErrorMessage err)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("Agent refused the handshake: " + err.Code);
        }

        client.Welcome = reply as WelcomeMessage;
        return client;
    }

    public async Task<PipeMessage> RequestAsync(PipeMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var id = Interlocked.Increment(ref _nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var tcs = new TaskCompletionSource<PipeMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[id] = tcs;
        await SendAsync(message with { Id = id }, ct).ConfigureAwait(false);
        using (ct.Register(() => tcs.TrySetCanceled(ct)))
        {
            return await tcs.Task.ConfigureAwait(false);
        }
    }

    public async Task<ActionResult> ActAsync(ActionRequest request, CancellationToken ct = default)
    {
        var reply = await RequestAsync(new ActMessage { Request = request }, ct).ConfigureAwait(false);
        return reply switch
        {
            ResultMessage r => r.Result,
            ErrorMessage e => new ActionResult { Action = request.Action, Status = ActionStatus.Failed, Reason = e.Code, Message = e.Message },
            _ => new ActionResult { Action = request.Action, Status = ActionStatus.Failed, Message = "Unexpected reply" },
        };
    }

    public async Task<PullResultMessage?> PullAsync(string table, IReadOnlyDictionary<string, string>? args = null, int? top = null, CancellationToken ct = default)
    {
        var reply = await RequestAsync(new PullMessage { Table = table, Args = args, Top = top }, ct).ConfigureAwait(false);
        return reply as PullResultMessage;
    }

    public async Task SendAsync(PipeMessage message, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await PipeFraming.WriteAsync(_stream, PipeFraming.Encode(message), ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        await _stream.DisposeAsync().ConfigureAwait(false);
        if (_reader is not null)
        {
            try
            {
                await _reader.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        foreach (var w in _waiting.Values)
        {
            w.TrySetException(new AgentNotRunningException("disconnected"));
        }

        _cts.Dispose();
        _writeLock.Dispose();
    }

    private async Task ReadLoop()
    {
        var reader = new PipeFraming.LineReader(_stream);
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (PipeFraming.Decode(line) is not { } msg)
                {
                    continue;
                }

                if (msg.Id is { } id && _waiting.TryRemove(id, out var tcs))
                {
                    tcs.TrySetResult(msg);
                }
                else
                {
                    Pushed?.Invoke(msg);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }

        foreach (var w in _waiting.Values)
        {
            w.TrySetException(new AgentNotRunningException("disconnected"));
        }

        Closed?.Invoke();
    }

    /// <summary>Raised once on the reader thread when the connection ends (the agent quit or the pipe broke).</summary>
    public event Action? Closed;
}
