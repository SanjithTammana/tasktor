using Tasktor.Capture.Commands;
using Tasktor.Capture.Recording;
using Tasktor.Contracts.Platform;
using Tasktor.Core.Apps;

namespace Tasktor.Capture.Deep;

/// <summary>
/// C §6 / §5: the capture host's logic thread, the one place the T1 parts meet. The hook ring is drained in order into
/// <see cref="KeyRunBuilder"/> (decode, suppression, actor, redaction); closed runs, view diffs (redacted line by line)
/// and media go to the vault through <see cref="CaptureRecorder"/>, which applies every capture block; only what the
/// recorder accepted reaches an <see cref="ActivityDocumentBuilder"/> document, so a blocked app never leaks into one.
/// Side tracks come from <see cref="TrackManager"/>; a closed track's or activity's document is recorded too.
/// Single-threaded by design (no locks): the host calls it from its logic loop only.
/// </summary>
public sealed class DeepCapture
{
    public const int DrainChunk = 512;

    private readonly InputRing _ring;
    private readonly KeyRunBuilder _runs;
    private readonly SnapshotDiffer _differ;
    private readonly SecretRedactor _redactor;
    private readonly CaptureRecorder _recorder;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<long> _nowMs;
    private readonly RawInput[] _buffer = new RawInput[DrainChunk];
    private readonly Dictionary<ViewKey, (string App, string? Host)> _viewOwners = [];
    private FocusTarget? _focus;
    private readonly PointerLogic _pointer = new();
    private readonly ActivityBoundary _boundary = new();
    private long _pointerAfter = long.MinValue;
    private readonly Dictionary<long, PendingClick> _pendingClicks = [];
    private long _nextClick;

    /// <summary>C19 acceptance 2: a click's target counts only if it arrives within this budget of the click.</summary>
    public const int ClickTargetBudgetMs = 100;

    /// <summary>Clicks waiting for their target at most (older ones are dropped first; the budget expires the rest).</summary>
    public const int MaxPendingClicks = 64;

    /// <summary>
    /// Asked once per recorded click with (id, x, y): resolve the element under the point <b>off</b> this thread and answer
    /// with <see cref="ClickTarget"/> on it. Null: clicks keep no target. Must only start the work, never wait on it.
    /// </summary>
    public Action<long, int, int>? ClickTargetRequested { get; set; }

    public int PendingClicks => _pendingClicks.Count;

    /// <summary>Clicks whose target was asked for, and those whose target was kept (C19 acceptance 2: ≥ 90% in UIA apps).</summary>
    public long ClickTargetsAsked { get; private set; }

    public long ClickTargetsKept { get; private set; }

    private sealed record PendingClick(PointerEvent Click, string App, string? Host, int Track, DateTimeOffset At, DateTimeOffset? DocStart);

    /// <param name="nowMs">The key clock: the same milliseconds the hook stamps on <see cref="RawInput.TimeMs"/>.</param>
    public DeepCapture(InputRing ring, KeyRunBuilder runs, SnapshotDiffer differ, SecretRedactor redactor, CaptureRecorder recorder, Func<DateTimeOffset> now, Func<long> nowMs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        _ring = ring;
        _runs = runs;
        _differ = differ;
        _redactor = redactor;
        _recorder = recorder;
        _now = now;
        _nowMs = nowMs;
        _runs.RunClosed += OnRun; // runs close inside Key / Tick / Focus: _focus is still the run's target then
    }

    public TrackManager Tracks { get; } = new();

    public ActivityDocumentBuilder Documents { get; } = new();

    public FieldTruth Field { get; } = new();

    /// <summary>C22: Enter-terminated runs in terminals meet shell history lines here; <see cref="Pump"/> records them.</summary>
    public CommandReconciler Commands { get; } = new();

    public long CommandsWritten { get; private set; }

    /// <summary>Ring entries lost to overwrite (the card reports it).</summary>
    public long Dropped => _ring.Dropped;

    public long DocumentsWritten { get; private set; }

    /// <summary>One logic-loop step: drain the ring into the run builder, then let time close idle runs and tracks.</summary>
    public int Pump()
    {
        var total = DrainKeys();
        ExpireClicks(_nowMs());
        _runs.Tick(_nowMs());
        var now = _now();
        Apply(Tracks.Tick(now));
        foreach (var c in Commands.Flush(now))
        {
            Command(c);
        }

        if (_boundary.Tick(now))
        {
            CloseActivity();
        }

        return total;
    }

