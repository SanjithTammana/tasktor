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
        // OQ-C22: this process's SQLite is SQLCipher (for the encrypted content index); bound before any database opens.
        SqliteDb.UseEngine("e_sqlcipher");
        string? dataDir = null, historyFile = null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--data-dir") dataDir = args[i + 1];
            // C22 checks: tail this file instead of PSReadLine's own, so a check never touches the owner's real history.
            if (args[i] == "--history-file") historyFile = Path.GetFullPath(args[i + 1]);
        }

        // Checks on a PC whose browsers are running: accept no browser data at all (no browser pipe, no offline queue),
        // so a fixture run never records the owner's real browsing.
        var browser = !args.Contains("--no-browser");

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
        return await IndicatorHost.RunAsync(indicator, () => new CaptureLifetime(store, stop.Task, indicator, historyFile, browser), stop.Task).ConfigureAwait(false);
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
    private readonly Win32RecordingIndicator _indicator;

    public CaptureLifetime(StoreLayout store, Task<bool> stop, Win32RecordingIndicator indicator, string? historyFile = null, bool browser = true)
    {
        _indicator = indicator;
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
            // C21: the content index exists only encrypted (OQ-C22: SQLCipher). With an engine that cannot encrypt, OpenEncrypted
            // returns null and nothing is indexed: no plain-text index of what was on screen is ever written.
            var index = Index.ContentIndex.OpenEncrypted(Path.Combine(store.CaptureDir, "index.db"), key.EncryptionKey("capture-index"));
            _session = new Host.CaptureSession(recorder, endpoint, Core.Time.SystemClock.Instance, new Security.Win32DesktopProbe(), sessions.Read)
            {
                Index = index,
                Vault = vault,
                History = new Commands.HistoryTail(historyFile ?? Commands.HistoryTail.DefaultPath(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))),
            };
            _indicator.SessionChanged += _session.SessionChanged;
            _session.Start();
            var control = new CaptureControl(blocks, now)
            {
                ResolveTab = endpoint.ResolveTab,
                RecordsToday = () => recorder.RecordsToday,
                Extensions = endpoint.Connected,
                Search = (query, since) => _session?.Search(query, since),
                ForgetSince = since =>
                {
                    var n = recorder.ForgetSince(since);
                    _session?.ForgetIndex(since); // C21.4: vault and index forget together
                    return n;
                },
                ForgetMatching = (app, host) =>
                {
                    var n = recorder.ForgetMatching(app, host);
                    _session?.ForgetIndex(app, host);
                    return n;
                },
            };
            control.Changed += endpoint.NotifyBlocksChanged;

            if (stop.IsCompleted) return;
            var (queued, _) = browser ? QueueDrain.Drain(Path.Combine(store.CaptureDir, "nmh-queue.jsonl")) : ([], 0);
            foreach (var m in queued)
            {
                if (stop.IsCompleted) break;
                if (m is Contracts.Capture.BrowserTab { Incognito: false } t) // a private tab is never recorded from the queue
                    recorder.Browser("chrome", t.Tab, new TabContext(t.Host is null ? null : Contracts.Platform.Sensitive.Of(t.Host), Secure: true, t.Active, t.Active), queued: true);
            }

            if (stop.IsCompleted) return;
            _browser = browser ? new CapturePipeServer(endpoint) : null;
            _control = new ControlPipeServer(control);
            _browser?.Start();
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
        if (_session is not null)
        {
            // After the sources: close the activity, then the loop. Fail closed: while a capture callback still holds the
            // loop, throw so the indicator gate keeps the indicator up and retries; a retry succeeds once the thread is gone.
            if (!_session.Stop()) throw new InvalidOperationException("The capture loop has not stopped yet.");
            _indicator.SessionChanged -= _session.SessionChanged;
            _session.Dispose();
            _session = null;
        }
        _disposed = true;
    }
}
