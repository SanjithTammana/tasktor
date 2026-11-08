using Tasktor.Capture.Deep;
using Tasktor.Contracts.Platform;

namespace Tasktor.Capture.Commands;

/// <summary>Where a logged command came from.</summary>
public enum CommandSource : byte
{
    /// <summary>An Enter-terminated key run in a terminal, no history line (cmd, bash, or PSReadLine off).</summary>
    Typed,

    /// <summary>A history line with no key run seen (the keyboard hook off, or the command was pasted).</summary>
    History,

    /// <summary>Both, within <see cref="CommandReconciler.Window"/>: the history text wins (it is the final edited line).</summary>
    Both,
}

/// <summary>One command line: when, the terminal app it was typed in (null when only history saw it), the text, who.</summary>
public sealed record CommandEvent(DateTimeOffset At, string? App, Sensitive<string> Text, CommandSource Source, Actor Actor);

/// <summary>
/// C22.1: typed commands are Enter-terminated key runs in terminal windows, reconciled with PSReadLine history lines
/// appended within one second, which win when present (history has the line as run, after edits, recalls and
/// completion). Either alone is still a command. Each is held for <see cref="Window"/> waiting for its partner, then
/// released in time order. Logic-loop only (no locks).
/// </summary>
public sealed class CommandReconciler
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    /// <summary>Held entries beyond this are released early (a burst of history must not grow without bound).</summary>
    public const int MaxHeld = 256;

    /// <summary>Terminal hosts and shells whose Enter-terminated runs count as commands (C22; editors do not).</summary>
    public static readonly string[] Terminals =
        [.. ActorAttributor.Shells, "windowsterminal.exe", "openconsole.exe", "conhost.exe", "wezterm-gui.exe", "alacritty.exe", "mintty.exe"];

    private static readonly Actor Unseen = new(ActorKind.Unknown, null, ActorEvidence.None);

    private readonly List<Held> _typed = [];
    private readonly List<Held> _history = [];

    public static bool IsTerminal(string app) => Terminals.Contains(app, StringComparer.OrdinalIgnoreCase);

    public int Pending => _typed.Count + _history.Count;

    /// <summary>Discard pending commands when their capture boundary becomes uncertain.</summary>
    public void Clear() { _typed.Clear(); _history.Clear(); }

    public void Typed(string app, Sensitive<string> text, DateTimeOffset at, Actor actor)
    {
        var h = new Held(at, app, text, actor);
        if (_history.FindIndex(x => x.Partner is null && Near(x.At, at)) is var i and >= 0)
        {
            (_history[i].Partner, h.Partner) = (h, _history[i]);
        }

        _typed.Add(h);
    }

    public void History(Sensitive<string> text, DateTimeOffset at)
    {
        var h = new Held(at, null, text, Unseen);
        if (_typed.FindIndex(x => x.Partner is null && Near(x.At, at)) is var i and >= 0)
        {
            (_typed[i].Partner, h.Partner) = (h, _typed[i]);
        }

        _history.Add(h);
    }

    /// <summary>Commands whose partner window has passed by <paramref name="now"/>, in time order.</summary>
    public IReadOnlyList<CommandEvent> Flush(DateTimeOffset now)
    {
        var force = Pending > MaxHeld;
        var due = new List<CommandEvent>();
        foreach (var t in _typed)
        {
            var last = t.Partner is { } p && p.At > t.At ? p.At : t.At;
            if (force || last + Window < now)
            {
                t.Done = true;
                due.Add(t.Partner is { } hist
                    ? new CommandEvent(t.At, t.App, hist.Text, CommandSource.Both, t.Actor)
                    : new CommandEvent(t.At, t.App, t.Text, CommandSource.Typed, t.Actor));
            }
        }

        foreach (var h in _history)
        {
            if (h.Partner is { } p)
            {
                h.Done = p.Done;
            }
            else if (force || h.At + Window < now)
            {
                h.Done = true;
                due.Add(new CommandEvent(h.At, null, h.Text, CommandSource.History, h.Actor));
            }
        }

        _typed.RemoveAll(x => x.Done);
        _history.RemoveAll(x => x.Done);
        return [.. due.OrderBy(e => e.At)];
    }

    private static bool Near(DateTimeOffset a, DateTimeOffset b) => (a - b).Duration() <= Window;

    private sealed class Held(DateTimeOffset at, string? app, Sensitive<string> text, Actor actor)
    {
        public DateTimeOffset At { get; } = at;

        public string? App { get; } = app;

        public Sensitive<string> Text { get; } = text;

        public Actor Actor { get; } = actor;

        public Held? Partner { get; set; }

        public bool Done { get; set; }
    }
}
