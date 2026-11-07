using Tasktor.Capture.Browser;
using Tasktor.Capture.Deep;
using Tasktor.Capture.Indicator;
using Tasktor.Core.Storage;
using Tasktor.Hosts.Common.Pipe;

namespace Tasktor.Capture;

/// <summary>The consented T1 capture host. The indicators own the capture lifetime.</summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string? dataDir = null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--data-dir") dataDir = args[i + 1];
        }

        using var single = new Mutex(true, (OperatingSystem.IsWindows() ? "Local\\" : string.Empty) + PipeName.ForCapture(), out var first);
        if (!first) return 0;
        var store = StoreLayout.Resolve(dataDir, AppContext.BaseDirectory);
        var consents = new ConsentStore(store, (_, _) => { });
        consents.Load();
        if (!consents.HasModule("capture")) return 3;

        var stop = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var indicator = new Win32RecordingIndicator();
        indicator.Failed += () => stop.TrySetResult(true);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(false); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult(false);
        return await IndicatorHost.RunAsync(indicator, () => new CaptureLifetime(store, stop.Task), stop.Task).ConfigureAwait(false);
    }
}

/// <summary>All recording sources are stopped and drained before the gate removes its indicators.</summary>
internal sealed class CaptureLifetime : IDisposable
{
    private CapturePipeServer? _browser;
    private ControlPipeServer? _control;
    private Host.CaptureSession? _session;
    private Timer? _expiry, _retention;
    private bool _disposed;

    public CaptureLifetime(StoreLayout store, Task<bool> stop)
    {
        try
        {
            if (stop.IsCompleted) return;
            var protection = OperatingSystem.IsWindows() ? Windows.Security.Dpapi.Protection : KeyProtection.None;
            var key = InstallKey.LoadOrCreate(store, protection);
            var now = () => DateTimeOffset.UtcNow;
            var blocks = new CaptureBlocks(store.CaptureBlocks);
            var vault = new RawVault(Path.Combine(store.CaptureDir, "raw"), key.EncryptionKey("capture-vault"));
            vault.Purge(now());
            var recorder = new Recording.CaptureRecorder(vault, blocks, now);
            var endpoint = new BrowserEndpoint(blocks, now);
            endpoint.ContextChanged += (browser, tab, ctx) => { if (!stop.IsCompleted) recorder.Browser(browser, tab, ctx); };

            // Step 6: one logic loop for DeepCapture; the browser's page text and video are posted to it. The secure-desktop
            // and session checks fail closed. The keyboard hook is not attached yet (layout decoding awaits the real-PC check).
            var sessions = new Security.WtsSessionSnapshot(new Security.Win32WtsSessionQuery());
            _session = new Host.CaptureSession(recorder, endpoint, Core.Time.SystemClock.Instance, new Security.Win32DesktopProbe(), sessions.Read);
            _session.Start();
            var control = new CaptureControl(blocks, now)
            {
                ResolveTab = endpoint.ResolveTab,
                RecordsToday = () => recorder.RecordsToday,
                Extensions = endpoint.Connected,
                ForgetSince = recorder.ForgetSince,
            };
            control.Changed += endpoint.NotifyBlocksChanged;

            if (stop.IsCompleted) return;
            var (queued, _) = QueueDrain.Drain(Path.Combine(store.CaptureDir, "nmh-queue.jsonl"));
            foreach (var m in queued)
            {
                if (stop.IsCompleted) break;
                if (m is Contracts.Capture.BrowserTab { Incognito: false } t) // a private tab is never recorded from the queue
                    recorder.Browser("chrome", t.Tab, new TabContext(t.Host is null ? null : Contracts.Platform.Sensitive.Of(t.Host), Secure: true, t.Active, t.Active), queued: true);
            }

            if (stop.IsCompleted) return;
            _browser = new CapturePipeServer(endpoint);
            _control = new ControlPipeServer(control);
            _browser.Start();
            _control.Start();
            _expiry = new Timer(_ => endpoint.NotifyBlocksChanged(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            _retention = new Timer(_ => vault.Purge(now()), null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_expiry is not null) { _expiry.DisposeAsync().AsTask().GetAwaiter().GetResult(); _expiry = null; }
        if (_retention is not null) { _retention.DisposeAsync().AsTask().GetAwaiter().GetResult(); _retention = null; }
        if (_control is not null) { _control.DisposeAsync().AsTask().GetAwaiter().GetResult(); _control = null; }
        if (_browser is not null) { _browser.DisposeAsync().AsTask().GetAwaiter().GetResult(); _browser = null; }
        if (_session is not null) { _session.Dispose(); _session = null; } // after the sources: close the activity, then the loop
        _disposed = true;
    }
}
