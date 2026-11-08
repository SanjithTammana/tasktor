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
    private volatile bool _historyRebaseline = true; // the gate starts closed
    private int _discardCommands;

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

    /// <summary>
    /// C19.1: names a click's target (the UIA hit test, #8). Each click asks it once, on the thread pool, never on the loop;
    /// an answer later than <see cref="DeepCapture.ClickTargetBudgetMs"/> is cancelled, and one that comes is posted back
    /// through the gate like any source event. Null: clicks keep no target. Set it before <see cref="Start"/>.
    /// </summary>
    public IClickTargetResolver? ClickTargets
    {
        get => _clickTargets;
        set
        {
            _clickTargets = value;
            Deep.ClickTargetRequested = value is null ? null : RequestClickTarget;
        }
    }

    private IClickTargetResolver? _clickTargets;

    /// <summary>C21.2: the chunk and query encoder (behind gate G8); null: the index is words only.</summary>
    public IChunkEmbedder? Embedder { get; init; }

    /// <summary>Chunks sent to the encoder per batch, and how long a query's vector may take before words alone answer.</summary>
    public const int EmbedBatch = 256;

    public static readonly TimeSpan QueryEmbedTimeout = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan EmbedTimeout = TimeSpan.FromMinutes(5);
    private int _embedding;

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
                if (_secure && !secure) Deep.PointerBoundary(Environment.TickCount64);
                _secure = secure;
                _recorder.SetSecure(secure);
            }
        }
        // C22 history across a closed gate (secure desktop, lock, switch user). History lines carry no timestamps, so the
        // boundary is the read itself: the file is read on every tick so its tail moves on; lines read while the gate is
        // closed are dropped, and so is everything read on the first open tick after it (written before that tick, so
        // possibly during the closed period). Commands held for pairing are dropped too. Lines first read after that
        // were written on the user's own desktop and are recorded as usual. Fail closed, without pausing until restart.
        if (_secure)
        {
            _historyRebaseline = true;
            Interlocked.Exchange(ref _discardCommands, 1);
        }

        if (Interlocked.Exchange(ref _discardCommands, 0) != 0)
        {
            Deep.Commands.Clear();
        }

        var historyGeneration = HistoryGeneration();
        var lines = History?.ReadNew();
        AcceptHistory(historyGeneration, lines);
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
            _historyRebaseline = true;
            Interlocked.Exchange(ref _discardCommands, 1);
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
            EmbedPending(index);
        }
    }

    /// <summary>
    /// The new chunks' texts go to the encoder off the loop (one batch at a time); the vectors come back to the loop. A
    /// chunk forgotten meanwhile takes none. Returns the task (tests wait on it), or null when nothing was started.
    /// </summary>
    public Task? EmbedPending(ContentIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (Embedder is not { } embedder || Interlocked.CompareExchange(ref _embedding, 1, 0) != 0) return null;
        var pending = index.Unembedded(EmbedBatch);
        if (pending.Count == 0)
        {
            Volatile.Write(ref _embedding, 0);
            return null;
        }

        return Task.Run(async () =>
        {
            try
            {
                using var cancel = new CancellationTokenSource(EmbedTimeout);
                if (await embedder.EmbedAsync(pending, cancel.Token).ConfigureAwait(false) is { } vectors)
                {
                    void Store()
                    {
                        foreach (var (id, q, s) in vectors) index.SetEmbedding(id, q, s);
                    }

                    if (_started && !_stopped) await Loop.InvokeAsync(() => { Store(); return true; }).ConfigureAwait(false);
                    else Store();
                }
            }
            catch (Exception)
            {
                // the next batch tries again; never logged (the texts are personal)
            }
            finally
            {
                Volatile.Write(ref _embedding, 0);
            }
        });
    }

    /// <summary>C21 search, run on the loop (the index is the loop's); null when there is no index.</summary>
    public IReadOnlyList<Contracts.Capture.ActivityHit>? Search(string query, DateTimeOffset? since)
    {
        if (Index is not { } index)
        {
            return null;
        }

        // The query's vector is made here, off the loop (the caller's thread), within a short budget; without it, words only.
        (sbyte[] Q, float S)? vector = null;
        if (Embedder is { } embedder)
        {
            try
            {
                using var cancel = new CancellationTokenSource(QueryEmbedTimeout);
                if (embedder.EmbedAsync([(0, query)], cancel.Token).WaitAsync(cancel.Token).GetAwaiter().GetResult() is [var v, ..])
                {
                    vector = (v.Q, v.S);
                }
            }
            catch (Exception)
            {
                vector = null;
            }
        }

        IReadOnlyList<Contracts.Capture.ActivityHit> Run() =>
            [.. index.Search(query, since, query: vector).Select(h => new Contracts.Capture.ActivityHit { Start = h.Start, End = h.End, Track = h.Track, Snippet = h.Snippet, Links = [.. h.Links] })];
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

    /// <summary>Queue the end of an app's audio sessions without inventing a playback state.</summary>
    public void PostMediaAudioStopped(string app) => PostCaptured(() => _media.AudioStopped(app));
    /// <summary>Queue the departure of a native media source, clearing its audible state and metadata.</summary>
    public void PostMediaRemove(string app) => PostCaptured(() => _media.Remove(app));
    /// <summary>
    /// Queue a focus change from UIA (C17 / C17.2): the field's identity and password state, the previous field's final
    /// value and the new field's value (both null for a password or unknown field).
    /// </summary>
    public void PostFocus(FocusTarget target, Sensitive<string>? previousValue, Sensitive<string>? value) =>
        PostCaptured(() => Deep.Focus(target, previousValue?.Reveal(), value?.Reveal()));

    /// <summary>
    /// C22.4: a command a known agent's child ran. Recorded under the agent's exe (its blocks apply) with the agent as actor,
    /// through the same scrub and gate as typed commands.
    /// </summary>
    public void PostAgentCommand(string agent, string agentExe, string exe, Sensitive<string> commandLine) =>
        PostCaptured(() => Deep.Command(new CommandEvent(_clock.UtcNow, agentExe, commandLine, CommandSource.Agent, new Actor(ActorKind.Agent, agent, ActorEvidence.AgentAncestor))));

    /// <summary>Queue a protected UI Automation walk for processing on the capture loop.</summary>
    public void PostUiaWalk(Sensitive<UiaWalk> walk, string app, Sensitive<string>? host) =>
        PostCaptured(() => Deep.Walk(walk.Reveal(), app, host?.Reveal(), DiffSource.Uia));

    private void RequestClickTarget(long id, int x, int y)
    {
        var resolver = _clickTargets!;
        _ = Task.Run(async () =>
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(DeepCapture.ClickTargetBudgetMs));
            Sensitive<string>? target;
            try
            {
                target = await resolver.ResolveAsync(x, y, cancel.Token).WaitAsync(cancel.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return; // late or failed: the click keeps no target (never logged: it could carry what was on screen)
            }

            if (target is { } t)
            {
                PostCaptured(() => Deep.ClickTarget(id, t.Reveal()));
            }
        });
    }

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
    // Captured before the file read so a concurrent notice cannot consume a newer rebaseline.
    internal long HistoryGeneration()
    {
        lock (_sessionGate) return _sessionGeneration;
    }

    // Runs on the logic thread; a batch crossing a notice is dropped without clearing that notice's rebaseline.
    internal void AcceptHistory(long generation, IReadOnlyList<Sensitive<string>>? lines)
    {
        lock (_sessionGate)
        {
            if (_secure || generation != _sessionGeneration) return;
            if (_historyRebaseline)
            {
                _historyRebaseline = false;
                return;
            }
        }
        if (lines is { Count: > 0 }) Deep.History(lines);
    }
    private void OnLoopError(Exception ex) => LoopErrors++; // never logged: a capture exception may carry captured text
}
