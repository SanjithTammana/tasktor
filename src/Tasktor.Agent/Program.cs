using Tasktor.Agent.Host;
using Tasktor.Core.Storage;

namespace Tasktor.Agent;

/// <summary><c>tasktor-core</c>: the always-on agent. <c>--data-dir &lt;path&gt;</c> overrides the store root (portable).</summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string? dataDir = null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--data-dir")
            {
                dataDir = args[i + 1];
            }
        }

        var store = StoreLayout.Resolve(dataDir, AppContext.BaseDirectory);
        using var instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            // Another agent runs in this session; it shows the flyout when asked through the pipe.
            return 0;
        }

        var quit = new TaskCompletionSource();
        await using var host = Composition.Create(store);
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

/// <summary>A named mutex per user session: a second <c>tasktor-core</c> exits (D-H9).</summary>
internal static class SingleInstance
{
    public static Mutex? TryAcquire()
    {
        var name = (OperatingSystem.IsWindows() ? "Local\\" : string.Empty) + Hosts.Common.Pipe.PipeName.ForCurrentUser() + "-core";
        var m = new Mutex(initiallyOwned: true, name, out var created);
        if (created)
        {
            return m;
        }

        m.Dispose();
        return null;
    }
}
