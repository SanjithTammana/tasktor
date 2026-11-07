using System.Text.Json;
using System.Text.Json.Nodes;
using Tasktor.Contracts;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Pipe;

namespace Tasktor.Hosts.Common.Pipe;

/// <summary>
/// The viewer's copy of the snapshot (U1 D-H3): the subscribe reply replaces it, each pushed delta patches it. A delta
/// that does not apply (a missed message) asks for a resync instead of guessing. Thread-safe: deltas arrive on the pipe
/// reader thread, viewers read on the UI thread.
/// </summary>
public sealed class SnapshotMirror
{
    private readonly Lock _gate = new();
    private JsonNode? _root;
    private IReadOnlyList<Card> _cards = [];
    private string? _cardsJson;

    public long Seq { get; private set; } = -1;

    public bool HasData
    {
        get
        {
            lock (_gate)
            {
                return _root is not null;
            }
        }
    }

    /// <summary>A copy of the current snapshot root (bindings resolve against it: <c>/state/…</c>).</summary>
    public JsonNode? Root
    {
        get
        {
            lock (_gate)
            {
                return _root?.DeepClone();
            }
        }
    }

    public IReadOnlyList<Card> Cards
    {
        get
        {
            lock (_gate)
            {
                return _cards;
            }
        }
    }

    /// <summary>Apply one delta. Returns false when it cannot apply (the caller resubscribes for a full snapshot).</summary>
    public bool Apply(DeltaMessage delta, out bool cardsChanged)
    {
        ArgumentNullException.ThrowIfNull(delta);
        cardsChanged = false;
        lock (_gate)
        {
            var root = _root?.DeepClone();
            try
            {
                foreach (var op in delta.Ops)
                {
                    if (op.Path.Length > 0 && root is null)
                    {
                        return false; // a patch before the first full snapshot
                    }

                    root = JsonPointer.Apply(root, op.Op, op.Path, op.Value);
                }
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentOutOfRangeException or FormatException)
            {
                return false;
            }

            _root = root;
            Seq = delta.Seq;
            var cardsJson = root?["cards"]?.ToJsonString();
            if (cardsJson != _cardsJson)
            {
                _cardsJson = cardsJson;
                _cards = cardsJson is null ? [] : JsonSerializer.Deserialize(cardsJson, ContractsJsonContext.Default.IReadOnlyListCard) ?? [];
                cardsChanged = true;
            }

            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _root = null;
            _cards = [];
            _cardsJson = null;
            Seq = -1;
        }
    }
}

/// <summary>
/// U4's <c>SnapshotClient</c>: hello → subscribe (state + cards) → deltas into a <see cref="SnapshotMirror"/>, pulls on
/// request, and reconnects with backoff (1 s doubling to 30 s) when the agent restarts or a delta cannot apply. Viewers
/// subscribe only while visible (D-G2: <see cref="Start"/> when shown, <see cref="StopAsync"/> when hidden).
/// </summary>
public sealed class SnapshotClient : IAsyncDisposable
{
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly string _clientName;
    private readonly string? _pipeName;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private CancellationTokenSource? _cts;
    private Task? _run;
    private volatile PipeClient? _client;

    public SnapshotClient(string clientName, string? pipeName = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _clientName = clientName;
        _pipeName = pipeName;
        _delay = delay ?? ((t, ct) => Task.Delay(t, ct));
    }

    public SnapshotMirror Mirror { get; } = new();

    /// <summary>True while connected and subscribed.</summary>
    public bool Connected { get; private set; }

    public long Reconnects { get; private set; }

    /// <summary>The live connection (for <c>act</c> through an <see cref="ActionInvoker"/>), or null.</summary>
    public PipeClient? Client => Connected ? _client : null;

    /// <summary>Raised (pipe thread) after each applied update; the flag says whether the cards changed.</summary>
    public event Action<bool>? Changed;

    /// <summary>Raised when the agent goes away or comes back (the widget shows "Tasktor isn't running").</summary>
    public event Action<bool>? ConnectionChanged;

    public void Start()
    {
        if (_run is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _run = RunAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            if (_run is not null)
            {
                await _run.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
        _cts = null;
        _run = null;
        SetConnected(false);
    }

    /// <summary>Pull a table (processes, series, learned …) over the live connection, or null when not connected.</summary>
    public async Task<JsonNode?> PullAsync(string table, IReadOnlyDictionary<string, string>? args = null, int? top = null, CancellationToken ct = default) =>
        Client is { } c ? (await c.PullAsync(table, args, top, ct).ConfigureAwait(false))?.Data : null;

    private async Task RunAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var client = await PipeClient.ConnectAsync(_clientName, _pipeName, 1500, ct).ConfigureAwait(false);
                _client = client;
                var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                client.Pushed += m =>
                {
                    if (m is DeltaMessage d && !Apply(d))
                    {
                        lost.TrySetResult(); // could not patch: resubscribe from a full snapshot
                    }
                };
                client.Closed += () => lost.TrySetResult();
                var first = await client.RequestAsync(new SubscribeMessage { Cards = true }, ct).ConfigureAwait(false);
                if (first is not DeltaMessage initial || !Apply(initial))
                {
                    throw new IOException("subscribe failed");
                }

                SetConnected(true);
                backoff = TimeSpan.FromSeconds(1);
                using (ct.Register(() => lost.TrySetResult()))
                {
                    await lost.Task.ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is AgentNotRunningException or IOException or InvalidOperationException or ObjectDisposedException)
            {
                // not running, refused or dropped: wait and try again
            }
            catch (OperationCanceledException)
            {
                break;
            }
            finally
            {
                _client = null;
                SetConnected(false);
            }

            if (ct.IsCancellationRequested)
            {
                break;
            }

            Reconnects++;
            Mirror.Clear();
            try
            {
                await _delay(backoff, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            backoff = NextBackoff(backoff);
        }
    }

    public static TimeSpan NextBackoff(TimeSpan current) => TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, current.Ticks * 2));

    private bool Apply(DeltaMessage d)
    {
        if (!Mirror.Apply(d, out var cardsChanged))
        {
            return false;
        }

        Changed?.Invoke(cardsChanged);
        return true;
    }

    private void SetConnected(bool on)
    {
        if (Connected == on)
        {
            return;
        }

        Connected = on;
        ConnectionChanged?.Invoke(on);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