    /// <summary>Keys typed before an event belong before it: every boundary drains the ring first.</summary>
    private int DrainKeys()
    {
        var total = 0;
        int n;
        while ((n = _ring.Drain(_buffer)) > 0)
        {
            for (var i = 0; i < n; i++)
            {
                if (_buffer[i].Kind == RawInputKind.Keyboard) _runs.Key(_buffer[i].ExtendTick(_nowMs()));
                else if (_buffer[i].Kind == RawInputKind.Mouse) Pointer(_buffer[i].ExtendTick(_nowMs()));
            }

            total += n;
            if (n < _buffer.Length)
            {
                break;
            }
        }

        return total;
    }

    /// <summary>
    /// Focus moved. <paramref name="previousValue"/> is the old field's final value when it exposes one: the open run then
    /// closes with the field's value diff as its text (paste, autocomplete, IME). <paramref name="value"/> is the new
    /// field's value, held for the next diff (never for a password field).
    /// </summary>
    public void Focus(FocusTarget target, string? previousValue = null, string? value = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        DrainKeys(); // the keys typed into the old field, before it lost focus
        var ms = _nowMs();
        if (Field.Held is { } before && previousValue is not null)
        {
            _runs.Close(RunEnd.FocusOut, ms, before, previousValue);
        }

        _runs.Focus(target, ms);
        _focus = target;
        Field.Hold(target.IsPassword != false || target.SecureField ? null : value);
    }

    /// <summary>A walk of a view (UIA or the extension): diff, redact, record, and add the accepted text to its track.</summary>
    /// <param name="link">The page's address when the walk is a browser page (scrubbed before it is kept).</param>
    public ViewDiff? Walk(UiaWalk walk, string app, string? host, DiffSource source = DiffSource.Uia, string? link = null)
    {
        ArgumentNullException.ThrowIfNull(walk);
        var now = _now();
        _viewOwners[walk.View] = (app, host); // a view's later records (its volatile close-out) keep its own app's blocks
        if (_viewOwners.Count > SnapshotDiffer.MaxViews * 2)
        {
            _viewOwners.Clear(); // the differ evicted most of these already; owners return with the next walk
            _viewOwners[walk.View] = (app, host);
        }

        if (_differ.Diff(walk, now, source) is not { } diff)
        {
            return null;
        }

        var clean = Scrub(diff);
        if (_recorder.View(clean, app, host))
        {
            Documents.Add(TrackOf(app), clean);
            Documents.Note(TrackOf(app), app, host);
            Documents.Link(TrackOf(app), _recorder.ScrubUrl(link));
        }

        return clean;
    }

    public void Media(MediaState media)
    {
        ArgumentNullException.ThrowIfNull(media);
        var now = _now();
        Apply(Tracks.On(media, now));
        if (_recorder.Media(media) && TrackFor(media.App) is { } track)
        {
            Documents.Add(track, media, now);
            Documents.Note(track, media.App, null);
        }
    }

    /// <summary>New shell history lines (C22.1), stamped now; they wait in <see cref="Commands"/> for a matching run.</summary>
    public void History(IReadOnlyList<Sensitive<string>> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        DrainKeys(); // the Enter that ran them is in the ring: let its run reach the reconciler first
        var now = _now();
        foreach (var c in commands)
        {
            Commands.History(c, now);
        }
    }

    /// <summary>
    /// Records one command: scrubbed for secrets (command lines carry tokens), checked against the blocks of its
    /// terminal, the foreground app and, for a history-only line, both PowerShells (they share one history file), then
    /// added to the activity document under "ran".
    /// </summary>
    public bool Command(CommandEvent command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var (text, hits) = _redactor.Scrub(command.Text.Reveal());
        List<string> apps = command.App is { } app ? [app] : ["pwsh.exe", "powershell.exe"];
        if (Tracks.Foreground is { } fg && !apps.Contains(fg, StringComparer.OrdinalIgnoreCase))
        {
            apps.Add(fg);
        }

        var record = new CommandRecord(command.App, text, command.Source.ToString().ToLowerInvariant(), command.Actor.ToString(), hits.Values.Sum());
        if (!_recorder.Command(record, apps))
        {
            return false;
        }

        CommandsWritten++;
        var track = TrackOf(command.App ?? Tracks.Foreground ?? string.Empty);
        Documents.Add(track, text, command.Actor, command.At);
        foreach (var a in apps)
        {
            Documents.Note(track, a, null);
        }

        return true;
    }

