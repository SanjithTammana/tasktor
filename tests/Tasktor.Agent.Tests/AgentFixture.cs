using Tasktor.Agent.Host;
using Tasktor.Cli;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Snapshot;
using Tasktor.Core.Actions;
using Tasktor.Core.Logging;
using Tasktor.Tests.Shared;

namespace Tasktor.Agent.Tests;

internal sealed class StaticCards(params Card[] cards) : ICardProvider
{
    public IReadOnlyList<Card> Cards() => cards;
}

/// <summary>An in-process agent on a private pipe, with a fake process table and a recording process handler.</summary>
internal sealed class AgentFixture : IAsyncDisposable
{
    public AgentFixture()
    {
        Store = new TempStore();
        PipeName = "tasktor-test-" + Guid.NewGuid().ToString("N")[..12];
        Procs[100] = new ProcessRow { Key = new ProcKey(100, 1), Exe = "worker.exe" };
        Procs[200] = new ProcessRow { Key = new ProcKey(200, 2), Exe = "lsass.exe", OwnUser = false };
        Host = new AgentHost(new AgentOptions
        {
            Store = Store.Layout,
            PipeName = PipeName,
            LogWriter = Log,
            ReadProcess = k => Procs.GetValueOrDefault(k.Pid) is { } r && r.Key == k ? r : Procs.GetValueOrDefault(k.Pid),
        });
        foreach (var id in new[] { "priority.set", "proc.end" })
        {
            Host.Router.Register(id, req =>
            {
                var pid = uint.Parse(req.Target!["pid"], System.Globalization.CultureInfo.InvariantCulture);
                if (!Procs.TryGetValue(pid, out var row))
                {
                    return new PreparedAction { Summary = id, Refusal = "No such process" };
                }

                return new PreparedAction
                {
                    Summary = (id == "proc.end" ? "End " : "Lower ") + row.Exe,
                    TargetName = row.Exe,
                    Processes = [row.Key],
                    Run = keys =>
                    {
                        lock (Calls)
                        {
                            Calls.Add(id + ":" + string.Join(",", keys.Select(k => k.Pid)));
                        }

                        return new ActionResult { Action = id, Status = ActionStatus.Ok, Message = "Done: " + row.Exe };
                    },
                };
            });
        }

        Host.Attach(new StaticCards(new Card
        {
            Id = "core.system",
            Source = new CardSource { Kind = CardSourceKind.Core },
            Title = "System",
            Blocks =
            [
                new MetricBlock { Label = "CPU", Value = Value.Binding("/state/system/cpu_pct"), Unit = "%", Priority = 1 },
                new BadgeBlock { Text = Value.Binding("/state/system/verdict"), Priority = 2 },
            ],
            Actions = [new CardAction { Id = "open", Label = "Open", Action = "open" }],
        }));
        Host.Start();
        Host.OnLoop(() => Host.UpdateState(s => s with { System = new SystemState { CpuPct = 34, Verdict = "CPU 34%: worker 2 cores" } })).GetAwaiter().GetResult();
        WaitFor(() => Host.Publisher.Latest?.State.System?.CpuPct == 34);
    }

    public TempStore Store { get; }

    public string PipeName { get; }

    public MemoryLogWriter Log { get; } = new();

    public Dictionary<uint, ProcessRow> Procs { get; } = [];

    public List<string> Calls { get; } = [];

    public AgentHost Host { get; }

    public static void WaitFor(Func<bool> condition, int ms = 5000)
    {
        if (!SpinWait.SpinUntil(condition, ms))
        {
            throw new TimeoutException("condition not met");
        }
    }

    public async Task<(int Code, string Out, string Err)> Cli(params string[] args) => await CliWithInput(null, args).ConfigureAwait(false);

    public async Task<(int Code, string Out, string Err)> CliWithInput(string? stdin, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await CliApp.RunAsync(args, new CliIo { Out = o, Err = e, In = new StringReader(stdin ?? string.Empty), PipeName = PipeName, DataDir = Store.Root }).ConfigureAwait(false);
        return (code, o.ToString(), e.ToString());
    }

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync().ConfigureAwait(false);
        Store.Dispose();
    }
}
