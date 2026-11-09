using System.Text;

namespace Tasktor.Capture.Deep;

/// <summary>Where keys are going right now: the view (window, root ref), the focused element and its sensitivity.</summary>
public sealed record FocusTarget(string View, string ElementRef, string App)
{
    /// <summary>UIA password state; null is unknown and fails closed.</summary>
    public bool? IsPassword { get; init; }

    /// <summary>The extension's field kind: <c>type=password</c>, <c>autocomplete=cc-*</c>, <c>one-time-code</c>, <c>current-password</c>.</summary>
    public bool SecureField { get; init; }

    /// <summary>The app declared the content sensitive (UserActivity ContentInfo).</summary>
    public bool Sensitive { get; init; }

    public string? Host { get; init; }

    public string? Tab { get; init; }
}

/// <summary>
/// D-C19 key runs: keys drained from the ring are grouped by (view, focused element); a run ends at a 3 s pause, a focus
/// change, Enter or Tab. Edits are applied (Backspace removes the last character), chords are kept apart (≤ 16).
/// Suppression is decided when the run opens and on every focus change; a suppressed run keeps only its key count and
/// duration. When the focused field's value is known at close, the value diff is the truth (<see cref="Close"/>).
/// Closed runs' text passes the <see cref="SecretRedactor"/> before they leave. Loop-only.
/// </summary>
public sealed class KeyRunBuilder
{
    public static readonly TimeSpan Pause = TimeSpan.FromSeconds(3);
    public const int MaxChords = 16;
    public const int MaxText = 4096;

    /// <summary>Recent closed runs kept for inspection; older ones live only in the vault (the host runs for weeks).</summary>
    public const int KeepClosed = 64;

    private readonly KeyDecoder _decoder;
    private readonly ActorAttributor _actors;
    private readonly SecretRedactor _redactor;
    private readonly Func<FocusTarget, DateTimeOffset, Suppression> _suppress;
    private readonly Func<long, DateTimeOffset> _wallAt;
    private readonly List<KeyRun> _closed = [];
    private FocusTarget? _focus;
    private Open? _open;
    private long _nextId = 1;

    /// <param name="wallAt">Map a monotonic event tick to its UTC wall time; never interpret ticks as Unix time.</param>
    public KeyRunBuilder(KeyDecoder decoder, ActorAttributor actors, SecretRedactor redactor, Func<FocusTarget, DateTimeOffset, Suppression> suppress, Func<long, DateTimeOffset> wallAt)
    {
        _decoder = decoder;
        _actors = actors;
        _redactor = redactor;
        _suppress = suppress;
        _wallAt = wallAt ?? throw new ArgumentNullException(nameof(wallAt));
    }

    /// <summary>Raised for each closed run (text already redacted).</summary>
    public event Action<KeyRun>? RunClosed;

    public IReadOnlyList<KeyRun> Closed => _closed;

    /// <summary>The time of the last Enter-terminated run per view (for D-C17's <c>cli</c> label).</summary>
    public Dictionary<string, long> LastEnter { get; } = new(StringComparer.Ordinal);

    private sealed class Open
    {
        public required FocusTarget Target { get; init; }

        public required long Start { get; init; }

        public long Last { get; set; }

        public Suppression Suppressed { get; set; }

        public StringBuilder Text { get; } = new();

        public int Keys { get; set; }

        public int Backspaces { get; set; }

        public List<string> Chords { get; } = [];

        public List<Actor> Actors { get; } = [];
    }

    public void Focus(FocusTarget target, long timeMs)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (_open is { } o && (o.Target.View != target.View || o.Target.ElementRef != target.ElementRef))
        {
            Close(RunEnd.FocusOut, timeMs);
        }