    public void Visible(VisibleSet set)
    {
        Apply(Tracks.On(set));
        Boundary();
    }

    public void Foreground(string app)
    {
        Apply(Tracks.OnForeground(app, _now()));
        Boundary();
    }

    /// <summary>D-C1: the foreground activity ends here when another app has been in front long enough (see <see cref="ActivityBoundary"/>).</summary>
    private void Boundary()
    {
        if (_boundary.Foreground(Tracks.Foreground, _now()))
        {
            CloseActivity();
        }
    }

    /// <summary>
    /// C21.4: what this thread still holds from an app or site goes before the vault forgets it. Open documents that
    /// included it go whole (they would otherwise be written later, with its text in them), its views' baselines and waiting
    /// clicks go, the open key run goes when it was typed there, and commands waiting for their match go. Returns how many
    /// open documents were dropped.
    /// </summary>
    public int Forget(string? app, string? host)
    {
        if (app is null && host is null)
        {
            return 0;
        }

        bool Hit(string a, string? h) => (app is not null && Glob.IsMatch(app, a)) || (host is not null && h is not null && RecordSource.HostMatches(host, h));
        DrainKeys(); // keys typed before the forget belong to it
        if (_focus is { } f && Hit(f.App, f.Host))
        {
            _runs.Discard();
        }

        foreach (var (view, owner) in _viewOwners.ToArray())
        {
            if (Hit(owner.App, owner.Host))
            {
                _differ.Forget(view);
                _viewOwners.Remove(view);
            }
        }

        foreach (var (id, p) in _pendingClicks.ToArray())
        {
            if (Hit(p.App, p.Host))
            {
                _pendingClicks.Remove(id);
            }
        }

        Commands.Clear();
        return Documents.Discard(app, host);
    }

    /// <summary>C3.4 / C21.4 forget by time: the open run, every view's baseline, waiting clicks and commands, and the open documents that reach into the range go.</summary>
    public int Forget(DateTimeOffset since)
    {
        DrainKeys();
        _runs.Discard();
        _differ.Clear();
        _viewOwners.Clear();
        _pendingClicks.Clear();
        Commands.Clear();
        return Documents.DiscardSince(since);
    }

    /// <summary>
    /// The foreground activity ended (C's episode boundary): flush the open run, emit volatile refs' last values, and
    /// record the track-1 document. Side tracks keep their own documents until they close.
    /// </summary>
    public void CloseActivity()
    {
        var now = _now();
        DrainKeys();
        _runs.Flush(_nowMs());
        foreach (var d in _differ.CloseActivity(now))
        {
            // Recorded under the view's own app and site, never the focused one: an excluded app's view stays excluded.
            if (!_viewOwners.TryGetValue(d.View, out var owner))
            {
                continue;
            }

            var clean = Scrub(d);
            if (_recorder.View(clean, owner.App, owner.Host))
            {
                Documents.Add(TrackOf(owner.App), clean);
                Documents.Note(TrackOf(owner.App), owner.App, owner.Host);
            }
        }

        Record(Documents.Close(1, now));
    }

    /// <summary>Discard pointer input stamped no later than the most recent secure-gate reopening.</summary>
    public void PointerBoundary(long nowMs)
    {
        _pointerAfter = nowMs;
        _pendingClicks.Clear(); // a target asked for before the gate closed never lands after it
    }

