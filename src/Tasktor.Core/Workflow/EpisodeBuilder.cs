using Tasktor.Contracts.Workflow;

namespace Tasktor.Core.Workflow;

/// <summary>A closed activity (C4): the unit of clustering. Times are monotonic ms; <see cref="StartUtcMs"/> is wall time.</summary>
public sealed record ActivityRecord
{
    public required int Index { get; init; }

    public required string App { get; init; }

    public required long StartMs { get; init; }

    public required long EndMs { get; init; }

    public required long StartUtcMs { get; init; }

    public int TzMin { get; init; }

    public required long ActiveMs { get; init; }

    public long IdleMs { get; init; }

    public int Glances { get; init; }

    public int TitleChanges { get; init; }

    public string? PrevApp { get; init; }

    public string? NextApp { get; init; }

    public double JobFrac { get; init; }

    /// <summary>Title tokens → ms shown while active (≤ 256 heaviest). Hashed at the store; text stays in memory.</summary>
    public required IReadOnlyList<KeyValuePair<string, long>> Tokens { get; init; }
}

public sealed record WindowRecord(int Index, long StartMs, long EndMs, long ActiveMs, IReadOnlyList<KeyValuePair<int, long>> Dwell);

public sealed record SessionRecord(int Index, long StartMs, long EndMs, IReadOnlyList<int> Activities);

/// <summary>
/// C4 episodes, built online from gated events (a state machine; the Python reference in <c>tasktor_reference</c> mirrors
/// it exactly). Segment ≥ 2 s counts as a glance; another app ≥ 30 s opens a new activity (shorter visits are absorbed,
/// so activities sum to active foreground time); idle ≥ 5 min, lock, suspend, pause, exclude and agent stop close it; a
/// 60 min cap chunks marathons. Workflow windows are 15 min of active time (dropped under 3 min); sessions run between
/// long idles, locks, sleeps and boots. Gaps are never idle. Deterministic: same events → same records.
/// </summary>
public sealed class EpisodeBuilder
{
    private readonly EpisodeThresholds _t;
    private readonly int _maxTokens;
    private long? _cursor;
    private long _offset;
    private int _tz;
    private Fg? _fg;
    private readonly HashSet<CaptureKind> _gaps = [];
    private long? _idleSince;
    private bool _jobOn;
    private Run? _act;
    private Run? _visit;
    private string? _lastApp;
    private int _nextActivity;
    private Win? _win;
    private int _nextWindow;
    private Sess? _sess;
    private int _nextSession;
    private bool _boundary = true;

    public EpisodeBuilder(EpisodeThresholds thresholds, int maxTokens = 256)
    {
        _t = thresholds;
        _maxTokens = maxTokens;
    }

    public List<ActivityRecord> Activities { get; } = [];

    public List<WindowRecord> Windows { get; } = [];

    public List<SessionRecord> Sessions { get; } = [];

    /// <summary>The open activity's app and tokens so far (the online assigner reads it), or null.</summary>
    public (string App, long ActiveMs, IReadOnlyDictionary<string, long> Tokens)? Open => _act is { } a ? (a.App, a.Active, a.Tokens) : null;

    private sealed class Fg
    {
        public required string App { get; init; }

        public int? Win { get; set; }

        public string? Title { get; set; }

        public List<string> Tokens { get; set; } = [];
    }

    private sealed class Run
    {
        public required int Index { get; init; }

        public required string App { get; init; }

        public required long Start { get; init; }

        public long StartUtc { get; init; }

        public int Tz { get; init; }

        public long Active { get; set; }

        public long Idle { get; set; }

        public long Job { get; set; }

        public int Glances { get; set; }

        public int TitleChanges { get; set; }

        public string? Prev { get; init; }

