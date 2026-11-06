using Tasktor.Contracts;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Pipe;
using Tasktor.Contracts.Snapshot;
using Tasktor.Hosts.Common.Pipe;

namespace Tasktor.Agent.Tests;

public sealed class PipeProtocolTests : IAsyncLifetime
{
    private AgentFixture _agent = null!;

    public Task InitializeAsync()
    {
        _agent = new AgentFixture();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _agent.DisposeAsync();

    [Fact]
    public async Task Hello_welcome_names_the_contracts()
    {
        await using var c = await PipeClient.ConnectAsync("flyout", _agent.PipeName, 10000);
        Assert.NotNull(c.Welcome);
        Assert.Contains(ContractIds.Snapshot, c.Welcome!.Contracts);
    }

    [Fact]
    public async Task Unknown_major_gets_an_error_not_a_guess()
    {
        await using var raw = await RawClient.Open(_agent.PipeName);
        await raw.Send(new HelloMessage { Id = "1", Client = "x", Contracts = ["tasktor.snapshot/2"] });
        var reply = await raw.Receive();
        Assert.Equal("unsupported_contract", Assert.IsType<ErrorMessage>(reply).Code);
    }

    [Fact]
    public async Task Requests_before_hello_are_refused_and_garbage_does_not_kill_the_session()
    {
        await using var raw = await RawClient.Open(_agent.PipeName);
        await raw.Send(new PullMessage { Id = "1", Table = "snapshot" });
        Assert.Equal("hello_first", Assert.IsType<ErrorMessage>(await raw.Receive()).Code);
        await raw.SendLine("{not json");
        Assert.Equal("bad_message", Assert.IsType<ErrorMessage>(await raw.Receive()).Code);
        await raw.Send(new HelloMessage { Id = "2", Client = "cli", Contracts = [ContractIds.Snapshot] });
        Assert.IsType<WelcomeMessage>(await raw.Receive());
    }

    [Fact]
    public async Task Subscribe_sends_the_view_then_coalesced_deltas()
    {
        await using var c = await PipeClient.ConnectAsync("flyout", _agent.PipeName, 10000);
        var deltas = new List<DeltaMessage>();
        c.Pushed += m =>
        {
            if (m is DeltaMessage d)
            {
                lock (deltas)
                {
                    deltas.Add(d);
                }
            }
        };
        var first = Assert.IsType<DeltaMessage>(await c.RequestAsync(new SubscribeMessage { Paths = ["/state/system"] }));
        Assert.Equal(34, first.Ops[0].Value!["state"]!["system"]!["cpu_pct"]!.GetValue<double>());

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 30; i++)
        {
            var v = 40 + i;
            await _agent.Host.OnLoop(() => _agent.Host.UpdateState(s => s with { System = s.System! with { CpuPct = v } }));
            await Task.Delay(50);
        }

        AgentFixture.WaitFor(() => { lock (deltas) { return deltas.Any(d => d.Ops.Any(o => o.Value?.GetValue<double>() == 69)); } });
        var elapsed = sw.Elapsed.TotalSeconds;
        lock (deltas)
        {
            // ≤ 1 per 500 ms per client (+1 for timing slack), and only the subscribed path.
            Assert.InRange(deltas.Count, 1, (int)(elapsed / 0.5) + 2);
            Assert.All(deltas.SelectMany(d => d.Ops), o => Assert.StartsWith("/state/system", o.Path, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Leases_die_with_the_connection()
    {
        var runs = 0;
        await _agent.Host.OnLoop(() => _agent.Host.Scheduler.Register("procs", _ => runs++));
        var c = await PipeClient.ConnectAsync("flyout", _agent.PipeName, 10000);
        await c.RequestAsync(new LeaseMessage { Source = "procs", IntervalMs = 1000, Columns = ["Usage"] });
        Assert.Equal(1, await _agent.Host.OnLoop(() => _agent.Host.Scheduler.LeaseCount("procs")));
        await c.DisposeAsync();
        AgentFixture.WaitFor(() => _agent.Host.OnLoop(() => _agent.Host.Scheduler.LeaseCount("procs")).Result == 0);
        await using var c2 = await PipeClient.ConnectAsync("flyout", _agent.PipeName, 10000);
        var err = Assert.IsType<ErrorMessage>(await c2.RequestAsync(new LeaseMessage { Source = "nope", IntervalMs = 1000 }));
        Assert.Equal("unknown_source", err.Code);
    }

    [Fact]
    public async Task A_cli_client_cannot_speak_as_the_flyout()
    {
        await using var c = await PipeClient.ConnectAsync("cli", _agent.PipeName, 10000);
        var r = await c.ActAsync(new ActionRequest { Action = "proc.end", Origin = Origin.Flyout, Target = new Dictionary<string, string> { ["pid"] = "100" } });
        Assert.Equal(ActionStatus.Refused, r.Status); // treated as CLI: destructive off
        Assert.Empty(_agent.Calls);
    }

    [Fact]
    public async Task Flyout_two_step_confirm_over_the_pipe()
    {
        await using var c = await PipeClient.ConnectAsync("flyout", _agent.PipeName, 10000);
        var req = new ActionRequest { Action = "proc.end", Origin = Origin.Flyout, Target = new Dictionary<string, string> { ["pid"] = "100" } };
        var first = await c.ActAsync(req);
        Assert.Equal(ActionStatus.NeedsConfirm, first.Status);
        Assert.Equal("End worker.exe", first.Summary);
        var second = await c.ActAsync(req with { Confirm = first.Token });
        Assert.Equal(ActionStatus.Ok, second.Status);
        Assert.Contains("proc.end:100", _agent.Calls);
    }

    [Fact]
    public async Task Unknown_pull_table_is_an_error()
    {
        await using var c = await PipeClient.ConnectAsync("flyout", _agent.PipeName, 10000);
        Assert.Equal("unknown_table", Assert.IsType<ErrorMessage>(await c.RequestAsync(new PullMessage { Table = "secrets" })).Code);
        var catalog = await c.PullAsync("catalog");
        Assert.Contains("proc.end", catalog!.Data!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task Data_pull_scans_off_the_loop_and_answers_with_the_forget_groups()
    {
        Directory.CreateDirectory(_agent.Store.Layout.TrackDir);
        File.WriteAllBytes(_agent.Store.Layout.EventsDb, new byte[1234]);
        await using var c = await PipeClient.ConnectAsync("flyout", _agent.PipeName, 10000);
        await c.PullAsync("data"); // the first pull starts a scan; it may answer before the scan ends
        AgentFixture.WaitFor(() => _agent.Host.OnLoop(() => _agent.Host.DataReport).GetAwaiter().GetResult() is not null);
        var data = (await c.PullAsync("data"))!.Data!;
        Assert.Equal(["learned", "tracking", "capture", "all"], data["groups"]!.AsArray().Select(g => g!["scope"]!.GetValue<string>()));
        Assert.True(data["groups"]![1]!["bytes"]!.GetValue<long>() >= 1234);
        Assert.DoesNotContain("events.db", data.ToJsonString(), StringComparison.Ordinal); // sizes and dates only, no names
    }

    [Fact]
    public async Task An_undoable_action_appears_in_recent_actions_with_its_undo()
    {
        await using var c = await PipeClient.ConnectAsync("flyout", _agent.PipeName, 10000);
        var r = await c.ActAsync(new ActionRequest { Action = "priority.set", Origin = Origin.Flyout, Target = new Dictionary<string, string> { ["pid"] = "100" } });
        Assert.Equal(ActionStatus.Ok, r.Status);
        AgentFixture.WaitFor(() => _agent.Host.OnLoop(() => _agent.Host.State.Current.RecentActions).GetAwaiter().GetResult() is { Count: > 0 });
        var recent = _agent.Host.OnLoop(() => _agent.Host.State.Current.RecentActions!).GetAwaiter().GetResult();
        Assert.Equal(("Lower worker.exe", "priority.restore", "flyout"), (recent[0].Text, recent[0].Undo, recent[0].Origin));
    }

    private static ActionRequest Module(string action, string id, Dictionary<string, string>? args = null) =>
        new() { Action = action, Origin = Origin.Flyout, Target = new Dictionary<string, string> { ["module"] = id }, Args = args };

    [Fact]
    public async Task Turning_a_Module_on_records_consent_and_applies_at_the_next_start()
    {
        await using var c = await PipeClient.ConnectAsync("flyout", _agent.PipeName, 10000);
        var on = await c.ActAsync(Module("module.enable", "focus"));
        Assert.Equal(ActionStatus.Ok, on.Status);
        Assert.Equal("Turned on Focus. Restart Tasktor to apply it.", on.Message);
        AgentFixture.WaitFor(() => _agent.Host.OnLoop(() => _agent.Host.Settings.ModuleOn("focus") && _agent.Host.Consents.HasModule("focus")).GetAwaiter().GetResult());

        var states = (await c.PullAsync("modules"))!.Data!["modules"]!.AsArray();
        var focus = states.Single(m => m!["id"]!.GetValue<string>() == "focus")!;
        Assert.Equal((true, false, true), (focus["on"]!.GetValue<bool>(), focus["running"]!.GetValue<bool>(), focus["consented"]!.GetValue<bool>()));
        Assert.Equal(Contracts.Config.ModuleCatalog.All.Count, states.Count);

        Assert.Equal("Turned off Focus.", (await c.ActAsync(Module("module.disable", "focus"))).Message);
        AgentFixture.WaitFor(() => !_agent.Host.OnLoop(() => _agent.Host.Settings.ModuleOn("focus")).GetAwaiter().GetResult());
    }

    [Fact]
    public async Task Modules_refuse_unknown_ids_rules_and_capture_without_its_own_screen()
    {
        await using var c = await PipeClient.ConnectAsync("flyout", _agent.PipeName, 10000);
        Assert.Equal(ActionStatus.Refused, (await c.ActAsync(Module("module.enable", "nope"))).Status);
        var capture = await c.ActAsync(Module("module.enable", "capture"));
        Assert.Equal(ActionStatus.Refused, capture.Status);
        Assert.False(_agent.Host.OnLoop(() => _agent.Host.Consents.HasModule("capture")).GetAwaiter().GetResult());
        Assert.Equal(ActionStatus.Refused, (await c.ActAsync(Module("module.enable", "capture", new() { ["screen"] = "capture" }))).Status); // not acknowledged
        Assert.Equal(ActionStatus.Ok, (await c.ActAsync(Module("module.enable", "capture", new() { ["screen"] = "capture", ["acknowledged"] = "true" }))).Status);
    }

    private sealed class RawClient : IAsyncDisposable
    {
        private readonly System.IO.Pipes.NamedPipeClientStream _s;
        private readonly PipeFraming.LineReader _r;

        private RawClient(System.IO.Pipes.NamedPipeClientStream s)
        {
            _s = s;
            _r = new PipeFraming.LineReader(s);
        }

        public static async Task<RawClient> Open(string name)
        {
            var s = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
            await s.ConnectAsync(2000);
            return new RawClient(s);
        }

        public Task Send(PipeMessage m) => PipeFraming.WriteAsync(_s, PipeFraming.Encode(m), CancellationToken.None);

        public Task SendLine(string line) => PipeFraming.WriteAsync(_s, line, CancellationToken.None);

        public async Task<PipeMessage?> Receive()
        {
            using var cts = new CancellationTokenSource(5000);
            return PipeFraming.Decode((await _r.ReadLineAsync(cts.Token))!);
        }

        public ValueTask DisposeAsync() => _s.DisposeAsync();
    }

}

public sealed class PersistenceTests
{
    [Fact]
    public async Task Pause_survives_an_agent_restart()
    {
        using var store = new Tasktor.Tests.Shared.TempStore();
        await using (var a = new Host.AgentHost(new Host.AgentOptions { Store = store.Layout, PipeName = "tasktor-p-" + Guid.NewGuid().ToString("N")[..8], LogWriter = new Core.Logging.MemoryLogWriter() }))
        {
            a.Start();
            var r = await a.OnLoop(() => a.Router.Submit(new ActionRequest { Action = "tracking.pause", Origin = Origin.Tray }));
            await r;
            AgentFixture.WaitFor(() => a.Paused.Tracking);
        }

        await using var b = new Host.AgentHost(new Host.AgentOptions { Store = store.Layout, PipeName = "tasktor-p-" + Guid.NewGuid().ToString("N")[..8], LogWriter = new Core.Logging.MemoryLogWriter() });
        Assert.True(b.Paused.Tracking);
    }
}
