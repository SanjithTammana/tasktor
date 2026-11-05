using System.Globalization;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Snapshot;
using Tasktor.Core.Actions;
using Tasktor.Core.Rules;

namespace Tasktor.Core.Features.Calendar;

/// <summary>
/// E2 Next event (Module <c>next_event</c>): the next event with a countdown, the rest of today, urgency at 15 / 5 / 1 min,
/// state <c>meeting_now</c> (an event in progress with a meeting link, I3's calendar half) and event <c>meeting.soon</c>
/// one minute before a meeting for the default Join toast. Join opens the link only on a click (D-H6). One wake per
/// boundary (15 / 5 / 1 min before, start, end); nothing ticks between.
/// </summary>
public sealed class NextEvent : Feature
{
    public static readonly TimeSpan[] Urgency = [TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1)];

    private readonly CalendarFeed _feed;
    private readonly HashSet<EventKey> _announced = [];
    private readonly List<RuleEvent> _pending = [];
    private IDisposable? _wake;

    public NextEvent(FeatureContext context, CalendarFeed feed)
        : base(context) => _feed = feed;

    public override string Id => "next_event";

    public override FeatureTier Tier => FeatureTier.Module;

    public CalEvent? Next { get; private set; }

    public CalEvent? MeetingNow { get; private set; }

    protected override void OnEnable()
    {
        _feed.Updated += Recompute;
        _feed.Acquire();
        Context.Router.Register("url.open", OpenLink);
        Recompute();
    }

    protected override void OnDisable()
    {
        _feed.Updated -= Recompute;
        _feed.Release();
        _wake?.Dispose();
        _wake = null;
        Next = MeetingNow = null;
        Context.UpdateState(s => s with { Calendar = null });
    }

    public void Recompute()
    {
        if (!Enabled)
        {
            return;
        }

        var now = Context.Clock.UtcNow;
        var rows = _feed.Rows.Where(r => !r.Deadline).ToList();
        var wasMeeting = MeetingNow?.Key;
        MeetingNow = rows.FirstOrDefault(r => r.MeetingUrl is not null && r.Start <= now && (r.End ?? r.Start) > now);
        Next = rows.Where(r => r.Start > now).MinBy(r => r.Start);

        foreach (var r in rows.Where(r => r.MeetingUrl is not null && r.Start > now && r.Start - now <= Urgency[2] && _announced.Add(r.Key)))
        {
            _pending.Add(new RuleEvent
            {
                Name = "meeting.soon",
                Id = r.Key.Uid + "@" + r.Start.UtcTicks.ToString(CultureInfo.InvariantCulture),
                Fields = new Dictionary<string, string>(StringComparer.Ordinal) { ["title"] = r.Title.Reveal(), ["start"] = _feed.Clock(r.Start) },
                Target = CardKit.Target("event", KeyText(r.Key)),
            });
        }

        _announced.RemoveWhere(k => k.Start < now - TimeSpan.FromHours(1));
        var today = rows.Where(r => (r.End ?? r.Start) > now && _feed.SameLocalDay(r.Start, now)).Take(8)
            .Select(r => new CalendarItem { Title = r.Title.Reveal(), Start = r.Start, End = r.End, Meeting = r.MeetingUrl is not null }).ToList();
        Context.UpdateState(s => s with { Calendar = today.Count == 0 ? null : today });
        if (_pending.Count > 0 || wasMeeting != MeetingNow?.Key)
        {
            Context.RulesDirty();
        }

        ScheduleWake(rows, now);
    }

    private void ScheduleWake(List<CalEvent> rows, DateTimeOffset now)
    {
        _wake?.Dispose();
        DateTimeOffset? next = null;
        foreach (var r in rows)
        {
            foreach (var t in Urgency.Select(u => r.Start - u).Append(r.Start).Append(r.End ?? r.Start))
            {
                if (t > now && (next is null || t < next))
                {
                    next = t;
                }
            }
        }

        _wake = next is { } due ? Context.Loop.At(due, Recompute) : null;
    }

    public override void Contribute(RuleInputs inputs, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        inputs.State("meeting_now", MeetingNow is not null);
        inputs.Events.AddRange(_pending);
        _pending.Clear();
    }

    /// <summary>Severity by time to the next event: warn within 15 min, critical within 5.</summary>
    public Severity UrgencyAt(DateTimeOffset now) => Next is not { } n ? Severity.Normal
        : n.Start - now <= Urgency[1] ? Severity.Critical
        : n.Start - now <= Urgency[0] ? Severity.Warn : Severity.Normal;

    public override IReadOnlyList<Card> Cards()
    {
        if (!Enabled)
        {
            return [];
        }

        var now = Context.Clock.UtcNow;
        var blocks = new List<Block>();
        if (Next is { } n)
        {
            blocks.Add(new CountdownBlock { Label = n.Title.Reveal(), Until = Value.Literal(n.Start.ToString("O", CultureInfo.InvariantCulture)), Priority = 1 });
        }
        else
        {
            blocks.Add(new BadgeBlock { Text = Value.Literal("Nothing else in the next 14 days"), Priority = 1 });
        }

        if (MeetingNow is not null)
        {
            blocks.Add(new BadgeBlock { Text = Value.Literal("In a meeting"), Priority = 2 });
        }

        var rest = _feed.Rows.Where(r => !r.Deadline && r.Start > now && _feed.SameLocalDay(r.Start, now)).Skip(1).Take(5).Select(r => _feed.Clock(r.Start) + "  " + r.Title.Reveal()).ToList();
        if (rest.Count > 0)
        {
            blocks.Add(new ListBlock { Label = "Rest of today", Items = Value.FromRawJson(System.Text.Json.JsonSerializer.Serialize(rest, Contracts.ContractsJsonContext.Default.IReadOnlyListString)), Max = 5, Priority = 3 });
        }

        var link = MeetingNow ?? (Next is { MeetingUrl: not null } nx && nx.Start - now <= Urgency[0] ? nx : null);
        return
        [
            new Card
            {
                Id = "module.next_event",
                Source = CardKit.ModuleSource,
                Title = "Next event",
                Icon = IconKey.Calendar,
                Severity = Value.Literal(CardKit.SeverityWord(UrgencyAt(now))),
                Blocks = blocks,
                Actions = link is null ? null : [new CardAction { Id = "join", Label = "Join", Action = "url.open", Target = CardKit.Target("event", KeyText(link.Key)) }],
            },
        ];
    }

    public static string KeyText(EventKey k) => k.Uid + "|" + k.Start.UtcTicks.ToString(CultureInfo.InvariantCulture);

    /// <summary>Opens a meeting link of a current calendar row (never an arbitrary URL from the request).</summary>
    private PreparedAction OpenLink(ActionRequest req)
    {
        var key = req.Target?.GetValueOrDefault("event");
        var row = _feed.Rows.FirstOrDefault(r => KeyText(r.Key) == key);
        if (row?.MeetingUrl is not { } url)
        {
            return new PreparedAction { Summary = "Join meeting", Refusal = "That event has no meeting link now" };
        }

        return new PreparedAction
        {
            Summary = "Join meeting",
            Run = _ => new ActionResult { Action = "url.open", Status = Context.Launch(url.Reveal()) ? ActionStatus.Ok : ActionStatus.Failed },
        };
    }
}
