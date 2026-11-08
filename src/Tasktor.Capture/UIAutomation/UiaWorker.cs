using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Tasktor.Capture.Deep;
using Tasktor.Contracts.Platform;

namespace Tasktor.Capture.UIAutomation;

/// <summary>Focus moved to <see cref="Element"/> (its password state and identity cached) in top-level window <see cref="Window"/>.</summary>
public sealed record UiaFocusChange(long Window, IUiaWalkNode Element);

/// <summary>
/// The UIA backend seam (C §4 <c>UiaReader</c>): one client on its own MTA thread. Its events may fire on any thread and
/// must only be queued. <see cref="Watch"/> and <see cref="Root"/> run on the worker thread; <see cref="HitTest"/> on
/// any MTA thread. Every call is bounded by UIA's 1 s transaction timeout.
/// </summary>
public interface IUiaClient : IDisposable
{
    /// <summary>A watched window's content changed (structure, text, names).</summary>
    event Action<long>? WindowDirty;

    event Action<UiaFocusChange>? FocusChanged;

    /// <summary>Move the handler group to <paramref name="window"/>'s root (0: watch nothing).</summary>
    void Watch(long window);

    /// <summary>
    /// A cached subtree of the window's root (off-screen subtrees pruned; the walk caps depth and count), or null. Text
    /// bodies (<see cref="IUiaWalkNode.ReadVisibleText"/>) answer only when <paramref name="textBodies"/> is set.
    /// </summary>
    IUiaWalkNode? Root(long window, bool textBodies);

    /// <summary>"role: name" of the element at a screen point, or null.</summary>
    string? HitTest(int x, int y);
}

/// <summary>
/// C §5.4 scheduling, per window: a walk is due <see cref="DebounceMs"/> after the last change (at most
/// <see cref="MaxDelayMs"/> after the first), costs a token (burst <see cref="Burst"/>, one a second), and a window whose
/// walk timed out is skipped for <see cref="HungSkipMs"/>.
/// </summary>
public sealed class UiaScheduler
{
    public const long DebounceMs = 750;
    public const long MaxDelayMs = 2000;
    public const double Burst = 5;
    public const long RefillMs = 1000;
    public const long HungSkipMs = 60_000;
    public const int MaxWindows = 64;

    private sealed class State
    {
        public long? First, Last;
        public double Tokens = Burst;
        public long RefilledAt;
        public long HungUntil = long.MinValue;
    }

    private readonly Dictionary<long, State> _windows = [];

    public long? NextDueMs
    {
        get
        {
            long? next = null;
            foreach (var s in _windows.Values)
            {
                if (Due(s) is { } d && (next is null || d < next)) next = d;
            }

            return next;
        }
    }

    public void Dirty(long window, long nowMs)
    {
        var s = Get(window, nowMs);
        if (nowMs < s.HungUntil) return;
        s.First ??= nowMs;
        s.Last = nowMs;
    }

    public void Hung(long window, long nowMs)
    {
        var s = Get(window, nowMs);
        s.HungUntil = nowMs + HungSkipMs;
        s.First = s.Last = null;
    }

    public double Tokens(long window, long nowMs)
    {
        var s = Get(window, nowMs);
        Refill(s, nowMs);
        return s.Tokens;
    }

    /// <summary>The windows to walk now (each takes a token); one without a token waits for the next.</summary>
    public IReadOnlyList<long> Due(long nowMs)
    {
        List<long>? due = null;
        foreach (var (window, s) in _windows)
        {
            if (Due(s) is not { } d || nowMs < d) continue;
            Refill(s, nowMs);
            if (s.Tokens < 1)
            {
                s.Last = s.First = nowMs + (long)((1 - s.Tokens) * RefillMs) - DebounceMs; // due once a token is back
                continue;
            }

            s.Tokens -= 1;
            s.First = s.Last = null;
            (due ??= []).Add(window);
        }

        return due ?? (IReadOnlyList<long>)[];
    }

    private static long? Due(State s) => s.First is { } f && s.Last is { } l ? Math.Min(l + DebounceMs, f + MaxDelayMs) : null;

    private static void Refill(State s, long nowMs)
    {
        if (nowMs <= s.RefilledAt) return;
        s.Tokens = Math.Min(Burst, s.Tokens + ((nowMs - s.RefilledAt) / (double)RefillMs));
        s.RefilledAt = nowMs;
    }

    private State Get(long window, long nowMs)
    {
        if (_windows.TryGetValue(window, out var s)) return s;
        if (_windows.Count >= MaxWindows)
        {
            foreach (var (w, old) in _windows)
            {
                if (old.First is null && nowMs >= old.HungUntil) { _windows.Remove(w); break; }
            }
        }

        return _windows[window] = new State { RefilledAt = nowMs };
    }
}

