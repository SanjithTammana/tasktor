using System.Globalization;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Snapshot;
using Tasktor.Core.Actions;
using Tasktor.Core.Features.Calendar;
using AppsFeature = Tasktor.Core.Features.SystemMonitor.Apps;

namespace Tasktor.Core.Features.Guards;

/// <summary>
/// I4 Power guard (Core) and the <c>core.guards</c> card (U4: I3, I4, I13 + B). I4 itself is default rules over existing
/// reversible actions (D-S4); this class adds the one new action, <c>cap_cores</c> (B3.5's core cap: a Job Object CPU
/// rate, released by the rule's undo or by "Release all caps"), and lists every active guard with Undo. App-affecting
/// rules act only on groups the user opted in (default none).
/// </summary>
public sealed class PowerGuard : Feature
{
    private readonly AppsFeature _apps;
    private readonly EventMode? _eventMode;
    private readonly Dictionary<string, (string Name, int Max, IReadOnlyList<ProcKey> Keys, DateTimeOffset Since)> _caps = new(StringComparer.Ordinal);

    public PowerGuard(FeatureContext context, AppsFeature apps, EventMode? eventMode)
        : base(context)
    {
        _apps = apps;
        _eventMode = eventMode;
    }

    public override string Id => "power_guard";

    public override FeatureTier Tier => FeatureTier.Core;

    public IReadOnlyCollection<string> Caps => _caps.Keys;

    /// <summary>One-line notices for the card (e.g. I3's calendar-only fallback).</summary>
    public List<Func<string?>> Notices { get; } = [];

    protected override void OnEnable()
    {
        Context.Router.Register("cores.cap", Cap);
        Context.Router.Register("cores.release", Release);
        Context.Router.Register("cores.release_all", ReleaseAll);
    }

    protected override void OnDisable()
    {
    }

    /// <summary>A stable cap id per target, so the rule's undo (same target) releases exactly that cap.</summary>
    public static string CapId(IReadOnlyDictionary<string, string>? target) =>
        target is null ? "cap" : "cap:" + string.Join(';', target.Where(kv => kv.Key != "rule").OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value));

    /// <summary>Guards that are not rule rows (a cap set by hand) for <c>state.guards</c>.</summary>
    public IEnumerable<GuardState> ManualGuards() => _caps.Select(kv => new GuardState
    {
        Rule = kv.Key,
        Text = kv.Value.Name + " capped to " + kv.Value.Max + (kv.Value.Max == 1 ? " core" : " cores"),
        Since = kv.Value.Since,
    });

    private PreparedAction Cap(ActionRequest req)
    {
        if (Context.Processes is not { } pc)
        {
            return new PreparedAction { Summary = "Cap cores", Refusal = "Not available" };
        }

        var max = int.TryParse(req.Args?.GetValueOrDefault("max"), NumberStyles.None, CultureInfo.InvariantCulture, out var m) && m is > 0 and <= 256 ? m : 2;
        if (_apps.Targets.Resolve(req.Target) is not { Keys.Count: > 0 } t)
        {
            return new PreparedAction { Summary = "Cap cores", Refusal = "Nothing to cap is running" };
        }

        var id = CapId(req.Target);
        return new PreparedAction
        {
            Summary = "Cap " + t.Name + " to " + max + " cores",
            TargetName = t.Name,
            Processes = t.Keys,
            Run = keys =>
            {
                var outcomes = pc.CapCores(id, keys, max);
                var ok = outcomes.Where(o => o.Status == ActionStatus.Ok).Select(o => o.Key).ToList();
                Context.Loop.Post(() =>
                {
                    if (ok.Count > 0)
                    {
                        _caps[id] = (t.Name, max, ok, Context.Clock.UtcNow);
                        Context.RulesDirty();
                    }
                });
                return new ActionResult
                {
                    Action = "cores.cap",
                    Status = ok.Count > 0 ? ActionStatus.Ok : ActionStatus.Failed,
                    Message = ok.Count > 0 ? t.Name + " capped to " + max + " cores" : "Could not cap " + t.Name,
                };
            },
        };
    }

    private PreparedAction Release(ActionRequest req)
    {
        var id = req.Target?.GetValueOrDefault("cap") ?? CapId(req.Target);
        if (!_caps.ContainsKey(id))
        {
            return new PreparedAction { Summary = "Release cap", Immediate = new ActionResult { Action = "cores.release", Status = ActionStatus.Ok, Message = "Nothing capped" } };
        }

        return ReleaseIds([id], "cores.release");
    }

    private PreparedAction ReleaseAll(ActionRequest req) =>
        _caps.Count == 0
            ? new PreparedAction { Summary = "Release all caps", Immediate = new ActionResult { Action = "cores.release_all", Status = ActionStatus.Ok, Message = "Nothing capped" } }
            : ReleaseIds([.. _caps.Keys], "cores.release_all");

    private PreparedAction ReleaseIds(IReadOnlyList<string> ids, string action) => new()
    {
        Summary = ids.Count == 1 ? "Release cap" : "Release all caps",
        Run = _ =>
        {
            foreach (var id in ids)
            {
                Context.Processes?.CapCores(id, [], 0);
            }

            Context.Loop.Post(() =>
            {
                foreach (var id in ids)
                {
                    _caps.Remove(id);
                }

                Context.RulesDirty();
            });
            return new ActionResult { Action = action, Status = ActionStatus.Ok, Message = ids.Count == 1 ? "Cap released" : ids.Count + " caps released" };
        },
    };

    public override IReadOnlyList<Card> Cards()
    {
        var blocks = new List<Block>
        {
            new TableBlock
            {
                Label = "Active guards",
                Columns = [new TableColumn { Key = "text", Label = "Guard" }, new TableColumn { Key = "since", Label = "Since" }],
                Rows = Value.Binding("/state/guards"),
                RowKey = "rule",
                Max = 6,
                Priority = 1,
            },
        };
        if (_eventMode?.Active is { } w)
        {
            blocks.Insert(0, new CountdownBlock { Label = w.Origin == "focus" ? "Focus block" : "Event mode", Until = Value.Literal(w.End.ToString("O", CultureInfo.InvariantCulture)), Priority = 1 });
        }

        foreach (var notice in Notices.Select(n => n()).OfType<string>())
        {
            blocks.Add(new BadgeBlock { Text = Value.Literal(notice), Priority = 3 });
        }

        var actions = new List<CardAction> { new() { Id = "undo", Label = "Undo", Action = "rule.undo", Target = CardKit.Target("rule", "{row}"), Priority = 1 } };
        if (_caps.Count > 0)
        {
            actions.Add(new CardAction { Id = "release_all", Label = "Release all caps", Action = "cores.release_all", Priority = 2 });
        }

        return
        [
            new Card
            {
                Id = "core.guards",
                Source = CardKit.Core,
                Title = "Guards",
                Icon = IconKey.Shield,
                Blocks = blocks,
                Actions = actions,
            },
        ];
    }
}
