using System.Text.Json;
using Tasktor.Contracts;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Config;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Snapshot;
using Tasktor.Contracts.Windows;
using Tasktor.Contracts.Workflow;
using Tasktor.Core.Actions;
using Tasktor.Core.Windows;

namespace Tasktor.Core.Features.Windows;

/// <summary>
/// F2 (Module <c>switcher</c>, U3 D-W2 / D-W4): the agent's half of the window switcher. It keeps the most-recently-used
/// order of window handles from <c>WinEventSource</c>'s foreground events (in memory, bounded) and answers the shell's
/// <c>switcher</c> pull with that order, the watched jobs' pids (job and frozen badges) and the Windows settings. Handles
/// and pids only: the shell enumerates the windows itself, so no title is fed or kept here.
/// </summary>
public sealed class SwitcherFeedFeature : Feature
{
    /// <summary>Handles remembered; older ones fall back to enumeration (z) order in the shell.</summary>
    public const int MaxWindows = 256;

    /// <summary>U3 OQ-W1: the default switcher hotkey.</summary>
    public const string DefaultKeys = "Alt+`";

    private readonly IEventSource<WindowEvent>? _source;
    private readonly WindowList _mru = new();
    private readonly Lock _gate = new();
    private IReadOnlyList<SwitcherJob> _jobs = [];
    private IDisposable? _refresh;

    /// <summary>How often the jobs copy is refreshed on the loop (the pull is answered on the pipe thread from it).</summary>
    public static readonly TimeSpan JobsEvery = TimeSpan.FromSeconds(2);

    public SwitcherFeedFeature(FeatureContext context, IEventSource<WindowEvent>? source)
        : base(context) => _source = source;

    public override string Id => "switcher";

    public override FeatureTier Tier => FeatureTier.Module;

    /// <summary>The watched jobs (B's rows) with their processes. Called on the logic loop only.</summary>
    public Func<IReadOnlyList<SwitcherJob>> Jobs { get; init; } = () => [];

    /// <summary>The Windows settings; by default the agent's own settings file (tests may supply fixed ones).</summary>
    public Func<WindowsSettings?>? Settings { get; init; }

    private WindowsSettings? Current => Settings is { } f ? f() : Context.Settings.Current.Windows;

    /// <summary>Starts <c>tasktor-shell</c> while this Module is on and stops it when it goes off (D-W2, D-H9).</summary>
    public HostSupervisor? Supervisor { get; init; }

    public SwitcherFeed Feed()
    {
        var settings = Current;
        IReadOnlyList<long> order;
        IReadOnlyList<SwitcherJob> jobs;
        lock (_gate)
        {
            order = _mru.Recent(MaxWindows);
            jobs = _jobs;
        }

        return new SwitcherFeed
        {
            Mru = order,
            Jobs = jobs,
            Hotkey = Keys(),
            ThisDesktopOnly = settings?.ThisDesktopOnly == true,
        };
    }

    protected override void OnEnable()
    {
        _source?.Start(e => Context.Loop.Post(() => On(e)));
        RefreshJobs();
        _refresh = Context.Loop.Every(JobsEvery, RefreshJobs);
        Supervisor?.Start();
        Context.Router.Register("switcher.settings", Change);
        Context.Router.Register("switcher.status", Status);
        Publish(registered: null);
        // The shell asks when it opens: answered on the pipe thread from a copy taken under the lock.
        Context.RegisterPull("switcher", _ => JsonSerializer.SerializeToNode(Feed(), ContractsJsonContext.Default.SwitcherFeed));
    }

    protected override void OnDisable()
    {
        Context.UpdateState(s => s with { Switcher = null });
        Supervisor?.Stop();
        _source?.Stop();
        _refresh?.Dispose();
        _refresh = null;
    }

    private string Keys() => Hotkey.Parse(Current?.Hotkey ?? DefaultKeys, out _)?.ToString() ?? DefaultKeys;

    private void Publish(bool? registered) =>
        Context.UpdateState(s => s with { Switcher = new SwitcherState { Keys = Keys(), Registered = registered, ThisDesktopOnly = Current?.ThisDesktopOnly == true } });

