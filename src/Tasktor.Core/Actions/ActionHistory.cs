using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Snapshot;

namespace Tasktor.Core.Actions;

/// <summary>
/// U4 D-G3 Home "Undo for actions in the last 30 min" and the shelf timeline's own actions: successful actions that the
/// catalog can undo, newest first, for <see cref="Window"/>, at most <see cref="Max"/>. Only undoable actions are kept, so
/// arbitrary summaries (an export path, say) never reach the snapshot. Running the undo (same target) marks the entry
/// undone. Loop-only.
/// </summary>
public sealed class ActionHistory
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(30);
    public const int Max = 20;

    private readonly List<RecentAction> _items = [];

    /// <summary>The list changed (an action was added, undone or expired).</summary>
    public event Action? Changed;

    public IReadOnlyList<RecentAction> Items => _items;

    public void Record(ActionRequest req, ActionResult result, string? summary, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(result);
        if (result.Status is not (ActionStatus.Ok or ActionStatus.Partial))
        {
            return;
        }

        var changed = Expire(now);
        var undone = _items.FindIndex(i => i.Undo == req.Action && SameTarget(i.Target, req.Target));
        if (undone >= 0)
        {
            _items[undone] = _items[undone] with { Undo = null };
            changed = true;
        }

        if (ActionCatalog.All.FirstOrDefault(a => a.Id == req.Action) is { Undo: { } undo } && !string.IsNullOrWhiteSpace(summary))
        {
            _items.Insert(0, new RecentAction
            {
                At = now,
                Action = req.Action,
                Text = summary,
                Origin = req.Origin.ToString().ToLowerInvariant(),
                Undo = undo,
                Target = req.Target,
            });
            if (_items.Count > Max)
            {
                _items.RemoveRange(Max, _items.Count - Max);
            }

            changed = true;
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Drop entries older than <see cref="Window"/>; raises <see cref="Changed"/> when any went.</summary>
    public void Prune(DateTimeOffset now)
    {
        if (Expire(now))
        {
            Changed?.Invoke();
        }
    }

    private bool Expire(DateTimeOffset now) => _items.RemoveAll(i => now - i.At > Window) > 0;

    private static bool SameTarget(IReadOnlyDictionary<string, string>? a, IReadOnlyDictionary<string, string>? b) =>
        (a?.Count ?? 0) == (b?.Count ?? 0) && (a is null || a.All(kv => b!.TryGetValue(kv.Key, out var v) && v == kv.Value));
}
