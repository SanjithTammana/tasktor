using System.Text.Json;
using Tasktor.Contracts;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Presets;
using Tasktor.Core.Actions;
using Tasktor.Core.Windows;

namespace Tasktor.Core.Features.Windows;

public sealed record WindowMove(long Hwnd, RectPx Rect, ShowState Show, bool Focus);

/// <summary>Restores user presets; rules may arrange existing windows but never start an app.</summary>
public sealed class PresetRestorer(FeatureContext context) : Feature(context)
{
    private readonly Dictionary<string, UndoSnapshot> _undos = new(StringComparer.Ordinal);
    private string? _latestUndo;
    private long _generation;
    private IDisposable? _pendingTimer;
    private IDisposable? _undoTimer;
    private PresetStore _store = new(context);

    public override string Id => "presets";

    public override FeatureTier Tier => FeatureTier.Module;

    public PresetStore Store => _store;

    public Func<IReadOnlyList<WindowInfo>> Windows { get; set; } = () => [];

    public Func<IReadOnlyList<MonitorInfo>> Monitors { get; set; } = () => [];

    public Func<WindowMove, bool> Move { get; set; } = _ => false;

    public Func<string, bool> Launch { get; set; } = _ => false;

    public Func<PresetLaunch, bool>? LaunchWithArgs { get; set; }

    public Func<FancyZonesLayout, string?>? ApplyLayout { get; set; }

    public Func<long, long, bool> UserChangedSince { get; set; } = (_, _) => true;

    public Func<long> Timestamp { get; set; } = () => Environment.TickCount64;

    public Func<uint, bool> IsFrozen { get; set; } = _ => false;

    protected override void OnEnable()
    {
        Context.Router.Register("preset.arrange", Arrange);
        Context.Router.Register("preset.apply", Apply);
        Context.Router.Register("preset.undo", Undo);
        Context.OffLoop(() =>
        {
            var loaded = new PresetStore(Context);
            loaded.Load();
            Context.Loop.Post(() => { if (Enabled) { _store = loaded; Context.UpdateState(s => s); } });
        });
    }

    protected override void OnDisable()
    {
        Interlocked.Increment(ref _generation);
        _pendingTimer?.Dispose();
        _pendingTimer = null;
        _undoTimer?.Dispose();
        _undoTimer = null;
        _undos.Clear();
        _latestUndo = null;
    }

    private PreparedAction Arrange(ActionRequest req) => Restore(req, false);

    private PreparedAction Apply(ActionRequest req) => req.Origin == Origin.Rule
        ? new PreparedAction { Summary = "Apply preset", Refusal = "Rules cannot launch presets" }
        : Restore(req, true);

    private PreparedAction Restore(ActionRequest req, bool launch)
    {
        var name = req.Target?.GetValueOrDefault("preset") ?? req.Args?.GetValueOrDefault("preset");
        var preset = _store.Current.Presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (preset is null) return new PreparedAction { Summary = "Arrange preset", Refusal = "Preset not found" };
        _pendingTimer?.Dispose();
        _pendingTimer = null;
        var generation = Interlocked.Increment(ref _generation);
        var undoKey = Key(req, preset.Name);
        var started = Timestamp();
        var deadline = Context.Clock.UtcNow.AddSeconds(20);
        UndoSnapshot? before = null;
        List<int> pending = [];
        HashSet<long> existing = [];
        return new PreparedAction
        {
            Summary = launch ? "Apply preset" : "Arrange preset",
            Run = _ =>
            {
                var monitors = Monitors();
                if (monitors.Count == 0) return new ActionResult { Action = req.Action, Status = ActionStatus.Failed, Message = "No monitors available" };
                var windows = Windows().Select(w => w with { Frozen = w.Frozen || IsFrozen(w.Pid) }).ToList();
                existing = windows.Select(w => w.Hwnd).ToHashSet();
                var plans = PresetPlanner.Plan(preset, windows, new MonitorMap(monitors), launch && req.Origin != Origin.Rule);
                var succeeded = new List<SlotPlan>();
                var items = new List<ActionItemResult>();
                foreach (var plan in plans)
                {
                    var ok = false;
                    var reason = plan.Reason;
                    if (Interlocked.Read(ref _generation) != generation) reason = "cancelled";
                    else if (plan.Outcome == SlotOutcome.Move && plan.Window is { } w && plan.TargetPx is { } rect)
                    {
                        if (IsFrozen(w.Pid)) reason = "frozen job";
                        else if (UserChangedSince(w.Hwnd, started)) reason = "window changed";
                        else { ok = Move(new WindowMove(w.Hwnd, rect, plan.Show, plan.Focus)); if (ok) succeeded.Add(plan); else reason = plan.Show == ShowState.Maximized ? "maximized placement unavailable without activation" : "move failed"; }
                    }
                    else if (plan.Outcome == SlotOutcome.Launch && preset.Slots[plan.Slot].Launch is { } app)
                    {
                        ok = app.Args is { Count: > 0 } ? LaunchWithArgs?.Invoke(app) == true : Launch(app.Exe);
                        if (ok) pending.Add(plan.Slot); else reason = "launch failed";
                    }
                    items.Add(new ActionItemResult { Item = "slot " + (plan.Slot + 1), Status = ok ? ActionStatus.Ok : ActionStatus.Refused, Reason = reason });
                }
                if (preset.Fancyzones is { } layouts)
                {
                    foreach (var layout in layouts)
                    {
                        if (Interlocked.Read(ref _generation) != generation) break;
                        var why = !launch ? "layout unchanged by arrange" : ApplyLayout?.Invoke(layout) ?? (ApplyLayout is null ? "FancyZones unavailable" : null);
                        items.Add(new ActionItemResult { Item = "layout", Status = why is null or "FancyZones unavailable" or "layout unchanged by arrange" ? ActionStatus.Ok : ActionStatus.Refused, Reason = why });
                    }
                }
                before = PresetPlanner.Snapshot(succeeded, Context.Clock.UtcNow);
                return new ActionResult { Action = req.Action, Status = items.All(i => i.Status == ActionStatus.Ok) ? ActionStatus.Ok : ActionStatus.Partial, Items = items, Undo = succeeded.Count > 0 ? "preset.undo" : null };
            },
            Done = result =>
            {
                if (Interlocked.Read(ref _generation) != generation) return;
                if (before is { Before.Count: > 0 })
                {
                    _undos[undoKey] = before;
                    _latestUndo = undoKey;
                    ScheduleUndoExpiry();
                    OfferUndo(undoKey);
                }
                _pendingTimer?.Dispose();
                if (pending.Count > 0) WaitForLaunched(preset, pending, existing, deadline, generation, started, undoKey);
            },
        };
    }