        _focus = target;
        if (_open is { } still)
        {
            still.Suppressed = Max(still.Suppressed, _suppress(target, _wallAt(timeMs)));
            if (still.Suppressed != Suppression.None)
            {
                still.Text.Clear();
                still.Chords.Clear();
            }
        }
    }

    /// <summary>Feed one raw key event (drained from the ring in order).</summary>
    public void Key(RawInput raw)
    {
        Tick(raw.TimeMs);
        var key = _decoder.Decode(raw);
        if (key.Action == KeyAction.None || _focus is null)
        {
            return;
        }

        var open = _open ??= new Open
        {
            Target = _focus,
            Start = key.TimeMs,
            Suppressed = _suppress(_focus, _wallAt(key.TimeMs)),
        };
        open.Last = key.TimeMs;
        open.Keys++;
        open.Actors.Add(_actors.ForKey(key));
        switch (key.Action)
        {
            case KeyAction.Uncertain:
                open.Suppressed = Max(open.Suppressed, Suppression.UncertainText);
                open.Text.Clear();
                open.Chords.Clear();
                break;
            case KeyAction.Text when open.Suppressed == Suppression.None && open.Text.Length < MaxText:
                open.Text.Append(key.Text);
                break;
            case KeyAction.Backspace:
                open.Backspaces++;
                if (open.Text.Length > 0)
                {
                    open.Text.Length--;
                }

                break;
            case KeyAction.Chord when open.Suppressed == Suppression.None && open.Chords.Count < MaxChords:
                open.Chords.Add(key.Text!);
                break;
            case KeyAction.Enter:
                Close(RunEnd.Enter, key.TimeMs);
                break;
            case KeyAction.Tab:
                Close(RunEnd.Tab, key.TimeMs);
                break;
        }
    }

    /// <summary>Time passes: a run idle for 3 s closes at its last key.</summary>
    public void Tick(long nowMs)
    {
        if (_open is { } o && nowMs - o.Last >= Pause.TotalMilliseconds)
        {
            Close(RunEnd.Pause, o.Last);
        }
    }

    /// <summary>Drop the open run without recording it (C21.4 forget).</summary>
    public void Discard() => _open = null;

    public void Flush(long nowMs)
    {
        if (_open is not null)
        {
            Close(RunEnd.Flush, nowMs);
        }
    }

    /// <summary>
    /// Close the open run. With <paramref name="fieldBefore"/> / <paramref name="fieldAfter"/> (the focused field's value at
    /// open and now), the inserted text of the value diff replaces the key-derived text: the field is the truth (paste,
    /// autocomplete and IME all land there).
    /// </summary>
    public KeyRun? Close(RunEnd end, long timeMs, string? fieldBefore = null, string? fieldAfter = null)
    {
        if (_open is not { } o)
        {
            return null;
        }

        _open = null;
        string? text = null;
        var hits = new Dictionary<string, int>();
        if (o.Suppressed == Suppression.None)
        {
            var raw = fieldAfter is not null ? Inserted(fieldBefore ?? string.Empty, fieldAfter) : o.Text.ToString();
            (text, hits) = _redactor.Scrub(raw);
        }

        var run = new KeyRun
        {
            Id = _nextId++,
            View = o.Target.View,
            ElementRef = o.Target.ElementRef,
            StartMs = o.Start,
            EndMs = Math.Max(timeMs, o.Last),
            Text = text,
            Keys = o.Keys,
            Backspaces = o.Suppressed == Suppression.None ? o.Backspaces : 0,
            Chords = o.Suppressed == Suppression.None ? [.. o.Chords] : [],
            End = end,
            Actor = ActorAttributor.ForRun(o.Actors),
            Suppressed = o.Suppressed,
            Redactions = hits,
        };
        if (end == RunEnd.Enter)
        {
            LastEnter[o.Target.View] = run.EndMs;
        }

        if (_closed.Count >= KeepClosed)
        {
            _closed.RemoveAt(0);
        }

        _closed.Add(run);
        RunClosed?.Invoke(run);
        return run;
    }

    /// <summary>The text inserted between two values of a field (common prefix and suffix removed).</summary>
    public static string Inserted(string before, string after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        return FieldTruth.Edit(before, after).Inserted;
    }

    private static Suppression Max(Suppression a, Suppression b) => a != Suppression.None ? a : b;
}

/// <summary>
/// D-C19 / D-C22 suppression, checked when a run opens and on every focus change: password and secure fields, the
/// secure desktop, sensitive content, Tasktor's own windows, excluded apps and hosts, and active capture blocks.
/// </summary>
public sealed class Suppressor
{
    public static readonly string[] OwnApps = ["tasktor.flyout.exe", "tasktor.widget.exe", "tasktor-shell.exe", "tasktor-core.exe", "tasktor-capture.exe", "tasktor-nmh.exe", "tasktor.exe"];

    private readonly CaptureBlocks _blocks;

    public Suppressor(CaptureBlocks blocks) => _blocks = blocks;

    /// <summary>True while Winlogon / UAC's secure desktop is the input desktop.</summary>
    public Func<bool> SecureDesktop { get; set; } = () => true;

    public Suppression Check(FocusTarget t, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(t);
        if (SecureDesktop())
        {
            return Suppression.SecureDesktop;
        }

        if (t.IsPassword is null) return Suppression.UnknownField;

        if (t.IsPassword is true)
        {
            return Suppression.Password;
        }

        if (t.SecureField)
        {
            return Suppression.SecureField;
        }

        if (t.Sensitive)
        {
            return Suppression.Sensitive;
        }

        if (OwnApps.Contains(t.App, StringComparer.OrdinalIgnoreCase))
        {
            return Suppression.OwnWindow;
        }

        return _blocks.Blocking(now, t.App, t.Host, t.Tab) switch
        {
            null => Suppression.None,
            BlockKind.App or BlockKind.Host => Suppression.Excluded,
            _ => Suppression.Blocked,
        };
    }
}






