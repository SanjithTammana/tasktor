using Tasktor.Capture.Commands;
using Tasktor.Capture.Browser;
using Tasktor.Capture.Deep;
using Tasktor.Capture.Index;
using Tasktor.Capture.Media;
using Tasktor.Capture.Recording;
using Tasktor.Capture.Security;
using Tasktor.Contracts.Platform;
using Tasktor.Core.Loop;

namespace Tasktor.Capture.Host;

/// <summary>
/// C §6 step 6: the capture host's one logic loop. Sources only enqueue (the browser endpoint's page text and video are
/// posted here); <see cref="DeepCapture"/> runs on this loop alone; every <see cref="PumpEvery"/> the secure-desktop check
/// is refreshed on this thread (the probe must run on the thread attached to the capture desktop) and the input ring is
/// drained. The secure desktop counts as present until the probe proves the user's desktop, and again on any probe
/// failure. <see cref="Stop"/> detaches the sources and closes the open activity, before the indicator goes.
/// The keyboard hook is not attached yet: its layout decoding waits for the real-PC check (Codex lane report).
/// </summary>
public sealed class CaptureSession : IDisposable
{
    public static readonly TimeSpan PumpEvery = TimeSpan.FromMilliseconds(250);

    /// <summary>The content worker's batch (D-C21): closed documents are indexed every 30 min, and once more on stop.</summary>
    public static readonly TimeSpan IngestEvery = TimeSpan.FromMinutes(30);

    private readonly BrowserEndpoint _endpoint;
    private readonly IClock _clock;
    private readonly SecureDesktopGuard _guard;
    private readonly Func<SessionAvailability> _session;
    private IDisposable? _pump;
    private IDisposable? _ingest;
    private bool _started;
    private bool _stopped;
    private volatile bool _secure = true;
    private readonly CaptureRecorder _recorder;
    private readonly MediaSessionLogic _media;
    private SessionAvailability _lastSession = SessionAvailability.Unknown;
    private DateTimeOffset _sessionAt = DateTimeOffset.MinValue;
    private readonly object _sessionGate = new();
    private long _sessionGeneration;