    private void WaitForLaunched(Preset preset, List<int> pending, HashSet<long> existing, DateTimeOffset deadline, long generation, long started, string undoKey)
    {
        if (!Enabled || Interlocked.Read(ref _generation) != generation || pending.Count == 0 || Context.Clock.UtcNow >= deadline) { _pendingTimer?.Dispose(); _pendingTimer = null; return; }
        _pendingTimer = Context.Loop.At(Context.Clock.UtcNow.AddMilliseconds(500), () =>
        {
            Context.OffLoop(() =>
            {
                var monitors = Monitors();
                var windows = Windows().Where(w => !existing.Contains(w.Hwnd)).Select(w => w with { Frozen = w.Frozen || IsFrozen(w.Pid) }).ToList();
                var found = new List<(int Slot, SlotPlan Plan)>();
                var dropped = new List<(int Slot, long Hwnd)>();
                if (monitors.Count > 0)
                {
                    var subset = preset with { Slots = pending.Select(i => preset.Slots[i] with { Launch = null }).ToList() };
                    foreach (var plan in PresetPlanner.Plan(subset, windows, new MonitorMap(monitors), false))
                    {
                        if (plan.Window is not { } w || Interlocked.Read(ref _generation) != generation || Context.Clock.UtcNow >= deadline) continue;
                        var slot = pending[plan.Slot];
                        if (plan.Outcome == SlotOutcome.Move && plan.TargetPx is { } rect && !UserChangedSince(w.Hwnd, started) && !IsFrozen(w.Pid) && Move(new WindowMove(w.Hwnd, rect, plan.Show, plan.Focus))) found.Add((slot, plan));
                        else dropped.Add((slot, w.Hwnd));
                    }
                }
                Context.Loop.Post(() =>
                {
                    if (!Enabled || Interlocked.Read(ref _generation) != generation) return;
                    foreach (var (slot, hwnd) in dropped)
                    {
                        pending.Remove(slot);
                        existing.Add(hwnd);
                    }
                    foreach (var (slot, plan) in found)
                    {
                        pending.Remove(slot);
                        existing.Add(plan.Window!.Hwnd);
                        var entry = (plan.Window.Hwnd, plan.Window.WindowPx, plan.Window.Show);
                        var previous = _undos.GetValueOrDefault(undoKey);
                        _undos[undoKey] = new UndoSnapshot(Context.Clock.UtcNow, [.. (previous?.Before ?? []), entry]);
                        _latestUndo = undoKey;
                    }
                    if (found.Count > 0) { ScheduleUndoExpiry(); OfferUndo(undoKey); }
                    WaitForLaunched(preset, pending, existing, deadline, generation, started, undoKey);
                });
            });
        });
    }