    /// <summary>
    /// <c>switcher.settings</c> (people only): <c>keys</c> (a hotkey other than the command line's) and/or
    /// <c>this_desktop_only</c>. A new hotkey restarts the shell, which registers it at start and reports back.
    /// </summary>
    private PreparedAction Change(ActionRequest req)
    {
        var keysText = req.Args?.GetValueOrDefault("keys");
        var desktopText = req.Args?.GetValueOrDefault("this_desktop_only");
        if (keysText is null && desktopText is null)
        {
            return new PreparedAction { Summary = "Window switcher settings", Refusal = "Nothing to change" };
        }

        Hotkey? keys = null;
        if (keysText is not null)
        {
            if (Hotkey.Parse(keysText, out var why) is not { } h)
            {
                return new PreparedAction { Summary = "Window switcher shortcut", Refusal = why };
            }

            if (h.ToString() == Hotkey.FromSetting(Context.Settings.Current.Flyout?.Hotkey).ToString())
            {
                return new PreparedAction { Summary = "Window switcher shortcut", Refusal = h + " opens the command line" };
            }

            keys = h;
        }

        bool? desktop = desktopText switch { "true" => true, "false" => false, null => null, _ => (bool?)null };
        if (desktopText is not null && desktop is null)
        {
            return new PreparedAction { Summary = "Window switcher settings", Refusal = "this_desktop_only is true or false" };
        }

        var restart = keys is not null && keys.ToString() != Keys();
        return new PreparedAction
        {
            Summary = keys is not null ? "Window switcher shortcut " + keys : "Window switcher: " + (desktop == true ? "this desktop only" : "all desktops"),
            Immediate = new ActionResult { Action = req.Action, Status = ActionStatus.Ok, Message = keys is not null ? "Window switcher: " + keys : "Saved" },
            Done = _ =>
            {
                Context.Settings.Update(c => c with
                {
                    Windows = (c.Windows ?? new WindowsSettings()) with
                    {
                        Hotkey = keys?.ToString() ?? c.Windows?.Hotkey,
                        ThisDesktopOnly = desktop ?? c.Windows?.ThisDesktopOnly ?? false,
                    },
                });
                Publish(registered: restart ? null : Context.State().Switcher?.Registered);
                if (restart && Supervisor is { } sup)
                {
                    sup.Stop(); // the shell registers its hotkey at start: a restart applies the new one
                    sup.Start();
                }
            },
        };
    }

    /// <summary><c>switcher.status</c>: the shell (only) says whether Windows accepted its hotkey; a report for older keys is ignored.</summary>
    private PreparedAction Status(ActionRequest req)
    {
        if (req.Origin != Origin.Shell)
        {
            return new PreparedAction { Summary = "Window switcher status", Refusal = "Only the switcher reports this" };
        }

        var keys = req.Args?.GetValueOrDefault("keys");
        var registered = req.Args?.GetValueOrDefault("registered") == "true";
        return new PreparedAction
        {
            Summary = "Window switcher status",
            Immediate = new ActionResult { Action = req.Action, Status = ActionStatus.Ok },
            Done = done =>
            {
                if (keys is not null && Hotkey.Parse(keys, out _)?.ToString() == Keys())
                {
                    Publish(registered);
                }
            },
        };
    }

    private void RefreshJobs()
    {
        var jobs = Jobs();
        lock (_gate)
        {
            _jobs = jobs;
        }
    }

    /// <summary>No card: the switcher is its own window (U3 D-W2).</summary>
    public override IReadOnlyList<Card> Cards() => [];

    private void On(WindowEvent e)
    {
        lock (_gate)
        {
            switch (e.Kind)
            {
                case WindowEventKind.Foreground:
                    _mru.Touch(e.Hwnd);
                    _mru.Trim(MaxWindows);
                    break;
                case WindowEventKind.Destroyed:
                    _mru.Remove(e.Hwnd);
                    break;
            }
        }
    }
}
