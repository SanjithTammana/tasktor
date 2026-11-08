using System.Text;
using Tasktor.Capture.Browser;
using Tasktor.Capture.Deep;
using Tasktor.Capture.Host;
using Tasktor.Capture.Recording;
using Tasktor.Capture.Security;
using Tasktor.Contracts.Capture;
using Tasktor.Contracts.Platform;

namespace Tasktor.Capture.Tests;

/// <summary>C22.4: commands run by a known agent's children arrive from the agent and are recorded as Agent(app), redacted.</summary>
public sealed class AgentCommandTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tasktor-agentcmd-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly CaptureBlocks _blocks;
    private readonly RawVault _vault;
    private readonly CaptureSession _session;

    public AgentCommandTests()
    {
        Directory.CreateDirectory(_dir);
        _blocks = new CaptureBlocks(Path.Combine(_dir, "blocks.json"), TimeZoneInfo.Utc);
        _vault = new RawVault(Path.Combine(_dir, "raw"), new byte[32]);
        var clock = new Clock();
        _session = new CaptureSession(new CaptureRecorder(_vault, _blocks, () => clock.UtcNow), new BrowserEndpoint(_blocks, () => clock.UtcNow), clock, new Probe(), () => SessionAvailability.UnlockedConnected);
    }

    public void Dispose()
    {
        _session.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => T0;
    }

    private sealed class Probe : IDesktopProbe
    {
        public DesktopObservation Read() => DesktopObservation.UserDesktop;
    }

    private List<string> Commands() =>
        [.. _vault.Read(T0.AddDays(-1), T0.AddDays(1)).Where(r => r.Kind == RecordKind.Command).Select(r => Encoding.UTF8.GetString(r.Body))];

    [Fact]
    public void An_agent_command_is_recorded_as_the_agent_and_redacted()
    {
        var control = new CaptureControl(_blocks, () => T0) { AgentCommand = _session.PostAgentCommand };
        _session.Tick();
        var status = control.Handle(new CaptureCommand { Verb = CaptureVerb.AgentCommand, Agent = "claude", App = "claude.exe", Exe = "node.exe", CommandLine = "node deploy.js --key=ghp_" + new string('a', 36) });
        Assert.Null(status.Refusal);
        _session.Loop.RunPending();
        var only = Assert.Single(Commands());
        Assert.Contains("node deploy.js", only, StringComparison.Ordinal);
        Assert.Contains("redacted", only, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_aaaa", only, StringComparison.Ordinal);
        Assert.Contains("agent", only, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("claude", only, StringComparison.Ordinal);
    }

    [Fact]
    public void A_blocked_agent_or_a_malformed_or_personal_request_records_nothing()
    {
        var control = new CaptureControl(_blocks, () => T0) { AgentCommand = _session.PostAgentCommand };
        _session.Tick();
        Assert.Equal("invalid", control.Handle(new CaptureCommand { Verb = CaptureVerb.AgentCommand, ByUser = true, Agent = "claude", App = "claude.exe", Exe = "git.exe", CommandLine = "git status" }).Refusal);
        Assert.Equal("invalid", control.Handle(new CaptureCommand { Verb = CaptureVerb.AgentCommand, Agent = "claude", App = "claude.exe", Exe = "git.exe" }).Refusal);
        Assert.Equal("invalid", control.Handle(new CaptureCommand { Verb = CaptureVerb.AgentCommand, Agent = "claude", App = "claude.exe", Exe = "git.exe", CommandLine = new string('x', 5000) }).Refusal);
        _blocks.Add(new CaptureBlock { Id = "", Kind = BlockKind.App, Value = "claude.exe", AddedBy = "user" });
        control.Handle(new CaptureCommand { Verb = CaptureVerb.AgentCommand, Agent = "claude", App = "claude.exe", Exe = "git.exe", CommandLine = "git status" });
        _session.Loop.RunPending();
        Assert.Empty(Commands());
    }
}
