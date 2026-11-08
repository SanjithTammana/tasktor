using Tasktor.Agent.Host;
using Tasktor.Core.Storage;

namespace Tasktor.Agent;

/// <summary>
/// <c>tasktor-core</c>: the always-on agent. <c>--data-dir &lt;path&gt;</c> overrides the store root (portable). For checks
/// beside the owner's own agent: <c>--pipe &lt;name&gt;</c> (its own pipe and single-instance lock) and
/// <c>--switcher-pid &lt;pid&gt;</c> (repeatable: the switcher lists only those processes' windows, reading no other title).
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var a = AgentArgs.Parse(args);
        var store = StoreLayout.Resolve(a.DataDir, AppContext.BaseDirectory);
        using var instance = SingleInstance.TryAcquire(a.Pipe);
        if (instance is null)
        {
            // Another agent runs in this session; it shows the flyout when asked through the pipe.
            return 0;
        }

        var quit = new TaskCompletionSource();
        await using var host = Composition.Create(store, tray: true, pipeName: a.Pipe, switcherPids: a.SwitcherPids);
        host.QuitRequested += () => quit.TrySetResult();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            quit.TrySetResult();
        };
        host.Start();
        await quit.Task.ConfigureAwait(false);
        return 0;
    }
}

/// <summary>The agent's command line (only what it knows; anything else is ignored).</summary>
internal sealed record AgentArgs(string? DataDir, string? Pipe, IReadOnlyList<uint> SwitcherPids)
{
    public static AgentArgs Parse(IReadOnlyList<string> args)
    {
        string? dataDir = null, pipe = null;
        var pids = new List<uint>();
        for (var i = 0; i < args.Count - 1; i++)
        {
            var value = args[i + 1];
            switch (args[i])
            {
                case "--data-dir":
                    dataDir = value;
                    break;
                case "--pipe" when System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z0-9._-]{1,64}$"):
                    pipe = value;
                    break;
                case "--switcher-pid" when uint.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid):
                    pids.Add(pid);
                    break;
            }
        }

        return new AgentArgs(dataDir, pipe, pids);
    }
}

/// <summary>A named mutex per user session and pipe: a second <c>tasktor-core</c> on the same pipe exits (D-H9).</summary>
internal static class SingleInstance
{
    public static string LockName(string? pipe) =>
        (OperatingSystem.IsWindows() ? "Local\\" : string.Empty) + (pipe ?? Hosts.Common.Pipe.PipeName.ForCurrentUser()) + "-core";

    public static Mutex? TryAcquire(string? pipe = null)
    {
        var m = new Mutex(initiallyOwned: true, LockName(pipe), out var created);
        if (created)
        {
            return m;
        }

        m.Dispose();
        return null;
    }
}