    private void Pointer(RawInput input)
    {
        if (_focus is not { } focus || _recorder.Secure || input.TimeMs <= _pointerAfter) return;
        if (_pointer.Process(input) is not { } pointer) return;
        if (!_recorder.Pointer(pointer, focus.App, focus.Host)) return;
        var track = TrackOf(focus.App);
        var at = _now() - TimeSpan.FromMilliseconds(Math.Max(0, _nowMs() - pointer.TimeMs));
        Documents.Add(track, pointer, at);
        Documents.Note(track, focus.App, focus.Host);
        if (pointer.Kind != PointerEventKind.Click || ClickTargetRequested is not { } request) return;

        // Recorded at once; the target is asked for off the loop and joins the click if it comes within the budget.
        if (_pendingClicks.Count >= MaxPendingClicks) _pendingClicks.Remove(_pendingClicks.Keys.Min());
        var id = ++_nextClick;
        _pendingClicks[id] = new PendingClick(pointer, focus.App, focus.Host, track, at, Documents.StartOf(track));
        ClickTargetsAsked++;
        request(id, pointer.X, pointer.Y);
    }

    /// <summary>
    /// The answer to <see cref="ClickTargetRequested"/> (on the loop). Kept only when it arrives within
    /// <see cref="ClickTargetBudgetMs"/> of the click, with the gate open and the click's app and site still unblocked; it is
    /// scrubbed, recorded as the click's <see cref="PointerEventKind.Target"/>, and joins the click's document while that
    /// document is still open. Returns whether it was recorded.
    /// </summary>
    public bool ClickTarget(long id, string? target)
    {
        if (!_pendingClicks.Remove(id, out var p) || target is not { Length: > 0 }) return false;
        if (_recorder.Secure || p.Click.TimeMs <= _pointerAfter || _nowMs() - p.Click.TimeMs > ClickTargetBudgetMs) return false;
        var name = Clean(target.Length > ClickEvent.MaxTarget ? target[..ClickEvent.MaxTarget] : target)!;
        var resolved = p.Click with { Kind = PointerEventKind.Target, Target = name };
        if (!_recorder.Pointer(resolved, p.App, p.Host)) return false;
        ClickTargetsKept++;
        if (p.DocStart is { } start && Documents.StartOf(p.Track) == start)
        {
            Documents.Add(p.Track, new ClickEvent(p.At, p.Click.Button, p.Click.X, p.Click.Y, name, new Actor(ActorKind.Unknown, null, ActorEvidence.None)));
            Documents.Note(p.Track, p.App, p.Host);
        }

        return true;
    }

    private void ExpireClicks(long nowMs)
    {
        if (_pendingClicks.Count == 0) return;
        foreach (var (id, p) in _pendingClicks.ToArray())
        {
            if (nowMs - p.Click.TimeMs > ClickTargetBudgetMs) _pendingClicks.Remove(id);
        }
    }

    private void OnRun(KeyRun run)
    {
        if (_focus is { } f && _recorder.KeyRun(run, f.App, f.Host))
        {
            Documents.Add(TrackOf(f.App), run, _now());
            Documents.Note(TrackOf(f.App), f.App, f.Host);
            if (run.End == RunEnd.Enter && run.Text is { Length: > 0 } text && run.Suppressed == Suppression.None && CommandReconciler.IsTerminal(f.App))
            {
                Commands.Typed(f.App, Sensitive.Of(text), _now(), run.Actor);
            }
        }
    }

    private void Apply(IReadOnlyList<TrackChange> changes)
    {
        foreach (var c in changes)
        {
            if (!c.Opened)
            {
                Record(Documents.Close(c.Track, c.At));
            }
        }
    }

    private void Record(ActivityDocument? doc)
    {
        if (doc is not null && _recorder.Document(doc))
        {
            DocumentsWritten++;
        }
    }

    /// <summary>The side track an app is on, else track 1.</summary>
    private int TrackOf(string app) => TrackFor(app) ?? 1;

    /// <summary>The app's side track, 1 when it is in front, null when it is neither (yet).</summary>
    private int? TrackFor(string app)
    {
        foreach (var (track, a) in Tracks.SideTracks)
        {
            if (string.Equals(a, app, StringComparison.OrdinalIgnoreCase))
            {
                return track;
            }
        }

        return Tracks.Foreground is null || string.Equals(Tracks.Foreground, app, StringComparison.OrdinalIgnoreCase) ? 1 : null;
    }

    private ViewDiff Scrub(ViewDiff d) => d with
    {
        Added = [.. d.Added.Select(l => l with { Name = Clean(l.Name), Value = Clean(l.Value) })],
    };

    private string? Clean(string? text) => text is null ? null : _redactor.Scrub(text).Text;
}



