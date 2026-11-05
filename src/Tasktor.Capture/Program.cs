using Tasktor.Capture.Browser;
using Tasktor.Capture.Deep;
using Tasktor.Core.Storage;
using Tasktor.Hosts.Common.Pipe;

namespace Tasktor.Capture;

/// <summary>
/// <c>tasktor-capture</c> (D-C21): the T1 capture host. Runs only while the Capture Module is on; started by the agent in
/// a kill-on-close Job Object. Here: the capture blocks, the browser pipe for tasktor-nmh, the control pipe for the
/// agent, and the offline queue drained at start. <c>--data-dir &lt;path&gt;</c> overrides the store root.
/// </summary>
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

        using var single = new Mutex(true, (OperatingSystem.IsWindows() ? "Local\\" : string.Empty) + PipeName.ForCapture(), out var first);
        if (!first)
        {
            return 0; // one capture host per session
        }

        var store = StoreLayout.Resolve(dataDir, AppContext.BaseDirectory);
        var blocks = new CaptureBlocks(store.CaptureBlocks);
        var endpoint = new BrowserEndpoint(blocks, () => DateTimeOffset.UtcNow);
        var control = new CaptureControl(blocks, () => DateTimeOffset.UtcNow) { ResolveTab = endpoint.ResolveTab };
        control.Changed += endpoint.NotifyBlocksChanged;

        // Context the browser queued while this host was not running: drained under the writer's mutex, then kept for the
        // capture pipeline (never logged: it holds hostnames).
        var (queued, invalid) = QueueDrain.Drain(Path.Combine(store.CaptureDir, "nmh-queue.jsonl"));
        _ = (queued.Count, invalid);

        await using var browser = new CapturePipeServer(endpoint);
        await using var ctl = new ControlPipeServer(control);
        browser.Start();
        ctl.Start();

        var stop = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.TrySetResult();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();
        using var expiry = new Timer(_ => endpoint.NotifyBlocksChanged(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        await stop.Task.ConfigureAwait(false);
        return 0;
    }
}