    public CaptureSession(CaptureRecorder recorder, BrowserEndpoint endpoint, IClock clock, IDesktopProbe probe, Func<SessionAvailability> session)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(clock);
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _clock = clock;
        _guard = new SecureDesktopGuard(probe);
        _session = session;
        Loop = new LogicLoop(clock, OnLoopError);
        Ring = new InputRing();
        var suppressor = new Suppressor(recorder.Blocks) { SecureDesktop = () => _secure };
        // Hook ticks are milliseconds of the monotonic clock; their wall time is now minus how long ago they happened.
        var runs = new KeyRunBuilder(new KeyDecoder(), new ActorAttributor(() => []), new SecretRedactor(), suppressor.Check, ms => _clock.UtcNow - TimeSpan.FromMilliseconds(Math.Max(0, Environment.TickCount64 - ms)));
        Deep = new DeepCapture(Ring, runs, new SnapshotDiffer(), new SecretRedactor(), recorder, () => _clock.UtcNow, () => Environment.TickCount64);
        _media = new MediaSessionLogic(media => Deep.Media(media.Reveal()));
        _recorder = recorder;
        _recorder.SetSecure(true); // closed until the first check proves the user's desktop
        _endpoint.TextSeen += OnText;
        _endpoint.MediaSeen += OnMedia;
    }

    public LogicLoop Loop { get; }

    public InputRing Ring { get; }

    public DeepCapture Deep { get; }

    /// <summary>True while capture must treat input as on a secure desktop (and on start, until the first check).</summary>
    public bool SecureDesktop => _secure;

    public long LoopErrors { get; private set; }

    /// <summary>The C21 content index (encrypted on disk), or null when this PC cannot encrypt one: then nothing is indexed.</summary>
    public ContentIndex? Index { get; init; }

    /// <summary>C22: PowerShell's history file, tailed each pump (new lines only); null when there is none to tail.</summary>
    public HistoryTail? History { get; init; }

    /// <summary>Where closed documents are read from for indexing.</summary>
    public RawVault? Vault { get; init; }

    /// <summary>Start the loop thread and its pump.</summary>
    public void Start()
    {
        _pump = Loop.Every(PumpEvery, Tick);
        _ingest = Loop.Every(IngestEvery, Ingest);
        Loop.Start("tasktor-capture-logic");
        _started = true;
    }

    /// <summary>One pump step (on the loop): refresh the secure-desktop state first, then drain input and close idle runs.</summary>
    public void Tick()
    {
        var now = _clock.UtcNow;
        long generation;
        bool refresh;
        SessionAvailability availability;
        lock (_sessionGate)
        {
            generation = _sessionGeneration;
            refresh = now - _sessionAt >= TimeSpan.FromSeconds(1) || now < _sessionAt;
            availability = _lastSession;
        }

        if (refresh) availability = _session(); // the session query is an RPC, at most once a second
        var secure = _guard.ShouldSuppress(availability);
        lock (_sessionGate)
        {
            // A notice invalidates both the session result and the desktop observation. Never reopen from a stale tick.
            if (generation == _sessionGeneration)
            {
                if (refresh)
                {
                    _lastSession = availability;
                    _sessionAt = now;
                }
                _secure = secure;
                _recorder.SetSecure(secure);
            }
        }
        // Read even while the gate is closed so the tail moves past them: a line appended then (another session or a script
        // writing the same history file) is dropped, never held until the gate reopens.
        if (History?.ReadNew() is { Count: > 0 } commands && !_secure)
        {
            Deep.History(commands); // stamped now, to meet the Enter that ran them within a second
        }

        if (!_secure) _media.Tick(now);
        Deep.Pump();
    }

    /// <summary>
    /// A session change (lock, unlock, connect, disconnect, remote): call it from the notification's own thread, first. It
    /// closes the write gate at once (ordered with any write in flight) and forces a fresh session read on the next tick;
    /// only that tick, after the probe proves the user's desktop, opens the gate again.
    /// </summary>
    public void SessionChanged()
    {
        lock (_sessionGate)
        {
            _sessionGeneration++;
            _secure = true;
            _recorder.SetSecure(true);
            _sessionAt = DateTimeOffset.MinValue;
        }
    }

    /// <summary>
    /// Detach the sources and close the open activity (its document is recorded). Idempotent. Returns true only once the
    /// logic thread has exited, so no capture callback can still run (the indicator may then go); false if a callback still
    /// holds it after <see cref="StopTimeout"/>. Callers that must be sure can also await <c>Loop.Completion</c>.
    /// </summary>
    public bool Stop()
    {
        if (_stopped)
        {
            return Loop.Completion.IsCompleted;
        }

        lock (_sessionGate) _stopped = true;
        _endpoint.TextSeen -= OnText;
        _endpoint.MediaSeen -= OnMedia;
        _pump?.Dispose();
        _ingest?.Dispose();
        if (_started)
        {
            Loop.InvokeAsync(() =>
            {
                Deep.CloseActivity();
                Ingest();
                return true;
            }).Wait(TimeSpan.FromSeconds(10));
            return Loop.TryStop(StopTimeout);
        }

        Loop.RunPending();
        Deep.CloseActivity();
        Ingest();
        return Loop.TryStop(TimeSpan.Zero); // never started: complete at once
    }

    /// <summary>How long <see cref="Stop"/> waits for the logic thread to exit.</summary>
    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Index the documents closed since the last batch (on the loop).</summary>
    public void Ingest()
    {
        if (Index is { } index && Vault is { } vault)
        {
            index.Ingest(vault, _clock.UtcNow.AddMinutes(1));
        }
    }

    /// <summary>C21 search, run on the loop (the index is the loop's); null when there is no index.</summary>
    public IReadOnlyList<Contracts.Capture.ActivityHit>? Search(string query, DateTimeOffset? since)
    {
        if (Index is not { } index)
        {
            return null;
        }

        IReadOnlyList<Contracts.Capture.ActivityHit> Run() =>
            [.. index.Search(query, since).Select(h => new Contracts.Capture.ActivityHit { Start = h.Start, End = h.End, Track = h.Track, Snippet = h.Snippet, Links = [.. h.Links] })];
        return _started && !_stopped ? Loop.InvokeAsync(Run).GetAwaiter().GetResult() : Run();
    }

    /// <summary>C21.4: forget the index's activities that included an app or site (on the loop while it runs).</summary>
    public int ForgetIndex(string? app, string? host)
    {
        if (Index is not { } index)
        {
            return 0;
        }

        return _started && !_stopped
            ? Loop.InvokeAsync(() => index.Forget(app, host)).GetAwaiter().GetResult()
            : index.Forget(app, host);
    }

    /// <summary>C21.4: forget the index from <paramref name="since"/> on (the vault forgets its own records); any thread.</summary>
    public int ForgetIndex(DateTimeOffset since)
    {
        if (Index is not { } index)
        {
            return 0;
        }

        return _started && !_stopped
            ? Loop.InvokeAsync(() => index.Forget(since, DateTimeOffset.MaxValue)).GetAwaiter().GetResult()
            : index.Forget(since, DateTimeOffset.MaxValue);
    }

    public void Dispose()
    {
        Stop();
        Loop.Dispose();
        Index?.Dispose();
    }

    /// <summary>Queue a visibility snapshot from a source for processing on the capture loop.</summary>
    public void PostVisible(VisibleSet set) => PostCaptured(() => Deep.Visible(set));

    /// <summary>Queue protected media metadata from a source for processing on the capture loop.</summary>
    public void PostMedia(Sensitive<MediaState> media) => PostCaptured(() => Deep.Media(media.Reveal()));

    /// <summary>Queue native media metadata for combination with audio peaks on the loop.</summary>
    public void PostMediaMetadata(string app, Sensitive<string>? title, Sensitive<string>? artist, MediaStatus status, long positionMs) =>
        PostCaptured(() => _media.Metadata(app, title, artist, status, positionMs));

    /// <summary>Queue one native audio peak for the media state window.</summary>
    public void PostMediaPeak(string app, DateTimeOffset at, float peak) => PostCaptured(() => _media.Peak(app, at, peak));

    /// <summary>Queue a protected UI Automation walk for processing on the capture loop.</summary>
    public void PostUiaWalk(Sensitive<UiaWalk> walk, string app, Sensitive<string>? host) =>
        PostCaptured(() => Deep.Walk(walk.Reveal(), app, host?.Reveal(), DiffSource.Uia));

    private void OnText(string app, int tab, UiaWalk walk, Sensitive<string>? host, Sensitive<string>? url) =>
        PostCaptured(() => Deep.Walk(walk, app, host?.Reveal(), DiffSource.Extension, url?.Reveal()));

    private void OnMedia(string app, int tab, bool playing, double? position) =>
        PostCaptured(() => Deep.Media(new MediaState(app, null, null, playing ? MediaStatus.Playing : MediaStatus.Paused, (long)((position ?? 0) * 1000), Audible: false)));

    private void PostCaptured(Action action)
    {
        lock (_sessionGate)
        {
            if (_secure || _stopped) return;
            var generation = _sessionGeneration;
            Loop.Post(() =>
            {
                lock (_sessionGate)
                {
                    if (_secure || generation != _sessionGeneration) return;
                }
                action();
            });
        }
    }
    private void OnLoopError(Exception ex) => LoopErrors++; // never logged: a capture exception may carry captured text
}
