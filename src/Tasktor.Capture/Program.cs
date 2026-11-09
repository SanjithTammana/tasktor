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

        // C19 acceptance 3 check (real PC): times the mouse hook's per-event work at 1000 Hz for 10 s on synthetic input;
        // prints timings only. Records nothing and needs no consent: no hook is installed.
        if (args.Contains("--bench-hook"))
        {
            var bench = Hook.HookLatencyBench.Run(new Hook.RingHookSink(new InputRing()), 10_000, 1000);
            Console.WriteLine(bench.ToString());
            return bench.WithinBudget ? 0 : 1;
        }

        // C18 check (real PC, #8): walks only its own fixture windows; prints pass / fail and timings.
        if (args.Contains("--check-uia")) return UIAutomation.UiaFixtureCheck.Run(Console.Out);
        if (args.Contains(UIAutomation.UiaFixtureCheck.ChildFlag)) return UIAutomation.UiaFixtureCheck.RunChild(args.Contains(UIAutomation.UiaFixtureCheck.HangFlag), Console.Out);

        // C20 check (real PC, #9): its own tone and silence through its own audio session; prints pass / fail only.
        if (args.Contains("--check-media")) return Media.MediaToneCheck.Run(Console.Out);

        // C19.2 check (real PC, #7): live enumeration + geometry, timed apart; prints timings and window counts only.
        if (args.Contains("--bench-visibility"))
        {
            var bench = Visibility.VisibilityBench.Run(new Visibility.Win32WindowGeometry(), 1000);
            Console.WriteLine(bench.ToString());
            return bench.WithinTarget ? 0 : 1;
        }
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
        // A hook Windows removed and that would not reinstall stops capture like a failed indicator: the agent's supervisor
        // restarts it (and reports repeated failures), instead of capture running on with no input recorded.
        return await IndicatorHost.RunAsync(indicator, () => new CaptureLifetime(store, stop.Task, indicator, historyFile, browser, () => stop.TrySetResult(true)), stop.Task).ConfigureAwait(false);
    }
}

/// <summary>All recording sources are stopped and drained before the gate removes its indicators.</summary>
internal sealed class CaptureLifetime : IDisposable
{
    private CapturePipeServer? _browser;
    private ControlPipeServer? _control;
    private Host.CaptureSession? _session;
    private Hook.LowLevelInputHook? _hook;
    private Visibility.Win32VisibilitySource? _visibility;
    private UIAutomation.UiaWalkSource? _uia;
    private Media.WasapiMeters? _meters;
    private IDisposable? _smtc;
    private Timer? _expiry, _retention;
    private bool _disposed;
    private readonly Win32RecordingIndicator _indicator;

