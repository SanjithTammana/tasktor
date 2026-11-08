using System.Text.Json;
using Tasktor.Contracts;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Config;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Windows;
using Tasktor.Contracts.Workflow;
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

    public Func<WindowsSettings?> Settings { get; init; } = () => null;

    public SwitcherFeed Feed()
    {
        var settings = Settings();
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
            Hotkey = settings?.Hotkey is { Length: > 0 } h ? h : "Alt+`",
            ThisDesktopOnly = settings?.ThisDesktopOnly == true,
        };
    }

    protected override void OnEnable()
    {
        _source?.Start(e => Context.Loop.Post(() => On(e)));
        RefreshJobs();
        _refresh = Context.Loop.Every(JobsEvery, RefreshJobs);
        // The shell asks when it opens: answered on the pipe thread from a copy taken under the lock.
        Context.RegisterPull("switcher", _ => JsonSerializer.SerializeToNode(Feed(), ContractsJsonContext.Default.SwitcherFeed));
    }

    protected override void OnDisable()
    {
        _source?.Stop();
        _refresh?.Dispose();
        _refresh = null;
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
