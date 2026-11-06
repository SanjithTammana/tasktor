using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Tasktor.Capture.Browser;
using Tasktor.Capture.Deep;
using Tasktor.Contracts;
using Tasktor.Contracts.Capture;
using Tasktor.Hosts.Common.Pipe;

namespace Tasktor.Capture.Tests;

/// <summary>M12 capture server: the tasktor.browser/1 contract, secure-until-focus, blocks, the offline queue, the control link.</summary>
public sealed class BrowserEndpointTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tasktor-cap-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = T0;

    public BrowserEndpointTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private (CaptureBlocks Blocks, BrowserEndpoint Endpoint, CaptureControl Control) World()
    {
        var blocks = new CaptureBlocks(Path.Combine(_dir, "blocks.json"));
        var ep = new BrowserEndpoint(blocks, () => _now);
        var ctl = new CaptureControl(blocks, () => _now) { ResolveTab = ep.ResolveTab, ForgetSince = since => (long)(_now - since).TotalMinutes };
        ctl.Changed += ep.NotifyBlocksChanged;
        return (blocks, ep, ctl);
    }

    private static BrowserHello Hello(string browser = "chrome", int contract = 1) => new() { At = T0, Contract = contract, Client = "nmh", Browser = browser, ExtensionVersion = "1.0.0" };

    private static BrowserTab Tab(int id, string? host, bool active = true) => new() { At = T0, Tab = id, Window = 1, Host = host, Active = active, Audible = false };

    [Fact]
    public void Hello_checks_the_contract_and_nothing_counts_before_it()
    {
        var (_, ep, _) = World();
        Assert.Empty(ep.Handle("s1", Tab(1, "docs.example.org")));
        Assert.Equal(1, ep.Rejected);
        var refused = Assert.IsType<BrowserWelcome>(Assert.Single(ep.Handle("s1", Hello(contract: 2))));
        Assert.True(refused.Blocks.Paused);
        Assert.Null(ep.BrowserOf("s1"));
        var ok = Assert.IsType<BrowserWelcome>(Assert.Single(ep.Handle("s1", Hello())));
        Assert.False(ok.Blocks.Paused);
        Assert.Equal("chrome", ep.BrowserOf("s1"));
    }

    [Fact]
    public void A_tab_is_secure_until_an_explicit_focus_and_again_after_navigation_or_reconnect()
    {
        var (_, ep, _) = World();
        ep.Handle("s1", Hello());
        ep.Handle("s1", Tab(7, "docs.example.org"));
        Assert.True(ep.Context("chrome", 7)!.Secure);
        ep.Handle("s1", new BrowserFocus { At = T0, Tab = 7, Field = "text" });
        Assert.False(ep.Context("chrome", 7)!.Secure);
        ep.Handle("s1", Tab(7, "docs.example.org"));
        Assert.False(ep.Context("chrome", 7)!.Secure); // same host: an update, not a navigation
        ep.Handle("s1", Tab(7, "bank.example.com"));
        Assert.True(ep.Context("chrome", 7)!.Secure);
        ep.Handle("s1", new BrowserFocus { At = T0, Tab = 7, Field = "none" });
        ep.Handle("s2", Hello());
        Assert.True(ep.Context("chrome", 7)!.Secure); // a reconnect of that browser
        Assert.Equal("<redacted>", ep.Context("chrome", 7)!.Host.ToString());
        Assert.Null(ep.Context("edge", 7)); // browsers are separate
    }

    [Fact]
    public void Popup_block_requests_add_and_remove_and_every_change_reaches_the_state()
    {
        var (blocks, ep, ctl) = World();
        var pushes = 0;
        ep.BlocksChanged += () => pushes++;
        ep.Handle("s1", Hello());
        ep.Handle("s1", Tab(3, "news.example.com"));
        var ack = Assert.IsType<BrowserAck>(Assert.Single(ep.Handle("s1", new BrowserBlockRequest { At = T0, RequestId = "r1", Op = "add", Kind = "site", Host = "news.example.com" })));
        Assert.True(ack.Ok);
        Assert.Equal(["news.example.com"], ep.StateFor("chrome").ExcludedHosts);
        Assert.False(Assert.IsType<BrowserAck>(ep.Handle("s1", new BrowserBlockRequest { At = T0, RequestId = "r2", Op = "add", Kind = "tab", Tab = 99 })[0]).Ok); // unknown tab
        Assert.True(Assert.IsType<BrowserAck>(ep.Handle("s1", new BrowserBlockRequest { At = T0, RequestId = "r3", Op = "add", Kind = "tab", Tab = 3, Minutes = 30 })[0]).Ok);
        Assert.Equal([3], ep.StateFor("chrome").PausedTabs);
        Assert.Empty(ep.StateFor("edge").PausedTabs);
        _now = T0.AddMinutes(31);
        Assert.Empty(ep.StateFor("chrome").PausedTabs); // the timed tab pause expired
        Assert.Equal("invalid", Assert.IsType<BrowserAck>(ep.Handle("s1", new BrowserBlockRequest { At = T0, RequestId = "r4", Op = "drop", Kind = "site", Host = "x.example" })[0]).Reason);
        Assert.True(Assert.IsType<BrowserAck>(ep.Handle("s1", new BrowserBlockRequest { At = T0, RequestId = "r5", Op = "remove", Kind = "site", Host = "news.example.com" })[0]).Ok);
        Assert.Empty(ep.StateFor("chrome").ExcludedHosts);

        ctl.Handle(new CaptureCommand { Verb = CaptureVerb.Pause, ByUser = false, Until = _now.AddMinutes(15) }); // a rule pauses
        var state = ep.StateFor("chrome");
        Assert.True(state.Paused);
        Assert.Equal(_now.AddMinutes(15), state.PausedUntil);
        ctl.Handle(new CaptureCommand { Verb = CaptureVerb.Exclude, ByUser = true, App = "chrome.exe" });
        Assert.True(ep.StateFor("chrome").Paused); // the browser app itself excluded
        Assert.False(ep.StateFor("edge").Paused && ep.StateFor("edge").PausedUntil is null);
        Assert.True(pushes >= 4);
        Assert.Contains(blocks.Blocks, b => b.AddedBy == "rule");
    }

    [Fact]
    public void Control_lets_rules_add_but_never_lift_and_forget_is_people_only()
    {
        var (_, ep, ctl) = World();
        ep.Handle("s1", Hello("edge"));
        ep.Handle("s1", Tab(5, "mail.example.com"));
        ep.Handle("s1", new BrowserVisibility { At = T0, Tab = 5, Visible = true });
        var paused = ctl.Handle(new CaptureCommand { Verb = CaptureVerb.Pause, ByUser = false });
        Assert.True(paused.PausedIndefinitely);
        Assert.True(Assert.Single(paused.Blocks).ByRule);
        Assert.Equal("rule_owned", ctl.Handle(new CaptureCommand { Verb = CaptureVerb.Resume, ByUser = false }).Refusal);
        Assert.Null(ctl.Handle(new CaptureCommand { Verb = CaptureVerb.Resume, ByUser = true }).Refusal);
        Assert.False(ctl.Status().PausedIndefinitely);
        var tab = ctl.Handle(new CaptureCommand { Verb = CaptureVerb.TabPause, ByUser = true }); // the active visible tab
        Assert.Null(tab.Refusal);
        Assert.Equal([5], ep.StateFor("edge").PausedTabs);
        Assert.Equal("unknown_tab", ctl.Handle(new CaptureCommand { Verb = CaptureVerb.TabPause, ByUser = true, Tab = 404 }).Refusal);
        Assert.Equal("invalid", ctl.Handle(new CaptureCommand { Verb = CaptureVerb.Forget, ByUser = false, Since = T0.AddHours(-1) }).Refusal);
        Assert.Equal(60, ctl.Handle(new CaptureCommand { Verb = CaptureVerb.Forget, ByUser = true, Since = T0.AddHours(-1) }).Forgotten);
    }

    [Fact]
    public async Task The_pipe_speaks_the_contract_end_to_end_and_pushes_block_changes()
    {
        var (_, ep, ctl) = World();
        var name = "tasktor-test-cap-" + Guid.NewGuid().ToString("N")[..10];
        await using var server = new CapturePipeServer(ep, name);
        await using var control = new ControlPipeServer(ctl, name + "-ctl");
        server.Start();
        control.Start();
        await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await pipe.ConnectAsync(cts.Token);
        var reader = new PipeFraming.LineReader(pipe);
        async Task Send(BrowserMessage m) => await PipeFraming.WriteAsync(pipe, JsonSerializer.Serialize(m, ContractsJsonContext.Default.BrowserMessage), cts.Token);
        async Task<BrowserMessage> Next() => JsonSerializer.Deserialize(await reader.ReadLineAsync(cts.Token) ?? throw new IOException("closed"), ContractsJsonContext.Default.BrowserMessage)!;

        await Send(Hello());
        Assert.IsType<BrowserWelcome>(await Next());
        await pipe.WriteAsync(Encoding.UTF8.GetBytes(new string('x', CappedLineReader.MaxLineBytes + 10) + "\n"), cts.Token); // oversized: dropped
        await pipe.WriteAsync("{not json}\n"u8.ToArray(), cts.Token);
        await Send(Tab(2, "docs.example.org"));
        await Send(new BrowserBlockRequest { At = T0, RequestId = "q1", Op = "add", Kind = "site", Host = "docs.example.org" });
        var got = new List<BrowserMessage> { await Next(), await Next() };
        Assert.Contains(got, m => m is BrowserAck { RequestId: "q1", Ok: true });
        Assert.Contains(got, m => m is BrowserBlocks b && b.State.ExcludedHosts.Contains("docs.example.org"));
        Assert.Equal(1, server.DroppedLines);
        Assert.Equal(1, server.InvalidLines);

        // The agent's card pauses through the control pipe; the browser session gets the push.
        var status = await CaptureLink.SendAsync(new CaptureCommand { Verb = CaptureVerb.Pause, ByUser = true, Until = _now.AddHours(1) }, cts.Token, name + "-ctl", 5000);
        Assert.Equal(_now.AddHours(1), status.PausedUntil);
        Assert.True(Assert.IsType<BrowserBlocks>(await Next()).State.Paused);
    }

    [Fact]
    public async Task Disposing_the_server_waits_for_an_inflight_context_callback()
    {
        var (_, ep, _) = World();
        var name = "tasktor-test-cap-" + Guid.NewGuid().ToString("N")[..10];
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        ep.ContextChanged += (_, _, _) => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); };
        var server = new CapturePipeServer(ep, name);
        server.Start();
        await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await pipe.ConnectAsync(cts.Token);
            var reader = new PipeFraming.LineReader(pipe);
            await PipeFraming.WriteAsync(pipe, JsonSerializer.Serialize<BrowserMessage>(Hello(), ContractsJsonContext.Default.BrowserMessage), cts.Token);
            Assert.NotNull(await reader.ReadLineAsync(cts.Token));
            await PipeFraming.WriteAsync(pipe, JsonSerializer.Serialize<BrowserMessage>(Tab(2, "example.org"), ContractsJsonContext.Default.BrowserMessage), cts.Token);
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))));
            var stopping = server.DisposeAsync().AsTask();
            await Task.Delay(200, cts.Token);
            Assert.False(stopping.IsCompleted);
            release.Set();
            await stopping.WaitAsync(cts.Token);
        }
        finally
        {
            release.Set();
        }
    }
    [Fact]
    public async Task A_refused_hello_is_answered_then_the_connection_closes()
    {
        var (_, ep, _) = World();
        var name = "tasktor-test-cap-" + Guid.NewGuid().ToString("N")[..10];
        await using var server = new CapturePipeServer(ep, name);
        server.Start();
        await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await pipe.ConnectAsync(cts.Token);
        await PipeFraming.WriteAsync(pipe, JsonSerializer.Serialize<BrowserMessage>(Hello(contract: 9), ContractsJsonContext.Default.BrowserMessage), cts.Token);
        var reader = new PipeFraming.LineReader(pipe);
        Assert.IsType<BrowserWelcome>(JsonSerializer.Deserialize(await reader.ReadLineAsync(cts.Token) ?? "", ContractsJsonContext.Default.BrowserMessage));
        Assert.Null(await reader.ReadLineAsync(cts.Token));
    }

    [Fact]
    public void The_queue_drains_context_only_under_the_writers_mutex()
    {
        var path = Path.Combine(_dir, "nmh-queue.jsonl");
        var lines = new[]
        {
            JsonSerializer.Serialize<BrowserMessage>(Tab(1, "docs.example.org"), ContractsJsonContext.Default.BrowserMessage),
            JsonSerializer.Serialize<BrowserMessage>(new BrowserFocus { At = T0, Tab = 1, Field = "secure" }, ContractsJsonContext.Default.BrowserMessage),
            JsonSerializer.Serialize<BrowserMessage>(new BrowserBlockRequest { At = T0, RequestId = "x", Op = "add", Kind = "site", Host = "a.example" }, ContractsJsonContext.Default.BrowserMessage),
            "garbage",
        };
        File.WriteAllLines(path, lines);
        Assert.StartsWith("tasktor-nmh-queue-", QueueDrain.MutexName(path), StringComparison.Ordinal);
        Assert.Equal(QueueDrain.MutexName(path).ToUpperInvariant().Replace("TASKTOR-NMH-QUEUE-", "tasktor-nmh-queue-", StringComparison.Ordinal), QueueDrain.MutexName(path));

        // While a writer holds the mutex, the drain waits; then it reads everything the writer appended.
        using var held = new Mutex(true, QueueDrain.MutexName(path));
        var drain = Task.Run(() => QueueDrain.Drain(path));
        Thread.Sleep(200);
        Assert.False(drain.IsCompleted);
        File.AppendAllText(path, JsonSerializer.Serialize<BrowserMessage>(new BrowserTabClosed { At = T0, Tab = 1 }, ContractsJsonContext.Default.BrowserMessage) + "\n");
        held.ReleaseMutex();
        var (messages, invalid) = drain.GetAwaiter().GetResult();
        Assert.Equal(3, messages.Count); // tab, focus, tab_closed; the block request is never accepted from the queue
        Assert.Equal(2, invalid);
        Assert.False(File.Exists(path));
    }
}