    public CaptureLifetime(StoreLayout store, Task<bool> stop, Win32RecordingIndicator indicator, string? historyFile = null, bool browser = true, Action? hookFailed = null)
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
            // and session checks fail closed. Hooks attach per CaptureHooks.Attached (keyboard and mouse, each after
            // its real-PC check).
            var sessions = new Security.WtsSessionSnapshot(new Security.Win32WtsSessionQuery());
            // C21: the content index exists only encrypted (OQ-C22: SQLCipher). With an engine that cannot encrypt, OpenEncrypted
            // returns null and nothing is indexed: no plain-text index of what was on screen is ever written.
            var index = Index.ContentIndex.OpenEncrypted(Path.Combine(store.CaptureDir, "index.db"), key.EncryptionKey("capture-index"));
            _session = new Host.CaptureSession(recorder, endpoint, Core.Time.SystemClock.Instance, new Security.Win32DesktopProbe(), sessions.Read)
            {
                Index = index,
                Vault = vault,

                // C21.2: chunk embeddings only with a model that passed gate G8 (capture\embedding\g8.json); else words only.
                // The 1 MB static table (in process) comes first; an ONNX model through the worker is the fallback.
                Embedder = (Index.IChunkEmbedder?)Index.StaticEmbedder.TryCreate(Path.Combine(store.CaptureDir, "embedding"))
                    ?? Index.WorkerEmbedder.TryCreate(
                        Path.Combine(AppContext.BaseDirectory, "worker", "python", OperatingSystem.IsWindows() ? "python.exe" : "bin/python3"),
                        Path.Combine(AppContext.BaseDirectory, "worker"),
                        Path.Combine(store.CaptureDir, "embedding")),
                History = new Commands.HistoryTail(historyFile ?? Commands.HistoryTail.DefaultPath(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))),
            };
            _indicator.SessionChanged += _session.SessionChanged;

            // C18.1 / C17: UIA on its own MTA thread (focus with password state, debounced walks, click targets). An app or
            // site block, a pause or a schedule stops it from reading at all; the gate drops what it posts while closed.
            var live = _session;
            _uia = new UIAutomation.UiaWalkSource(UIAutomation.Win32UiaClient.Create, Windows.Shell.Win32Windows.AppOfWindow,
                app => blocks.Blocking(now(), app, null, null) is not null, (walk, app) => live.PostUiaWalk(walk, app, null), live.PostFocus);
            _session.ClickTargets = _uia;
            _session.Start();
            _uia.Start();

            // C19.1 / C17: the low-level hook thread only copies each event into the session's ring; the loop drains it
            // (pointer shaping, key runs) behind the same gate. Installed after the indicator, removed before it (Dispose).
            _hook = new Hook.LowLevelInputHook(new Hook.RingHookSink(_session.Ring), hookFailed ?? (() => { }), Hook.CaptureHooks.Attached);
            if (!_hook.Start()) hookFailed?.Invoke();

            // C20.1: audible (WASAPI peaks, 1 Hz) and now-playing (SMTC); both only post, the gate drops while closed.
            var appOfPid = (uint pid) => Windows.Shell.Win32Windows.ExeOfProcess(pid);
            _meters = new Media.WasapiMeters(new Media.AudioMeterLogic(appOfPid, live.PostMediaPeak, live.PostMediaAudioStopped), now);
            _meters.Start();
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            {
                var smtc = new Media.SmtcSource(live.PostMediaMetadata, live.PostMediaRemove);
                _smtc = smtc;
                _ = smtc.StartAsync();
            }

            // C19.2: visible windows (numbers and apps only) into the loop; the gate drops them while closed.
            var session = _session;
            _visibility = new Visibility.Win32VisibilitySource(new Visibility.VisibilityProducer(new Visibility.Win32WindowGeometry(), session.PostVisible, now));
            _visibility.Start();
            var control = new CaptureControl(blocks, now)
            {
                ResolveTab = endpoint.ResolveTab,
                AgentCommand = (agent, agentExe, exe, line) => _session?.PostAgentCommand(agent, agentExe, exe, line),
                RecordsToday = () => recorder.RecordsToday,
                Extensions = endpoint.Connected,
                Search = (query, since) => _session?.Search(query, since),
                // C21.4: what is still open in memory goes first (else it is written later with the forgotten text), then
                // the vault and the index together.
                ForgetSince = since =>
                {
                    _session?.ForgetOpen(since);
                    var n = recorder.ForgetSince(since);
                    _session?.ForgetIndex(since);
                    return n;
                },
                ForgetMatching = (app, host) =>
                {
                    _session?.ForgetOpen(app, host);
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
        if (_hook is not null) { _hook.Dispose(); _hook = null; } // input stops first, and before the indicator goes
        if (_visibility is not null) { _visibility.Dispose(); _visibility = null; } // detached before the session stops
        if (_uia is not null) { _uia.Dispose(); _uia = null; }
        if (_meters is not null) { _meters.Dispose(); _meters = null; }
        if (_smtc is not null) { _smtc.Dispose(); _smtc = null; }
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
