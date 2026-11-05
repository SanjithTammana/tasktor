using System.Globalization;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Platform;
using Tasktor.Core.Rules;

namespace Tasktor.Core.Features.Calendar;

/// <summary>
/// E1 Deadlines (Module <c>deadlines</c>): the next 5 deadlines from the deadlines file and calendar deadline markers as
/// countdowns, warn under 48 h and critical under 24 h. No toasts by default; event <c>deadline.soon</c> fires 24 h before
/// for the default rule (off).
/// </summary>
public sealed class Deadlines : Feature
{
    public static readonly TimeSpan Warn = TimeSpan.FromHours(48);
    public static readonly TimeSpan Critical = TimeSpan.FromHours(24);

    private readonly CalendarFeed _feed;
    private readonly HashSet<EventKey> _announced = [];
    private readonly List<RuleEvent> _pending = [];
    private IDisposable? _wake;

    public Deadlines(FeatureContext context, CalendarFeed feed)
        : base(context) => _feed = feed;

    public override string Id => "deadlines";

    public override FeatureTier Tier => FeatureTier.Module;

    public IReadOnlyList<CalEvent> Upcoming { get; private set; } = [];

    protected override void OnEnable()
    {
        _feed.Updated += Recompute;
        _feed.Acquire();
        Recompute();
    }

    protected override void OnDisable()
    {
        _feed.Updated -= Recompute;
        _feed.Release();
        _wake?.Dispose();
        Upcoming = [];
    }

    public static Severity SeverityFor(TimeSpan left) => left <= Critical ? Severity.Critical : left <= Warn ? Severity.Warn : Severity.Normal;

    public void Recompute()
    {
        if (!Enabled)
        {
            return;
        }

        var now = Context.Clock.UtcNow;
        Upcoming = _feed.Rows.Where(r => r.Deadline && r.Start > now).OrderBy(r => r.Start).Take(5).ToList();
        foreach (var d in Upcoming.Where(d => d.Start - now <= Critical && _announced.Add(d.Key)))
        {
            _pending.Add(new RuleEvent
            {
                Name = "deadline.soon",
                Id = d.Key.Uid + "@" + d.Start.UtcTicks.ToString(CultureInfo.InvariantCulture),
                Fields = new Dictionary<string, string>(StringComparer.Ordinal) { ["title"] = d.Title.Reveal(), ["when"] = _feed.Clock(d.Start) },
            });
        }

        if (_pending.Count > 0)
        {
            Context.RulesDirty();
        }

        _wake?.Dispose();
        var next = Upcoming.SelectMany(d => new[] { d.Start - Warn, d.Start - Critical, d.Start }).Where(t => t > now).Order().FirstOrDefault();
        _wake = next == default ? null : Context.Loop.At(next, Recompute);
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

        var now = Context.Clock.UtcNow;
        var blocks = Upcoming.Select((d, i) => (Block)new CountdownBlock
        {
            Label = d.Title.Reveal(),
            Until = Value.Literal(d.Start.ToString("O", CultureInfo.InvariantCulture)),
            Severity = Value.Literal(CardKit.SeverityWord(SeverityFor(d.Start - now))),
            Priority = i + 1,
        }).ToList();
        if (blocks.Count == 0)
        {
            blocks.Add(new BadgeBlock { Text = Value.Literal(_feed.Feeds.Count == 0 ? "Add a deadlines file or a calendar in Settings" : "No deadlines in the next 14 days") });
        }

        return
        [
            new Card
            {
                Id = "module.deadlines",
                Source = CardKit.ModuleSource,
                Title = "Deadlines",
                Icon = IconKey.Clock,
                Severity = Value.Literal(CardKit.SeverityWord(Upcoming.Count == 0 ? Severity.Normal : SeverityFor(Upcoming[0].Start - now))),
                Blocks = blocks,
            },
        ];
    }
}