        public Dictionary<string, long> Tokens { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Win
    {
        public required int Index { get; init; }

        public required long Start { get; init; }

        public long Active { get; set; }

        public Dictionary<int, long> Dwell { get; } = [];
    }

    private sealed class Sess
    {
        public required int Index { get; init; }

        public required long Start { get; init; }

        public List<int> Activities { get; } = [];
    }

    public void On(CaptureEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var t = Math.Max(e.MonoMs, _cursor ?? e.MonoMs);
        Accrue(t);
        _offset = e.TsUtcMs - e.MonoMs;
        _tz = e.TzMin;
        switch (e.Kind)
        {
            case CaptureKind.Foreground when e.App is { } app:
                Focus(app, e.Win, e.Title, e.Domain, t);
                break;
            case CaptureKind.Title when _fg is { } fg && e.App is { } app2 && (e.Win == fg.Win || app2 == fg.App):
                SetTitle(fg, e.Title, e.Domain);
                break;
            case CaptureKind.IdleStart when e.Idle != IdleKind.Media:
                _idleSince ??= t;
                break;
            case CaptureKind.IdleEnd:
                EndIdle(t);
                break;
            case CaptureKind.Lock or CaptureKind.Suspend or CaptureKind.AgentStop or CaptureKind.Pause or CaptureKind.ExcludedStart:
                StartGap(e.Kind, t);
                break;
            case CaptureKind.Unlock:
                _gaps.Remove(CaptureKind.Lock);
                break;
            case CaptureKind.Resume:
                _gaps.Remove(CaptureKind.Suspend);
                break;
            case CaptureKind.AgentStart:
                _gaps.Remove(CaptureKind.AgentStop);
                break;
            case CaptureKind.Unpause:
                _gaps.Remove(CaptureKind.Pause);
                break;
            case CaptureKind.ExcludedEnd:
                _gaps.Remove(CaptureKind.ExcludedStart);
                break;
            case CaptureKind.JobOn:
                _jobOn = true;
                break;
            case CaptureKind.JobOff:
                _jobOn = false;
                break;
        }
    }

    /// <summary>Advance time without an event (the 60 s tick): thresholds that depend only on elapsed time apply.</summary>
    public void Tick(long monoMs) => Accrue(Math.Max(monoMs, _cursor ?? monoMs));

    /// <summary>Close everything at <paramref name="monoMs"/> (agent stop, end of a fixture stream).</summary>
    public void Flush(long monoMs)
    {
        var t = Math.Max(monoMs, _cursor ?? monoMs);
        Accrue(t);
        CloseVisitAsGlance();
        CloseActivity(t, null);
        CloseWindow(t);
        CloseSession(t);
    }

    private void Accrue(long t)
    {
        if (_cursor is not { } from)
        {
            _cursor = t;
            return;
        }

        _cursor = t;
        if (t <= from || _gaps.Count > 0 || _fg is null)
        {
            CheckIdle(t);
            return;
        }

        if (_idleSince is { } idle)
        {
            // Input idle: credited as idle inside the activity unless it reaches the split threshold.
            if (t - idle >= _t.IdleSplitMs)
            {
                SplitAtIdle(idle);
            }

            return;
        }

        Credit(from, t);
    }

    private void CheckIdle(long t)
    {
        if (_idleSince is { } idle && t - idle >= _t.IdleSplitMs)
        {
            SplitAtIdle(idle);
        }
    }

    private void SplitAtIdle(long idleStart)
    {
        CloseVisitAsGlance();
        CloseActivity(idleStart, null);
        CloseWindow(idleStart);
        CloseSession(idleStart);
        _boundary = true;
    }

    private void EndIdle(long t)
    {
        if (_idleSince is { } idle)
        {
            if (t - idle < _t.IdleSplitMs && _act is { } a)
            {
                a.Idle += t - idle;
            }

            _idleSince = null;
        }
    }

    /// <summary>Credit active foreground time [from, to) to the open activity or the pending visit, splitting at caps.</summary>
    private void Credit(long from, long to)
    {
        var at = from;
        while (at < to)
        {
            var fg = _fg!;
            if (_act is null)
            {
                OpenActivity(fg.App, at, null);
            }

            if (_act!.App == fg.App && _visit is null)
            {
                var room = Math.Min(_t.ActivityCapMs - _act.Active, WindowRoom());
                var step = Math.Min(to - at, room);
                Add(_act, fg, step);
                CreditWindow(_act.Index, step, at);
                at += step;
                if (_act.Active >= _t.ActivityCapMs)
                {
                    var app = _act.App;
                    CloseActivity(at, null);
                    OpenActivity(app, at, app);
                }

                if (_win is { } w && w.Active >= _t.WindowActiveMs)
                {
                    CloseWindow(at);
                }

                continue;
            }

            // A visit to another app: its own counters, promoted when it reaches the glance threshold.
            _visit ??= NewRun(fg.App, at, _act.App);
            var need = _t.GlanceMaxMs - _visit.Active;
            var part = Math.Min(to - at, need);
            Add(_visit, fg, part);
            at += part;
            if (_visit.Active >= _t.GlanceMaxMs)
            {
                Promote();
            }
        }
    }

    private long WindowRoom() => _win is { } w ? _t.WindowActiveMs - w.Active : _t.WindowActiveMs;

    private void Add(Run r, Fg fg, long ms)
    {
        r.Active += ms;
        if (_jobOn)
        {
            r.Job += ms;
        }

        foreach (var tok in fg.Tokens)
        {
            r.Tokens[tok] = r.Tokens.GetValueOrDefault(tok) + ms;
        }
    }

    private void CreditWindow(int activity, long ms, long start)
    {
        if (ms <= 0)
        {
            return;
        }

        _win ??= new Win { Index = _nextWindow++, Start = start };
        _win.Active += ms;
        _win.Dwell[activity] = _win.Dwell.GetValueOrDefault(activity) + ms;
    }

    private Run NewRun(string app, long start, string? prev) => new() { Index = -1, App = app, Start = start, Prev = prev, StartUtc = start + _offset, Tz = _tz };

    private void OpenActivity(string app, long start, string? prev)
    {
        _act = new Run { Index = _nextActivity++, App = app, Start = start, Prev = prev ?? _lastApp, StartUtc = start + _offset, Tz = _tz };
        if (_boundary || _sess is null)
        {
            CloseSession(start);
            _sess = new Sess { Index = _nextSession++, Start = start };
            _boundary = false;
        }

        _sess.Activities.Add(_act.Index);
    }

    /// <summary>The visit reached 30 s: the open activity ends where the visit began; the visit becomes the activity.</summary>
    private void Promote()
    {
        var v = _visit!;
        _visit = null;
        CloseActivity(v.Start, v.App);
        OpenActivity(v.App, v.Start, null);
        var a = _act!;
        a.Active = v.Active;
        a.Job = v.Job;
        a.TitleChanges = v.TitleChanges;
        foreach (var kv in v.Tokens)
        {
            a.Tokens[kv.Key] = kv.Value;
        }

        var remaining = v.Active;
        while (remaining > 0)
        {
            var step = Math.Min(remaining, WindowRoom());
            CreditWindow(a.Index, step, _cursor!.Value - remaining);
            remaining -= step;
            if (_win is { } w && w.Active >= _t.WindowActiveMs)
            {
                CloseWindow(_cursor!.Value - remaining);
            }
        }
    }

    /// <summary>A visit shorter than 30 s is absorbed: its time is the activity's; ≥ 2 s counts as a glance.</summary>
    private void CloseVisitAsGlance()
    {
        if (_visit is not { } v || _act is not { } a)
        {
            _visit = null;
            return;
        }

        _visit = null;
        if (v.Active >= _t.SegmentMinMs)
        {
            a.Glances++;
        }

        a.Active += v.Active;
        a.Job += v.Job;
        var remaining = v.Active;
        while (remaining > 0)
        {
            var step = Math.Min(remaining, WindowRoom());
            CreditWindow(a.Index, step, _cursor!.Value - remaining);
            remaining -= step;
            if (_win is { } w && w.Active >= _t.WindowActiveMs)
            {
                CloseWindow(_cursor!.Value - remaining);
            }
        }
    }

    private void Focus(string app, int? win, string? title, string? domain, long t)
    {
        if (_visit is { } v && v.App != app)
        {
            CloseVisitAsGlance(); // back to the activity's app, or on to a third app before 30 s
        }

        _fg = new Fg { App = app, Win = win, Title = title, Tokens = Distinct(Tokenizer.Tokens(title, app, domain)) };
    }

    private void SetTitle(Fg fg, string? title, string? domain)
    {
        if (fg.Title == title)
        {
            return;
        }

        fg.Title = title;
        fg.Tokens = Distinct(Tokenizer.Tokens(title, fg.App, domain));
        var target = _visit ?? (_act is { } a && a.App == fg.App ? a : null);
        if (target is not null)
        {
            target.TitleChanges++;
        }
    }

    private static List<string> Distinct(List<string> tokens)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return [.. tokens.Where(seen.Add)];
    }

