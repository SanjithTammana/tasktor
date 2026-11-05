using System.Globalization;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Platform;
using Tasktor.Core.Actions;
using Tasktor.Core.Rules;

namespace Tasktor.Core.Features.Calendar;

/// <summary>An I13 event window: a name and a time span, made by hand, by a calendar marker or by a focus block (E11).</summary>
public sealed record EventWindow(string Id, string Name, DateTimeOffset Start, DateTimeOffset End, string Origin);

/// <summary>
/// I13 Event mode (Module <c>event_mode</c>): while a window is active, state <c>event_mode</c> is true, so the default rules
/// pause refits and the content worker (undo when it ends). Windows come from the user, from calendar events with the
/// marker, and from E11 focus blocks (which run even when this Module is off). One wake per boundary. The window's name is
/// never logged; a calendar title stays in memory.
/// </summary>
public sealed class EventMode : Feature
{
    private readonly CalendarFeed? _feed;
    private readonly Dictionary<string, EventWindow> _manual = new(StringComparer.Ordinal);
    private IDisposable? _wake;
    private bool _wasActive;

    public EventMode(FeatureContext context, CalendarFeed? feed)
        : base(context) => _feed = feed;

    public override string Id => "event_mode";

    public override FeatureTier Tier => FeatureTier.Module;

    /// <summary>The active window ending last, or null.</summary>
    public EventWindow? Active { get; private set; }

    /// <summary>Raised on the loop when event mode turns on (U3's saved layout restore hooks here; a no-op until it ships).</summary>
    public event Action<EventWindow>? Began;

    public IEnumerable<EventWindow> Windows
    {
        get
        {
            var fromCalendar = Enabled && _feed is not null
                ? _feed.Rows.Where(r => r.EventWindow && r.End is not null).Select(r => new EventWindow("cal-" + NextEvent.KeyText(r.Key), r.Title.Reveal(), r.Start, r.End!.Value, "calendar"))
                : [];
            return _manual.Values.Concat(fromCalendar);
        }
    }

    protected override void OnEnable()
    {
        if (_feed is not null)
        {
            _feed.Updated += Recompute;
            _feed.Acquire();
        }

        Context.Router.Register("event_mode.start", Start);
        Context.Router.Register("event_mode.stop", Stop);
        Recompute();
    }

    protected override void OnDisable()
    {
        if (_feed is not null)
        {
            _feed.Updated -= Recompute;
            _feed.Release();
        }

        _manual.Clear();
        Recompute();
    }

    /// <summary>Open a window (manual or a focus block). Works with the Module off, for E11.</summary>
    public void Begin(EventWindow w)
    {
        ArgumentNullException.ThrowIfNull(w);
        _manual[w.Id] = w;
        Recompute();
    }

    public void End(string id)
    {
        if (_manual.Remove(id))
        {
            Recompute();
        }
    }

    public void Recompute()
    {
        var now = Context.Clock.UtcNow;
        foreach (var gone in _manual.Values.Where(w => w.End <= now).Select(w => w.Id).ToList())
        {
            _manual.Remove(gone);
        }

        var windows = Windows.ToList();
        Active = windows.Where(w => w.Start <= now && w.End > now).MaxBy(w => w.End);
        if (Active is not null && !_wasActive)
        {
            Context.Log.Info("event_mode.on", Logging.LogField.Of("origin", Active.Origin));
            Began?.Invoke(Active);
        }

        if (_wasActive != (Active is not null))
        {
            Context.RulesDirty();
        }

        _wasActive = Active is not null;
        _wake?.Dispose();
        var next = windows.SelectMany(w => new[] { w.Start, w.End }).Where(t => t > now).Order().FirstOrDefault();
        _wake = next == default ? null : Context.Loop.At(next, Recompute);
    }

    public override void Contribute(RuleInputs inputs, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        inputs.State("event_mode", Active is not null);
    }

    // Cards: I13's countdown shows on core.guards (U4), built by the Guards feature from Active.
    public override IReadOnlyList<Contracts.Cards.Card> Cards() => [];

    private PreparedAction Start(ActionRequest req)
    {
        var minutes = int.TryParse(req.Args?.GetValueOrDefault("minutes"), NumberStyles.None, CultureInfo.InvariantCulture, out var m) && m is > 0 and <= 24 * 60 ? m : 60;
        var name = req.Args?.GetValueOrDefault("name") is { Length: > 0 and <= 80 } n ? n : "Event mode";
        var now = Context.Clock.UtcNow;
        var w = new EventWindow("manual", name, now, now.AddMinutes(minutes), "manual");
        return new PreparedAction
        {
            Summary = "Event mode",
            Immediate = Run(() => Begin(w), "event_mode.start", "Event mode on for " + minutes + " min"),
        };
    }

    private PreparedAction Stop(ActionRequest req) => new()
    {
        Summary = "Event mode",
        Immediate = Run(() => End("manual"), "event_mode.stop", "Event mode off"),
    };

    private static ActionResult Run(Action a, string id, string message)
    {
        a();
        return new ActionResult { Action = id, Status = ActionStatus.Ok, Message = message };
    }
}
