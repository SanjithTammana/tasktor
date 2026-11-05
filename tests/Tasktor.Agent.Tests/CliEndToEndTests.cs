using System.Text.Json;
using System.Text.Json.Nodes;
using Tasktor.Cli;
using Tasktor.Contracts;

namespace Tasktor.Agent.Tests;

public sealed class CliEndToEndTests : IAsyncLifetime
{
    private AgentFixture _agent = null!;

    public Task InitializeAsync()
    {
        _agent = new AgentFixture();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _agent.DisposeAsync();

    [Fact]
    public async Task Status_json_prints_state_and_cards_only_when_asked()
    {
        var (code, output, _) = await _agent.Cli("status", "--json");
        Assert.Equal(ExitCode.Ok, code);
        var snap = JsonNode.Parse(output)!;
        Assert.Equal("tasktor.snapshot/1", snap["schema"]!.GetValue<string>());
        Assert.Equal(34, snap["state"]!["system"]!["cpu_pct"]!.GetValue<double>());
        Assert.Null(snap["cards"]);
        var withCards = JsonNode.Parse((await _agent.Cli("status", "--json", "--cards")).Out)!;
        Assert.Equal("core.system", withCards["cards"]![0]!["id"]!.GetValue<string>());
        Assert.NotNull(JsonSerializer.Deserialize(output, ContractsJsonContext.Default.Snapshot));
    }

    [Fact]
    public async Task Status_text_renders_cards_through_the_text_renderer()
    {
        var (code, output, _) = await _agent.Cli("status");
        Assert.Equal(ExitCode.Ok, code);
        Assert.Contains("== System", output, StringComparison.Ordinal);
        Assert.Contains("CPU: 34%", output, StringComparison.Ordinal);
        Assert.Contains("CPU 34%: worker 2 cores", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Agent_down_exits_3_and_says_so_in_json()
    {
        var o = new StringWriter();
        var code = await CliApp.RunAsync(["status", "--json"], new CliIo { Out = o, Err = new StringWriter(), PipeName = "tasktor-nobody-" + Guid.NewGuid().ToString("N")[..8] });
        Assert.Equal(ExitCode.AgentDown, code);
        Assert.Equal("{\"agent\":{\"running\":false}}", o.ToString().Trim());
    }

    [Fact]
    public async Task Pause_and_resume_tracking_show_in_the_snapshot()
    {
        Assert.Equal(ExitCode.Ok, (await _agent.Cli("pause", "tracking")).Code);
        AgentFixture.WaitFor(() => _agent.Host.Publisher.Latest?.Agent.Paused?.Tracking == true);
        var snap = JsonNode.Parse((await _agent.Cli("status", "--json")).Out)!;
        Assert.True(snap["agent"]!["paused"]!["tracking"]!.GetValue<bool>());
        Assert.Contains("paused: tracking", (await _agent.Cli("status")).Out, StringComparison.Ordinal);
        Assert.Equal(ExitCode.Ok, (await _agent.Cli("resume", "tracking")).Code);
    }

    [Fact]
    public async Task Reversible_proc_action_runs_without_confirmation()
    {
        var (code, output, _) = await _agent.Cli("proc", "lower", "100");
        Assert.Equal(ExitCode.Ok, code);
        Assert.Contains("Done: worker.exe", output, StringComparison.Ordinal);
        Assert.Contains("priority.set:100", _agent.Calls);
    }

    [Fact]
    public async Task Destructive_cli_needs_yes_and_the_setting()
    {
        var off = await _agent.Cli("proc", "end", "100", "--yes");
        Assert.Equal(ExitCode.Refused, off.Code);
        Assert.Contains("cli.allow_destructive", off.Err, StringComparison.Ordinal);

        await _agent.Host.OnLoop(() => _agent.Host.CliAllowDestructive = true);
        var noYes = await _agent.Cli("proc", "end", "100");
        Assert.Equal(ExitCode.NeedsConfirm, noYes.Code);
        Assert.Contains("--yes", noYes.Err, StringComparison.Ordinal);
        Assert.Empty(_agent.Calls);

        Assert.Equal(ExitCode.Ok, (await _agent.Cli("proc", "end", "100", "--yes")).Code);
        Assert.Contains("proc.end:100", _agent.Calls);
    }

    [Fact]
    public async Task Guards_refuse_with_exit_2()
    {
        var r = await _agent.Cli("proc", "lower", "200");
        Assert.Equal(ExitCode.Refused, r.Code);
        Assert.Empty(_agent.Calls);
    }

    [Fact]
    public async Task Json_flag_prints_the_action_result()
    {
        var r = await _agent.Cli("proc", "end", "100", "--json");
        var result = JsonSerializer.Deserialize(r.Out, ContractsJsonContext.Default.ActionResult)!;
        Assert.Equal(Contracts.Actions.ActionStatus.Refused, result.Status);
        Assert.Equal("origin_policy:cli_destructive_off", result.Reason);
    }

    [Fact]
    public async Task Unknown_command_and_missing_arguments_exit_1()
    {
        Assert.Equal(ExitCode.Error, (await _agent.Cli("frobnicate")).Code);
        Assert.Equal(ExitCode.Error, (await _agent.Cli("proc", "lower")).Code);
        Assert.Equal(ExitCode.Error, (await _agent.Cli("pause", "everything")).Code);
        Assert.Equal(ExitCode.Ok, (await _agent.Cli("help")).Code);
    }

    [Fact]
    public async Task Job_set_writes_the_progress_file_without_the_agent()
    {
        var o = new StringWriter();
        var io = new CliIo { Out = o, Err = new StringWriter(), DataDir = _agent.Store.Root, PipeName = "nobody" };
        Assert.Equal(ExitCode.Ok, await CliApp.RunAsync(["job", "set", "embed_1", "--pct", "42", "--stage", "embedding", "--heartbeat", "30"], io));
        Assert.Equal(ExitCode.Ok, await CliApp.RunAsync(["job", "set", "embed_1", "--msg", "batch 3/7"], io));
        var file = JsonSerializer.Deserialize(File.ReadAllText(_agent.Store.Layout.JobFile("embed_1")), ContractsJsonContext.Default.ProgressFile)!;
        Assert.Equal(42, file.Pct);
        Assert.Equal("embedding", file.Stage);
        Assert.Equal("batch 3/7", file.Msg);
        Assert.Equal(30, file.HeartbeatS);
        Assert.Equal(ExitCode.Ok, await CliApp.RunAsync(["job", "done", "embed_1"], io));
        Assert.Equal(Contracts.Jobs.ProgressState.Done, JsonSerializer.Deserialize(File.ReadAllText(_agent.Store.Layout.JobFile("embed_1")), ContractsJsonContext.Default.ProgressFile)!.State);
        Assert.Equal(ExitCode.Error, await CliApp.RunAsync(["job", "set", "../evil", "--pct", "1"], io));
        Assert.Equal(ExitCode.Ok, await CliApp.RunAsync(["job", "rm", "embed_1"], io));
        Assert.False(File.Exists(_agent.Store.Layout.JobFile("embed_1")));
    }

    [Fact]
    public async Task Agent_hook_keeps_only_session_folder_and_state()
    {
        const string hook = """{"session_id":"s-1","cwd":"/home/someone/projects/my-repo","hook_event_name":"UserPromptSubmit","prompt":"secret plans","transcript_path":"/x/y.jsonl"}""";
        var (code, _, _) = await _agent.CliWithInput(hook, "agent-hook", "working", "--agent", "claude");
        Assert.Equal(ExitCode.Ok, code);
        var text = File.ReadAllText(_agent.Store.Layout.AgentFile("s-1"));
        var file = JsonSerializer.Deserialize(text, ContractsJsonContext.Default.AgentHookFile)!;
        Assert.Equal("my-repo", file.Folder);
        Assert.Equal(Contracts.Snapshot.AgentSessionState.Working, file.State);
        Assert.DoesNotContain("secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("someone", text, StringComparison.Ordinal);
        Assert.DoesNotContain("transcript", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_action_is_logged_with_origin_and_without_arguments()
    {
        await _agent.Cli("proc", "lower", "100");
        AgentFixture.WaitFor(() => _agent.Log.Lines.Any(l => l.Contains("action=priority.set", StringComparison.Ordinal)));
        var line = _agent.Log.Lines.First(l => l.Contains("action=priority.set", StringComparison.Ordinal));
        Assert.Contains("target=worker.exe origin=cli result=ok", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Snapshot_file_is_written_atomically()
    {
        AgentFixture.WaitFor(() => File.Exists(_agent.Store.Layout.Snapshot));
        AgentFixture.WaitFor(() =>
        {
            try
            {
                return JsonSerializer.Deserialize(File.ReadAllText(_agent.Store.Layout.Snapshot), ContractsJsonContext.Default.Snapshot)?.State.System?.CpuPct == 34;
            }
            catch (IOException)
            {
                return false;
            }
        });
        Assert.False(File.Exists(_agent.Store.Layout.Snapshot + ".tmp"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Export_data_writes_a_manifest_and_says_plain_text()
    {
        var to = Path.Combine(_agent.Store.Root, "out");
        var (code, output, err) = await _agent.Cli("export", "data", "--to", to);
        Assert.Equal(ExitCode.Ok, code);
        Assert.Contains("plain text", err + output, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(to, "manifest.json")));
        Assert.Equal(ExitCode.NeedsConfirm, (await _agent.Cli("export", "data", "--to", to + "2", "--include-raw")).Code);
        Assert.Equal(ExitCode.Ok, (await _agent.Cli("export", "data", "--to", to + "3", "--include-raw", "--yes")).Code);
    }

    [Fact]
    public async Task Forget_is_destructive_on_the_cli()
    {
        Directory.CreateDirectory(_agent.Store.Layout.StateDir);
        File.WriteAllText(_agent.Store.Layout.JobKinds, "{}");
        Assert.Equal(ExitCode.Refused, (await _agent.Cli("forget", "learned", "--yes")).Code);
        await _agent.Host.OnLoop(() => _agent.Host.CliAllowDestructive = true);
        Assert.Equal(ExitCode.NeedsConfirm, (await _agent.Cli("forget", "learned")).Code);
        Assert.True(File.Exists(_agent.Store.Layout.JobKinds));
        var r = await _agent.Cli("forget", "learned", "--yes");
        Assert.Equal(ExitCode.Ok, r.Code);
        Assert.False(File.Exists(_agent.Store.Layout.JobKinds));
    }

    [Fact]
    public async Task Settings_round_trip_through_a_zip()
    {
        File.WriteAllText(_agent.Store.Layout.Config, """{ "schema_version": 1, "update_check": true }""");
        var zip = Path.Combine(_agent.Store.Root, "settings.zip");
        Assert.Equal(ExitCode.Ok, (await _agent.Cli("settings", "export", zip)).Code);
        Assert.True(File.Exists(zip));
        await _agent.Host.OnLoop(() => _agent.Host.CliAllowDestructive = true);
        Assert.Equal(ExitCode.Ok, (await _agent.Cli("settings", "import", zip, "--yes")).Code);
    }
}