    private PreparedAction Undo(ActionRequest req)
    {
        var name = req.Target?.GetValueOrDefault("preset");
        var undoKey = req.Target?.GetValueOrDefault("undo_key") ?? (name is null ? _latestUndo : Key(req, name));
        var snapshot = undoKey is null ? null : _undos.GetValueOrDefault(undoKey);
        if (snapshot is null || snapshot.Expired(Context.Clock.UtcNow)) return new PreparedAction { Summary = "Undo preset", Refusal = "Nothing to undo" };
        var generation = Interlocked.Increment(ref _generation);
        var started = Timestamp();
        _pendingTimer?.Dispose();
        _pendingTimer = null;
        return new PreparedAction
        {
            Summary = "Undo preset",
            Run = _ =>
            {
                var windows = Windows().ToDictionary(w => w.Hwnd);
                var items = new List<ActionItemResult>();
                foreach (var entry in snapshot.Before)
                {
                    var ok = Interlocked.Read(ref _generation) == generation && !UserChangedSince(entry.Hwnd, started) && windows.TryGetValue(entry.Hwnd, out var w) && !w.Frozen && !w.Hung && !w.Elevated && !IsFrozen(w.Pid) && Move(new WindowMove(entry.Hwnd, entry.WindowPx, entry.Show, false));
                    items.Add(new ActionItemResult { Item = "window", Status = ok ? ActionStatus.Ok : ActionStatus.Refused, Reason = ok ? null : "window unavailable" });
                }
                return new ActionResult { Action = req.Action, Status = items.All(i => i.Status == ActionStatus.Ok) ? ActionStatus.Ok : ActionStatus.Partial, Items = items };
            },
            Done = _ =>
            {
                if (Interlocked.Read(ref _generation) != generation) return;
                _undos.Remove(undoKey!);
                if (_latestUndo == undoKey) _latestUndo = _undos.Count == 0 ? null : _undos.MaxBy(pair => pair.Value.At).Key;
                ScheduleUndoExpiry();
                _pendingTimer?.Dispose();
                _pendingTimer = null;
            },
        };
    }

    private void ScheduleUndoExpiry()
    {
        _undoTimer?.Dispose();
        _undoTimer = null;
        if (_undos.Count == 0) return;
        var due = _undos.Values.Min(snapshot => snapshot.At + UndoSnapshot.Lifetime).AddTicks(1);
        _undoTimer = Context.Loop.At(due, () =>
        {
            foreach (var key in _undos.Where(pair => pair.Value.Expired(Context.Clock.UtcNow)).Select(pair => pair.Key).ToArray()) _undos.Remove(key);
            if (_latestUndo is not null && !_undos.ContainsKey(_latestUndo))
            {
                _latestUndo = _undos.Count == 0 ? null : _undos.MaxBy(pair => pair.Value.At).Key;
                Context.Router.Toasts.Withdraw("preset-undo");
            }
            ScheduleUndoExpiry();
        });
    }

    private static string Key(ActionRequest req, string preset)
    {
        var name = preset.ToUpperInvariant();
        var rule = req.Rule ?? string.Empty;
        return req.Origin == Origin.Rule ? "rule:" + rule.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + rule + ":" + name : "user:" + name;
    }

    private void OfferUndo(string undoKey)
    {
        const string tag = "preset-undo";
        var request = new ActionRequest { Action = "preset.undo", Origin = Origin.Toast, Toast = tag, Target = CardKit.Target("undo_key", undoKey) };
        Context.Router.Toasts.Withdraw(tag);
        Context.Router.Toasts.Offer(tag, request);
        Context.Notifier.Show(new ToastRequest { Tag = tag, Title = "Windows arranged", Buttons = [new ToastButton { Label = "Undo", Request = request }] });
    }

    public override IReadOnlyList<Card> Cards()
    {
        if (!Enabled) return [];
        IReadOnlyList<string> names = _store.Current.Presets.Select(p => p.Name).ToArray();
        var blocks = new List<Block> { new ListBlock { Label = "Presets", Items = Value.FromRawJson(JsonSerializer.Serialize(names, ContractsJsonContext.Default.IReadOnlyListString)), Max = 5 } };
        var actions = new List<CardAction>();
        for (var i = 0; i < names.Count; i++)
        {
            var target = CardKit.Target("preset", names[i]);
            actions.Add(new CardAction { Id = "apply-" + i, Label = "Apply " + names[i], Action = "preset.apply", Target = target });
            actions.Add(new CardAction { Id = "arrange-" + i, Label = "Arrange " + names[i], Action = "preset.arrange", Target = target });
        }
        return [new Card { Id = "module.presets", Source = CardKit.ModuleSource, Title = "Presets", Blocks = blocks, Actions = actions }];
    }
}
