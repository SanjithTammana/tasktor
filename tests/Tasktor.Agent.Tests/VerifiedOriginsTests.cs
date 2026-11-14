using System.IO.Pipes;
using Tasktor.Agent.Host;
using Tasktor.Contracts.Actions;
using Tasktor.Hosts.Common.Pipe;

namespace Tasktor.Agent.Tests;

/// <summary>F-PIPE1: a pipe client's origins follow the exe on the other end of the handle, not the name it gives.</summary>
public sealed class VerifiedOriginsTests
{
    private static readonly string Install = Path.Combine(Path.GetTempPath(), "tasktor-install-" + Guid.NewGuid().ToString("N")[..8]);

    private static IReadOnlySet<Origin> For(string client, string? image) =>
        PipeServer.VerifiedOrigins(Install, _ => image)(client, null!);

    [Fact]
    public void Tasktors_own_viewers_get_their_origins()
    {
        Assert.Contains(Origin.Toast, For("flyout", Path.Combine(Install, "Tasktor.Flyout.exe")));
        Assert.Contains(Origin.Flyout, For("flyout", Path.Combine(Install, "flyout", "TASKTOR.FLYOUT.EXE")));
        Assert.Equal([Origin.Widget], For("widget", Path.Combine(Install, "Tasktor.Widget.exe")));
        Assert.Equal([Origin.Shell], For("shell", Path.Combine(Install, "tasktor-shell.exe")));
    }

    [Fact]
    public void Any_other_program_acts_as_the_cli_whatever_it_calls_itself()
    {
        Assert.Equal([Origin.Cli], For("flyout", @"C:\Users\someone\Downloads\Tasktor.Flyout.exe"));
        Assert.Equal([Origin.Cli], For("flyout", Path.Combine(Install, "Tasktor.Widget.exe"))); // ours, but not the flyout
        Assert.Equal([Origin.Cli], For("shell", null)); // the caller could not be read
        Assert.Equal([Origin.Cli], For("cli", Path.Combine(Install, "Tasktor.Flyout.exe")));
    }

    [Fact]
    public async Task The_caller_is_read_from_the_pipe_handle()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var name = "tasktor-test-caller-" + Guid.NewGuid().ToString("N")[..10];
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.WhenAll(server.WaitForConnectionAsync(cts.Token), client.ConnectAsync(cts.Token));

        Assert.Equal((uint)Environment.ProcessId, PipeCaller.ProcessId(server));
        Assert.True(PipeCaller.Is(server, Environment.ProcessPath!));
        Assert.False(PipeCaller.Is(server, Path.Combine(Install, "Tasktor.Flyout.exe")));
    }
}
