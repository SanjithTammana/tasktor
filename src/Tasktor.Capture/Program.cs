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

        // C §1: capture refuses to start without a valid consent record (D-C22), whoever started it.
        var consents = new ConsentStore(store, (_, _) => { });
        consents.Load();
        if (!consents.HasModule("capture"))
        {
            return 3; // CaptureSupervisor.ExitNoConsent: the agent shows "no consent on record" and does not restart
        }

        var protection = OperatingSystem.IsWindows() ? Windows.Security.Dpapi.Protection : KeyProtection.None;
        var key = InstallKey.LoadOrCreate(store, protection); // the agent's install key, DPAPI-protected for this user
        var now = () => DateTimeOffset.UtcNow;
        var blocks = new CaptureBlocks(store.CaptureBlocks);
        var vault = new RawVault(Path.Combine(store.CaptureDir, "raw"), key.EncryptionKey("capture-vault"));
        vault.Purge(now()); // 7-day retention at start
        var recorder = new Recording.CaptureRecorder(vault, blocks, now);
        var endpoint = new BrowserEndpoint(blocks, now);
        endpoint.ContextChanged += (browser, tab, ctx) => recorder.Browser(browser, tab, ctx);
        var control = new CaptureControl(blocks, now)
        {
            ResolveTab = endpoint.ResolveTab,
            RecordsToday = () => recorder.RecordsToday,
            ForgetSince = recorder.ForgetSince,
        };
        control.Changed += endpoint.NotifyBlocksChanged;

        // Context the browser queued while this host was not running: drained under the writer's mutex and recorded as
        // queued (blocks still apply; never logged: it holds hostnames). Queued lines carry no browser: recorded as chrome.
        var (queued, _) = QueueDrain.Drain(Path.Combine(store.CaptureDir, "nmh-queue.jsonl"));
        foreach (var m in queued)
        {
            if (m is Contracts.Capture.BrowserTab t)
            {
                recorder.Browser("chrome", t.Tab, new TabContext(t.Host is null ? null : Contracts.Platform.Sensitive.Of(t.Host), Secure: true, t.Active, t.Active), queued: true);
            }
        }

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
        using var retention = new Timer(_ => vault.Purge(now()), null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        await stop.Task.ConfigureAwait(false);
        return 0;
    }
}
