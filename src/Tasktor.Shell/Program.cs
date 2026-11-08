using System.Text.Json;
using Tasktor.Contracts;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Windows;
using Tasktor.Hosts.Common.Pipe;

namespace Tasktor.Shell;

/// <summary>
/// <c>tasktor-shell</c> (U3 D-W2): the warm window process for the switcher (F2). Started by the agent only while the
/// switcher Module is on; a pipe client like the flyout (origin <c>shell</c>). No network code (G-capture-net covers it),
/// no hook unless "Replace Alt+Tab" is on (not built yet), nothing stored.
/// </summary>
internal static class Program
{
    private static readonly TimeSpan PullTimeout = TimeSpan.FromMilliseconds(250);

    private static PipeClient? s_client;

    [STAThread]
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            return 1;
        }

        var a = ShellArgs.Parse(args);
        var pipe = a.Pipe;

        using var single = new Mutex(true, "Local\\tasktor-shell-" + (pipe ?? PipeName.ForCurrentUser()), out var first);
        if (!first)
        {
            return 0;
        }

        using var window = new SwitcherWindow(() => Feed(pipe), jobId => Resume(pipe, jobId), a.Includes);
        return window.Run();
    }

    /// <summary>The agent's switcher feed, or null when it does not answer in time (the switcher still opens, in z order).</summary>
    private static SwitcherFeed? Feed(string? pipe)
    {
        try
        {
            var client = Client(pipe);
            var pull = client?.PullAsync("switcher").WaitAsync(PullTimeout).GetAwaiter().GetResult();
            return pull?.Data is { } data ? data.Deserialize(ContractsJsonContext.Default.SwitcherFeed) : null;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or AgentNotRunningException or InvalidOperationException or JsonException or ObjectDisposedException)
        {
            Drop();
            return null;
        }
    }

    /// <summary><c>job.resume</c> for a frozen window's job (the switcher asked first); true when it resumed.</summary>
    private static async Task<bool> Resume(string? pipe, string jobId)
    {
        try
        {
            if (Client(pipe) is not { } client)
            {
                return false;
            }

            var result = await client.ActAsync(new ActionRequest { Action = "job.resume", Origin = Origin.Shell, Target = new Dictionary<string, string> { ["job"] = jobId } }).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            return result.Status == ActionStatus.Ok;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or AgentNotRunningException or InvalidOperationException or ObjectDisposedException)
        {
            Drop();
            return false;
        }
    }

    private static PipeClient? Client(string? pipe)
    {
        if (s_client is { IsConnected: true } c)
        {
            return c;
        }

        Drop();
        s_client = PipeClient.ConnectAsync("shell", pipe, timeoutMs: 200).GetAwaiter().GetResult();
        return s_client;
    }

    private static void Drop()
    {
        var c = s_client;
        s_client = null;
        c?.DisposeAsync().AsTask().Wait(TimeSpan.FromMilliseconds(200));
    }
}
