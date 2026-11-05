using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Tasktor.Contracts;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Pipe;
using Tasktor.Contracts.Platform;
using Tasktor.Core.Actions;
using Tasktor.Core.Logging;
using Tasktor.Core.Loop;
using Tasktor.Core.State;
using Tasktor.Hosts.Common.Pipe;

namespace Tasktor.Agent.Host;

/// <summary>
/// The agent's pipe (U1 D-H3): current user only (<see cref="PipeOptions.CurrentUserOnly"/>), ≤ 16 clients, JSON lines.
/// Each client has a reader task that only posts to the logic loop and a writer task that drains its queue; all session
/// state lives on the loop. Deltas are coalesced to ≤ 1 per 500 ms per client by diffing against what that client has.
/// Leases die with the connection. No TCP listener, ever.
/// </summary>
public sealed class PipeServer : IAsyncDisposable
{
    public const int MaxClients = 16;
    public static readonly TimeSpan DeltaInterval = TimeSpan.FromMilliseconds(500);
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.Ordinal) { ContractIds.Snapshot, ContractIds.Action };

    private readonly string _pipeName;
    private readonly LogicLoop _loop;
    private readonly SnapshotPublisher _publisher;
    private readonly ActionRouter _router;
    private readonly Scheduler _scheduler;
    private readonly LogSink? _log;
    private readonly string _agentVersion;
    private readonly Dictionary<string, Func<PullMessage, JsonNode?>> _pulls = new(StringComparer.Ordinal);
    private readonly List<Session> _sessions = [];
    private readonly Lock _sessionsGate = new(); // the loop adds and removes; dispose runs on another thread
    private readonly CancellationTokenSource _cts = new();
    private Task? _accept;
    private JsonNode? _latest;

    public PipeServer(string pipeName, LogicLoop loop, SnapshotPublisher publisher, ActionRouter router, Scheduler scheduler, string agentVersion, LogSink? log = null)
    {
        _pipeName = pipeName;
        _loop = loop;
        _publisher = publisher;
        _router = router;
        _scheduler = scheduler;
        _agentVersion = agentVersion;
        _log = log;
        _publisher.Published += OnPublished;
        RegisterPull("snapshot", _ => Latest()?.DeepClone());
        RegisterPull("catalog", _ => JsonSerializer.SerializeToNode(ActionCatalog.All.Select(a => a.Id).ToList(), ContractsJsonContext.Default.IReadOnlyListString));
    }

    /// <summary>
    /// Decides which origins a client may use, from its hello name and (on Windows) its verified image path. Default:
    /// a client named <c>flyout</c>, <c>widget</c> or <c>shell</c> gets that origin (plus toast for the flyout); anything
    /// else is treated as the CLI.
    /// </summary>
    public Func<string, NamedPipeServerStream, IReadOnlySet<Origin>> OriginsFor { get; set; } = DefaultOrigins;

    public int ClientCount
    {
        get
        {
            lock (_sessionsGate)
            {
                return _sessions.Count;
            }
        }
    }

    /// <summary>Register a pull table (loop-only handler).</summary>
    public void RegisterPull(string table, Func<PullMessage, JsonNode?> handler) => _pulls[table] = handler;

    public void Start() => _accept ??= Task.Run(AcceptLoop);

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _publisher.Published -= OnPublished;
        if (_accept is not null)
        {
            try
            {
                await _accept.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        List<Session> open;
        lock (_sessionsGate)
        {
            open = [.. _sessions];
        }

        foreach (var s in open)
        {
            s.Close();
        }

        _cts.Dispose();
    }

    public static IReadOnlySet<Origin> DefaultOrigins(string client, NamedPipeServerStream stream) => client switch
    {
        "flyout" => new HashSet<Origin> { Origin.Flyout, Origin.Toast },
        "widget" => new HashSet<Origin> { Origin.Widget },
        "shell" => new HashSet<Origin> { Origin.Shell },
        _ => new HashSet<Origin> { Origin.Cli },
    };

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream stream;
            try
            {
                stream = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, MaxClients, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (IOException)
            {
                // All instances busy: wait for one to free up.
                await Task.Delay(200, _cts.Token).ConfigureAwait(false);
                continue;
            }

            try
            {
                await stream.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (IOException)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            var session = new Session(this, stream);
            _loop.Post(() =>
            {
                lock (_sessionsGate)
                {
                    _sessions.Add(session);
                }
            });
            session.Start();
        }
    }

    private JsonNode? Latest()
    {
        if (_latest is null && _publisher.LatestBytes is { } bytes)
        {
            _latest = JsonNode.Parse(bytes);
        }

        return _latest;
    }

    private void OnPublished(SnapshotPublished p)
    {
        _latest = null;
        List<Session> current;
        lock (_sessionsGate)
        {
            current = [.. _sessions];
        }

        foreach (var s in current)
        {
            s.MarkDirty();
        }
    }

    private sealed class Session
    {
        private readonly PipeServer _server;
        private readonly NamedPipeServerStream _stream;
        private readonly Channel<string> _out = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        private readonly Dictionary<string, DemandLease> _leases = new(StringComparer.Ordinal);
        private IReadOnlySet<Origin> _origins = new HashSet<Origin> { Origin.Cli };
        private bool _hello;
        private bool _subscribed;
        private bool _withCards;
        private List<string> _paths = [];
        private JsonNode? _sent;
        private DateTimeOffset _lastFlush = DateTimeOffset.MinValue;
        private IDisposable? _flushTimer;
        private bool _closed;

        public Session(PipeServer server, NamedPipeServerStream stream)
        {
            _server = server;
            _stream = stream;
        }

        public void Start()
        {
            _ = Task.Run(ReadLoop);
            _ = Task.Run(WriteLoop);
        }

        public void Close()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            _out.Writer.TryComplete();
            try
            {
                _stream.Dispose();
            }
            catch (IOException)
            {
            }
        }

        public void MarkDirty()
        {
            if (!_subscribed || _flushTimer is not null)
            {
                return;
            }

            var now = _server._loop.Clock.UtcNow;
            var due = now - _lastFlush >= DeltaInterval ? now : _lastFlush + DeltaInterval;
            _flushTimer = _server._loop.At(due, Flush);
        }

        private void Send(PipeMessage m) => _out.Writer.TryWrite(PipeFraming.Encode(m));

        private async Task ReadLoop()
        {
            var reader = new PipeFraming.LineReader(_stream);
            try
            {
                while (true)
                {
                    var line = await reader.ReadLineAsync(_server._cts.Token).ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    var msg = PipeFraming.Decode(line);
                    _server._loop.Post(() => Handle(msg));
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
            }

            _server._loop.Post(Disconnected);
        }

        private async Task WriteLoop()
        {
            try
            {
                await foreach (var line in _out.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    await PipeFraming.WriteAsync(_stream, line, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _server._loop.Post(Disconnected);
            }
        }

        private void Disconnected()
        {
            foreach (var l in _leases.Values)
            {
                l.Dispose();
            }

            _leases.Clear();
            _flushTimer?.Dispose();
            lock (_server._sessionsGate)
            {
                _server._sessions.Remove(this);
            }
            Close();
        }

        private void Handle(PipeMessage? msg)
        {
            if (msg is null)
            {
                Send(new ErrorMessage { Code = "bad_message", Message = "Not a valid message" });
                return;
            }

            if (msg is HelloMessage hello)
            {
                var unknown = hello.Contracts.FirstOrDefault(c => !Supported.Contains(c));
                if (unknown is not null)
                {
                    Send(new ErrorMessage { Id = hello.Id, Code = "unsupported_contract", Message = unknown });
                    return;
                }

                _hello = true;
                _origins = _server.OriginsFor(hello.Client, _stream);
                Send(new WelcomeMessage { Id = hello.Id, Agent = _server._agentVersion, Contracts = [.. Supported] });
                return;
            }

            if (!_hello)
            {
                Send(new ErrorMessage { Id = msg.Id, Code = "hello_first" });
                return;
            }

            switch (msg)
            {
                case SubscribeMessage sub:
                    _subscribed = true;
                    _withCards = sub.Cards;
                    _paths = [.. sub.Paths ?? []];
                    var initial = View();
                    _sent = initial;
                    _lastFlush = _server._loop.Clock.UtcNow;
                    Send(new DeltaMessage { Id = sub.Id, Seq = _server._publisher.Seq, Ops = [new PatchOp { Op = "replace", Path = string.Empty, Value = initial }] });
                    break;
                case UnsubscribeMessage u:
                    _subscribed = false;
                    _sent = null;
                    Send(new ErrorMessage { Id = u.Id, Code = "ok" });
                    break;
                case PullMessage pull:
                    if (!_server._pulls.TryGetValue(pull.Table, out var handler))
                    {
                        Send(new ErrorMessage { Id = pull.Id, Code = "unknown_table", Message = pull.Table });
                        break;
                    }

                    Send(new PullResultMessage { Id = pull.Id, Table = pull.Table, Data = handler(pull) });
                    break;
                case ActMessage act:
                    var req = act.Request;
                    if (!_origins.Contains(req.Origin))
                    {
                        // A client may only speak for the origins it is: anything unverified acts as the CLI.
                        req = req with { Origin = _origins.Contains(Origin.Cli) ? Origin.Cli : _origins.First() };
                    }

                    var id = act.Id;
                    _server._router.Submit(req).ContinueWith(
                        t => _server._loop.Post(() => Send(new ResultMessage
                        {
                            Id = id,
                            Result = t.IsCompletedSuccessfully ? t.Result : new ActionResult { Action = req.Action, Status = ActionStatus.Failed, Message = "Failed" },
                        })),
                        TaskScheduler.Default);
                    break;
                case LeaseMessage lease:
                    if (_leases.Remove(lease.Source, out var old))
                    {
                        old.Dispose();
                    }

                    if (lease.IntervalMs > 0)
                    {
                        if (!_server._scheduler.IsRegistered(lease.Source))
                        {
                            Send(new ErrorMessage { Id = lease.Id, Code = "unknown_source", Message = lease.Source });
                            break;
                        }

                        var interval = TimeSpan.FromMilliseconds(Math.Max(500, lease.IntervalMs));
                        _leases[lease.Source] = _server._scheduler.Lease(lease.Source, interval, ParseColumns(lease.Columns), holder: "pipe");
                    }

                    Send(new ErrorMessage { Id = lease.Id, Code = "ok" });
                    break;
                default:
                    Send(new ErrorMessage { Id = msg.Id, Code = "unsupported", Message = msg.GetType().Name });
                    break;
            }
        }

        private void Flush()
        {
            _flushTimer = null;
            if (!_subscribed || _closed)
            {
                return;
            }

            var now = View();
            var ops = Core.State.JsonDiff.Diff(_sent, now);
            _sent = now;
            _lastFlush = _server._loop.Clock.UtcNow;
            if (ops.Count > 0)
            {
                Send(new DeltaMessage { Seq = _server._publisher.Seq, Ops = ops });
            }
        }

        /// <summary>The part of the snapshot this client subscribed to.</summary>
        private JsonObject View()
        {
            var full = _server.Latest() as JsonObject ?? [];
            if (_paths.Count == 0)
            {
                var all = (JsonObject)full.DeepClone();
                if (!_withCards)
                {
                    all.Remove("cards");
                }

                return all;
            }

            var view = new JsonObject();
            foreach (var p in _paths)
            {
                if (JsonPointer.TryResolve(full, p, out var node))
                {
                    Place(view, p, node?.DeepClone());
                }
            }

            if (_withCards && full["cards"] is { } cards)
            {
                view["cards"] = cards.DeepClone();
            }

            return view;
        }

        private static void Place(JsonObject root, string pointer, JsonNode? value)
        {
            var tokens = pointer.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(JsonPointer.Unescape).ToList();
            var node = root;
            for (var i = 0; i < tokens.Count - 1; i++)
            {
                if (node[tokens[i]] is not JsonObject child)
                {
                    child = [];
                    node[tokens[i]] = child;
                }

                node = child;
            }

            if (tokens.Count > 0)
            {
                node[tokens[^1]] = value;
            }
        }

        private static ColumnSet ParseColumns(IReadOnlyList<string>? columns)
        {
            if (columns is null || columns.Count == 0)
            {
                return ColumnSet.All;
            }

            var set = ColumnSet.None;
            foreach (var c in columns)
            {
                if (Enum.TryParse<ColumnSet>(c, ignoreCase: true, out var v))
                {
                    set |= v;
                }
            }

            return set == ColumnSet.None ? ColumnSet.All : set;
        }
    }
}
