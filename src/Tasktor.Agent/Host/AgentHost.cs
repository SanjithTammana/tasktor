using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Snapshot;
using Tasktor.Core.Actions;
using Tasktor.Core.Logging;
using Tasktor.Core.Loop;
using Tasktor.Core.State;
using Tasktor.Core.Storage;
using Tasktor.Core.Time;

namespace Tasktor.Agent.Host;

/// <summary>What the composition root needs from its caller: the store, the clock and the OS adapters (fakes in tests).</summary>
public sealed record AgentOptions
{
    public required StoreLayout Store { get; init; }

    public IClock Clock { get; init; } = SystemClock.Instance;

    public string? PipeName { get; init; }

    public string Version { get; init; } = typeof(AgentHost).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    /// <summary>Fresh single-process read for the guards (Windows: <c>Win32ProcessTable</c>).</summary>
    public Func<ProcKey, ProcessRow?> ReadProcess { get; init; } = _ => null;

    public IProcessControl? ProcessControl { get; init; }

    public INotifier? Notifier { get; init; }

    public bool WriteSnapshotFile { get; init; } = true;

    public ILogWriter? LogWriter { get; init; }

    /// <summary>For tests: run handlers inline instead of on the thread pool.</summary>
    public Func<Func<ActionResult>, Task<ActionResult>>? OffLoop { get; init; }
}

/// <summary>
/// The agent's hand-written composition root (no reflection DI; Native AOT). Owns the one logic loop and wires state,
/// cards, the publisher, the router and the pipe. Features attach through <see cref="Attach"/>. Tests run it in-process.
/// </summary>
public sealed class AgentHost : IAsyncDisposable
{
    private readonly CoalescingWriter _writer = new();
    private readonly List<IDisposable> _owned = [];
    private PausedState _paused = new();
    private SelfStats? _self;
    private bool _cliAllowDestructive;

    public AgentHost(AgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
        Store = options.Store;
        Directory.CreateDirectory(Store.Root);
        Log = new LogSink(options.Clock, options.LogWriter ?? new FileLogWriter(Store.LogDir));
        Loop = new LogicLoop(options.Clock, ex => Log.Error("loop.error", LogField.Of("type", ex.GetType().Name)));
        Scheduler = new Scheduler(options.Clock);
        State = new StateTracker();
        Cards = new CardTemplates();
        Publisher = new SnapshotPublisher(Loop, State, Cards, AgentInfo, options.WriteSnapshotFile ? bytes => _writer.Enqueue(Store.Snapshot, bytes) : null);
        Router = new ActionRouter(
            Loop,
            new OriginPolicy { CliAllowDestructive = () => _cliAllowDestructive },
            GuardChain.Default(),
            options.ReadProcess,
            Log,
            options.OffLoop);
        Pipe = new PipeServer(options.PipeName ?? Hosts.Common.Pipe.PipeName.ForCurrentUser(), Loop, Publisher, Router, Scheduler, options.Version, Log);
        RegisterCoreHandlers();
    }

    public AgentOptions Options { get; }

    public StoreLayout Store { get; }

    public LogSink Log { get; }

    public LogicLoop Loop { get; }

    public Scheduler Scheduler { get; }

    public StateTracker State { get; }

    public CardTemplates Cards { get; }

    public SnapshotPublisher Publisher { get; }

    public ActionRouter Router { get; }

    public PipeServer Pipe { get; }

    public PausedState Paused => _paused;

    /// <summary>Raised on the loop when the user asks to quit (tray, flyout).</summary>
    public event Action? QuitRequested;

    /// <summary><c>cli.allow_destructive</c> (off by default). Settings set it on the loop.</summary>
    public bool CliAllowDestructive
    {
        get => _cliAllowDestructive;
        set => _cliAllowDestructive = value;
    }

    /// <summary>Attach a card provider (a feature). Loop-only after start.</summary>
    public void Attach(ICardProvider provider)
    {
        Cards.Add(provider);
        Publisher.MarkChanged();
    }

    /// <summary>Change state on the loop and schedule a publish.</summary>
    public void UpdateState(Func<SnapshotState, SnapshotState> change)
    {
        State.Update(change);
        Publisher.MarkChanged();
    }

    public void UpdateSelf(SelfStats self)
    {
        _self = self;
        Publisher.MarkChanged();
    }

    /// <summary>Start the loop thread and the pipe; publish the first snapshot (D-H10: start to first snapshot &lt; 1 s).</summary>
    public void Start()
    {
        Loop.Start();
        Loop.Post(() => Publisher.Publish(force: true));
        Pipe.Start();
    }

    /// <summary>Run <paramref name="work"/> on the loop and wait for it (tests, startup wiring).</summary>
    public Task<T> OnLoop<T>(Func<T> work) => Loop.InvokeAsync(work);

    public Task OnLoop(Action work) => Loop.InvokeAsync(() =>
    {
        work();
        return true;
    });

    public void Own(IDisposable disposable) => _owned.Add(disposable);

    public async ValueTask DisposeAsync()
    {
        await Pipe.DisposeAsync().ConfigureAwait(false);
        Loop.Stop();
        foreach (var d in _owned)
        {
            d.Dispose();
        }

        await _writer.FlushAsync().ConfigureAwait(false);
        Loop.Dispose();
    }

    private AgentInfo AgentInfo() => new() { Version = Options.Version, Running = true, Paused = _paused, Self = _self };

    private void SetPaused(Func<PausedState, PausedState> change)
    {
        _paused = change(_paused);
        Publisher.MarkChanged();
    }

    private void RegisterCoreHandlers()
    {
        PreparedAction Toggle(string summary, Func<PausedState, PausedState> change) => new()
        {
            Summary = summary,
            Immediate = new ActionResult { Action = "pause", Status = ActionStatus.Ok, Message = summary },
            Done = r =>
            {
                if (r.Status == ActionStatus.Ok)
                {
                    SetPaused(change);
                }
            },
        };

        Router.Register("tracking.pause", _ => Toggle("Tracking paused", p => p with { Tracking = true }));
        Router.Register("tracking.resume", _ => Toggle("Tracking resumed", p => p with { Tracking = false }));
        Router.Register("refits.pause", _ => Toggle("Refits paused", p => p with { Refits = true }));
        Router.Register("refits.resume", _ => Toggle("Refits resumed", p => p with { Refits = false }));
        Router.Register("status", _ => new PreparedAction { Summary = "status", Immediate = new ActionResult { Action = "status", Status = ActionStatus.Ok } });
        Router.Register("quit", _ => new PreparedAction
        {
            Summary = "Quit Tasktor",
            Immediate = new ActionResult { Action = "quit", Status = ActionStatus.Ok, Message = "Quitting" },
            Done = _ => QuitRequested?.Invoke(),
        });
        Router.Register("log.verbose", req => new PreparedAction
        {
            Summary = "Verbose log",
            Immediate = new ActionResult { Action = "log.verbose", Status = ActionStatus.Ok, Message = req.Args?.GetValueOrDefault("on") == "false" ? "Verbose log off" : "Verbose log on for 24 h" },
            Done = _ => Log.SetVerbose(req.Args?.GetValueOrDefault("on") != "false"),
        });
    }
}
