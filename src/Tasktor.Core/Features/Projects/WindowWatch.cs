using System.Globalization;
using System.Text.Json;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Workflow;
using Tasktor.Core.Apps;
using Tasktor.Core.Rules;

namespace Tasktor.Core.Features.Projects;

/// <summary>An I11 watch: the app (exe glob) and a title pattern the user wrote (glob, case-insensitive).</summary>
public sealed record WindowWatchSpec(string App, string TitlePattern);

/// <summary>
/// I11 Required-window watch (Module <c>windows_watch</c>): warns when a window the user chose disappears. Window events
/// come from <c>WinEventSource</c>, started only while a watch exists (lease-driven, not F's toggle). A watched window
/// absent for 30 s raises <c>window.missing</c> once, with only the app and the user's own pattern as fields; other
/// windows' titles are matched in memory and never kept. It re-arms when the window is back.
/// </summary>
public sealed class WindowWatch : Feature
{
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    private readonly IEventSource<WindowEvent>? _source;
    private readonly List<RuleEvent> _pending = [];
    private Watch[] _watches = [];

    public WindowWatch(FeatureContext context, IEventSource<WindowEvent>? source)
        : base(context) => _source = source;

    public override string Id => "windows_watch";

    public override FeatureTier Tier => FeatureTier.Module;

    /// <summary>The user's watches (user-authored patterns, kept in the rules / settings file).</summary>
    public Func<IReadOnlyList<WindowWatchSpec>> Watches { get; set; } = () => [];

    public bool Listening { get; private set; }

    private sealed class Watch(WindowWatchSpec spec)
    {
        public WindowWatchSpec Spec { get; } = spec;

        public nint? Hwnd { get; set; }

        public bool Seen { get; set; }

        public DateTimeOffset? AbsentSince { get; set; }

        public bool Announced { get; set; }

        public IDisposable? Wake { get; set; }
    }

    public IReadOnlyList<(WindowWatchSpec Spec, bool Missing)> Status =>
        [.. _watches.Select(w => (w.Spec, w.Seen && w.AbsentSince is { } since && Context.Clock.UtcNow - since >= Grace))];

    protected override void OnEnable()
    {
        Reload();
        if (_watches.Length > 0 && _source is not null)
        {
            _source.Start(e => Context.Loop.Post(() => On(e)));
            Listening = true;
        }
    }

    protected override void OnDisable()
    {
        if (Listening)
        {
            _source?.Stop();
            Listening = false;
        }

        foreach (var w in _watches)
        {
            w.Wake?.Dispose();
        }

        _watches = [];
    }

    public void Reload() => _watches = [.. Watches().Select(s => new Watch(s))];

    private static bool Matches(WindowWatchSpec s, string? exe, string? title) =>
        exe is not null && title is not null && Glob.IsMatch(s.App, exe) && Glob.IsMatch(s.TitlePattern, title);

    /// <summary>One window event (foreground or title change); the title is compared in memory only.</summary>
    public void On(WindowEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Kind is not (WindowEventKind.Foreground or WindowEventKind.TitleChanged))
        {
            return;
        }

        var title = e.Title?.Reveal();
        var now = Context.Clock.UtcNow;
        foreach (var w in _watches)
        {
            if (Matches(w.Spec, e.Exe, title))
            {
                Present(w, e.Hwnd);
            }
            else if (w.Hwnd == e.Hwnd && e.Kind == WindowEventKind.TitleChanged)
            {
                Absent(w, now); // renamed away from the pattern (the watched tab closed)
            }
        }
    }

    /// <summary>The current top-level windows (exe, title): a watch with no match is absent (the destroy path).</summary>
    public void Snapshot(IEnumerable<(string Exe, string Title)> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        var list = windows.ToList();
        var now = Context.Clock.UtcNow;
        foreach (var w in _watches)
        {
            if (list.Any(x => Matches(w.Spec, x.Exe, x.Title)))
            {
                Present(w, w.Hwnd ?? 0);
            }
            else
            {
                Absent(w, now);
            }
        }
    }

    private static void Present(Watch w, nint hwnd)
    {
        w.Hwnd = hwnd;
        w.Seen = true;
        w.AbsentSince = null;
        w.Announced = false;
        w.Wake?.Dispose();
        w.Wake = null;
    }

    private void Absent(Watch w, DateTimeOffset now)
    {
        if (!w.Seen || w.AbsentSince is not null)
        {
            return;
        }

        w.AbsentSince = now;
        w.Hwnd = null;
        w.Wake = Context.Loop.At(now + Grace, () => Check(w));
    }

    private void Check(Watch w)
    {
        if (w.AbsentSince is not { } since || w.Announced || Context.Clock.UtcNow - since < Grace)
        {
            return;
        }

        w.Announced = true;
        _pending.Add(new RuleEvent
        {
            Name = "window.missing",
            Id = "watch-" + Array.IndexOf(_watches, w).ToString(CultureInfo.InvariantCulture) + "@" + since.UtcTicks.ToString(CultureInfo.InvariantCulture),
            Fields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["app"] = Path.GetFileNameWithoutExtension(w.Spec.App.Replace("*", "", StringComparison.Ordinal)),
                ["pattern"] = w.Spec.TitlePattern,
            },
        });
        Context.RulesDirty();
    }

    public override void Contribute(RuleInputs inputs, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        inputs.Events.AddRange(_pending);
        _pending.Clear();
    }

    public override IReadOnlyList<Card> Cards()
    {
        if (!Enabled)
        {
            return [];
        }

        var status = Status;
        var missing = status.Count(s => s.Missing);
        var items = status.Select(s => s.Spec.App + " · " + s.Spec.TitlePattern + (s.Missing ? " · missing" : "")).ToList();
        return
        [
            new Card
            {
                Id = "module.windows_watch",
                Source = CardKit.ModuleSource,
                Title = "Window watch",
                Icon = IconKey.Windows,
                Severity = Value.Literal(missing > 0 ? "warn" : "normal"),
                Blocks =
                [
                    new BadgeBlock { Text = Value.Literal(status.Count == 0 ? "Pick a window to watch in Settings" : missing == 0 ? "All watched windows are open" : missing + " missing"), Priority = 1 },
                    new ListBlock { Label = "Watched", Items = Value.FromRawJson(JsonSerializer.Serialize<IReadOnlyList<string>>(items, Contracts.ContractsJsonContext.Default.IReadOnlyListString)), Max = 5, Priority = 2 },
                ],
            },
        ];
    }
}