/// <summary>
/// The UIA worker's logic (C18.1 / C17.2), single-threaded on the UIA thread. Focus: the field's identity and password
/// state come cached with the event; its value is read only when it is explicitly not a password and its app is not
/// blocked, and the previous field's final value likewise (C17.2's value diff). The watch follows the focused window.
/// Walks: as <see cref="UiaScheduler"/> allows; a walk over <see cref="TimeoutMs"/> is abandoned (nothing posted) and its
/// window skipped for a minute. Nothing here is logged.
/// </summary>
public sealed class UiaWorker(IUiaClient client, Func<long, string?> appOf, Func<string, bool> blocked, Action<Sensitive<UiaWalk>, string> postWalk,
    Action<FocusTarget, Sensitive<string>?, Sensitive<string>?> postFocus, Func<long> nowMs)
{
    public const long TimeoutMs = 1000;

    /// <summary>D-C18: text bodies are read only after this much dwell on a window.</summary>
    public const long TextDwellMs = 2000;

    private readonly UiaScheduler _scheduler = new();
    private readonly Dictionary<long, string?> _apps = [];
    private long _watched;
    private long _watchedSince;
    private bool _textWalked;
    private IUiaWalkNode? _focused;
    private bool _focusedReadable;

    public long Walks { get; private set; }

    public long TimedOut { get; private set; }

    /// <summary>Walk durations (ms) of the last 256 walks, for the p95 check.</summary>
    public Queue<long> Durations { get; } = new();

    public long? NextDueMs => _scheduler.NextDueMs;

    public void OnFocus(UiaFocusChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var app = App(change.Window);
        var e = change.Element;
        Sensitive<string>? previous = _focusedReadable ? Read(_focused) : null;
        var readable = e.Password == false && app is not null && !blocked(app);
        var name = UiaWalkLogic.Cap(e.Name?.Reveal(), UiaWalkLogic.MaxNameChars);
        var id = UiaWalkLogic.Cap(e.AutomationId?.Reveal(), UiaWalkLogic.MaxAutomationIdChars);
        var target = new FocusTarget(View(change.Window), RefMinter.Ref(0, id, e.Role, name, 0).ToString("x", CultureInfo.InvariantCulture), app ?? string.Empty)
        {
            IsPassword = e.Password,
        };
        postFocus(target, previous, readable ? Read(e) : null);
        (_focused, _focusedReadable) = (e, readable);
        if (change.Window != _watched)
        {
            _watched = change.Window;
            _watchedSince = nowMs();
            _textWalked = false;
            client.Watch(change.Window);
        }

        _scheduler.Dirty(change.Window, nowMs());
    }

    public void OnDirty(long window)
    {
        if (window == _watched && window != 0) _scheduler.Dirty(window, nowMs());
    }

    /// <summary>Run the walks that are due; returns when the next one is.</summary>
    public long? Step(long now)
    {
        if (_watched != 0 && !_textWalked && now - _watchedSince >= TextDwellMs)
        {
            _textWalked = true;
            _scheduler.Dirty(_watched, now - UiaScheduler.DebounceMs); // the dwell passed: walk once more with text bodies
        }

        foreach (var window in _scheduler.Due(now))
        {
            if (App(window) is not { } app || blocked(app)) continue; // an excluded app is never read
            var start = nowMs();
            Sensitive<UiaWalk>? walk = null;
            try
            {
                var text = window == _watched && now - _watchedSince >= TextDwellMs;
                if (client.Root(window, text) is { } root) walk = UiaWalkLogic.TryWalk(new ViewKey(window, 0), root);
            }
            catch (Exception)
            {
                walk = null; // a failed provider: nothing partial
            }

            var took = nowMs() - start;
            Walks++;
            Durations.Enqueue(took);
            if (Durations.Count > 256) Durations.Dequeue();
            if (took > TimeoutMs)
            {
                TimedOut++;
                _scheduler.Hung(window, nowMs());
                continue;
            }

            if (walk is { } w) postWalk(w, app);
        }

        return _watched != 0 && !_textWalked ? Min(_scheduler.NextDueMs, _watchedSince + TextDwellMs) : _scheduler.NextDueMs;
    }

    private static long? Min(long? a, long b) => a is { } x && x < b ? x : b;

    [SuppressMessage("Design", "CA1031", Justification = "A provider may throw anything; a failed read is no value.")]
    private static Sensitive<string>? Read(IUiaWalkNode? node)
    {
        try
        {
            return node?.Password == false ? node.ReadValue(FieldTruth.MaxChars * 3) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private string? App(long window)
    {
        if (_apps.TryGetValue(window, out var app)) return app;
        if (_apps.Count > 256) _apps.Clear();
        return _apps[window] = appOf(window);
    }

    private static string View(long window) => window.ToString("x", CultureInfo.InvariantCulture);
}