    private void StartGap(CaptureKind kind, long t)
    {
        CloseVisitAsGlance();
        CloseActivity(t, null);
        CloseWindow(t);
        if (kind is CaptureKind.Lock or CaptureKind.Suspend or CaptureKind.AgentStop)
        {
            CloseSession(t);
            _boundary = true;
        }

        _gaps.Add(kind);
        _fg = null;
        _idleSince = null;
    }

    private void CloseActivity(long end, string? next)
    {
        if (_act is not { } a)
        {
            return;
        }

        _act = null;
        _lastApp = a.App;
        if (a.Active <= 0)
        {
            _sess?.Activities.Remove(a.Index);
            return;
        }

        Activities.Add(new ActivityRecord
        {
            Index = a.Index,
            App = a.App,
            StartMs = a.Start,
            EndMs = end,
            StartUtcMs = a.StartUtc,
            TzMin = a.Tz,
            ActiveMs = a.Active,
            IdleMs = a.Idle,
            Glances = a.Glances,
            TitleChanges = a.TitleChanges,
            PrevApp = a.Prev,
            NextApp = next,
            JobFrac = a.Active == 0 ? 0 : Math.Round((double)a.Job / a.Active, 6),
            Tokens = [.. a.Tokens.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(_maxTokens)],
        });
    }

    private void CloseWindow(long end)
    {
        if (_win is not { } w)
        {
            return;
        }

        _win = null;
        if (w.Active >= _t.WindowMinActiveMs)
        {
            Windows.Add(new WindowRecord(w.Index, w.Start, end, w.Active, [.. w.Dwell.OrderBy(kv => kv.Key)]));
        }
    }

    private void CloseSession(long end)
    {
        if (_sess is not { } s)
        {
            return;
        }

        _sess = null;
        if (s.Activities.Count > 0)
        {
            Sessions.Add(new SessionRecord(s.Index, s.Start, end, [.. s.Activities]));
        }
    }
}
