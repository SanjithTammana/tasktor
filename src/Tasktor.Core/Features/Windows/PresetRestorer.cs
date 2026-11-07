using System.Text.Json;
using System.Text.Json.Nodes;
using Tasktor.Contracts;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Presets;
using Tasktor.Contracts.Workflow;
using Tasktor.Core.Actions;
using Tasktor.Core.Windows;

namespace Tasktor.Core.Features.Windows;

public sealed record WindowMove(long Hwnd, RectPx Rect, ShowState Show, bool Focus);

/// <summary>
/// U3 D-W5: restores user presets; rules may arrange existing windows but never start an app. Launched apps' windows
/// are matched on <see cref="WindowEventKind.Shown"/> events within 20 s (one catch-up pass covers a window shown before
/// the lifecycle hook was in); a window the user moves, sizes or closes meanwhile is dropped. The lifecycle lease is held
/// only while a restore waits.
/// </summary>
public sealed class PresetRestorer(FeatureContext context) : Feature(context)
{
    private readonly Dictionary<string, UndoSnapshot> _undos = new(StringComparer.Ordinal);
    private string? _latestUndo;
    private long _generation;
    private readonly WindowInterventions _interventions = new();
    private Func<long, long, bool>? _userChanged;
    private LaunchWait? _wait;
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

    /// <summary>Window events from the shared <c>WinEventSource</c> (lifecycle kinds arrive only under <see cref="LeaseLifecycle"/>).</summary>
    public IEventSource<WindowEvent>? Events { get; init; }

    /// <summary>Takes <c>WinEventSource</c>'s show / destroy / move-size lease; null where there is no source.</summary>
    public Func<IDisposable>? LeaseLifecycle { get; init; }

    /// <summary>Did the user move / size / close the window since a monotonic time? Defaults to the event-fed tracker.</summary>
    public Func<long, long, bool> UserChangedSince
    {
        get => _userChanged ?? _interventions.ChangedSince;
        set => _userChanged = value;
    }

    /// <summary>The wait for launched windows to show (one at a time; a new restore or undo replaces it).</summary>
    private sealed class LaunchWait(Preset preset, List<int> pending, HashSet<long> existing, DateTimeOffset deadline, long generation, long started, string undoKey, IDisposable? lease)
    {
        public Preset Preset { get; } = preset;

        public List<int> Pending { get; } = pending;

        public HashSet<long> Existing { get; } = existing;

        public DateTimeOffset Deadline { get; } = deadline;

        public long Generation { get; } = generation;

        public long Started { get; } = started;

        public string UndoKey { get; } = undoKey;

        public IDisposable? Lease { get; } = lease;

        public IDisposable? DeadlineTimer { get; set; }

        public IDisposable? PassTimer { get; set; }

        public bool Running { get; set; }

        public bool Dirty { get; set; }
    }

    public Func<long> Timestamp { get; set; } = () => Environment.TickCount64;

    /// <summary>A process's command line, for the launch a saved slot may use ("open it if it is not running").</summary>
    public Func<uint, string?> CommandLine { get; set; } = _ => null;

    public Func<uint, bool> IsFrozen { get; set; } = _ => false;

    protected override void OnEnable()
    {
        Context.Router.Register("preset.arrange", Arrange);
        Context.Router.Register("preset.apply", Apply);
        Context.Router.Register("preset.undo", Undo);
        Context.RegisterPull("presets", _ => PullPresets());
        Context.RegisterPull("open_windows", _ => JsonSerializer.SerializeToNode(OpenWindows(), ContractsJsonContext.Default.OpenWindowsReport));
        Context.Router.Register("preset.save", Save);
        Events?.Start(e =>
        {
            _interventions.On(e); // on the source thread: a drag is visible to an in-flight restore at once
            if (e.Kind == WindowEventKind.Shown)
            {
                Context.Loop.Post(OnShown);
            }
        });
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
        Events?.Stop();
        EndWait();
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
        EndWait();
        var generation = Interlocked.Increment(ref _generation);
        var undoKey = Key(req, preset.Name);
        var started = Timestamp();
        var deadline = Context.Clock.UtcNow.AddSeconds(20);
        UndoSnapshot? before = null;
        IDisposable? lease = null;
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
                        lease ??= LeaseLifecycle?.Invoke(); // before the launch, so its window's show event is seen
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
                if (Interlocked.Read(ref _generation) != generation)
                {
                    lease?.Dispose();
                    return;
                }

                if (before is { Before.Count: > 0 })
                {
                    _undos[undoKey] = before;
                    _latestUndo = undoKey;
                    ScheduleUndoExpiry();
                    OfferUndo(undoKey);
                }
                if (pending.Count > 0) StartWait(new LaunchWait(preset, pending, existing, deadline, generation, started, undoKey, lease));
                else lease?.Dispose();
            },
        };
    }

    /// <summary>
    /// The deep window's Windows page (U4 D-G3): each preset's name, slot and launch counts, hotkey and whether an undo is
    /// waiting for it. Never an app path, argument, window title or match pattern.
    /// </summary>
    /// <summary>
    /// The windows open now for "Save open windows as a preset" (U4 preset review): app, title, monitor, size, and the
    /// exact launch each would use. Personal data for the user's own window (owner-approved); never logged.
    /// </summary>
    public OpenWindowsReport OpenWindows()
    {
        var monitors = Monitors();
        var map = new MonitorMap(monitors);
        var list = monitors.ToList();
        return new OpenWindowsReport
        {
            Windows =
            [
                .. Windows().Where(w => !IsFrozen(w.Pid)).Select(w => new OpenWindow
                {
                    Id = w.Hwnd.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    App = w.AppId,
                    Title = w.Title.Reveal() is { Length: > 0 } t ? t : null,
                    Monitor = list.Count == 0 ? 0 : list.FindIndex(m => m.DevicePath == map.Of(w.FramePx).DevicePath) + 1,
                    Width = w.FramePx.W,
                    Height = w.FramePx.H,
                    Launch = LaunchOf(w),
                }),
            ],
        };
    }

    private PresetLaunch? LaunchOf(WindowInfo w)
    {
        if (CommandLine(w.Pid) is not { Length: > 0 } line || CommandLineArgs.Split(line) is not { Count: > 0 } argv)
        {
            return null;
        }

        return new PresetLaunch { Exe = w.Exe ?? argv[0], Args = argv.Count > 1 ? [.. argv.Skip(1)] : null };
    }

    /// <summary>
    /// <c>preset.save name=… windows=id,id launch=id,…</c> (people only): the ticked windows as a preset; a slot whose id is
    /// in <c>launch</c> opens its app if it is missing, with the command line its process runs now (re-read here, never
    /// taken from the request). A preset with the same name is replaced.
    /// </summary>
    private PreparedAction Save(ActionRequest req)
    {
        var name = req.Args?.GetValueOrDefault("name")?.Trim();
        static HashSet<long> Ids(string? csv) => [.. (csv ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => long.TryParse(x, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0).Where(v => v != 0)];
        var ticked = Ids(req.Args?.GetValueOrDefault("windows"));
        var launch = Ids(req.Args?.GetValueOrDefault("launch"));
        if (name is not { Length: > 0 and <= 60 })
        {
            return new PreparedAction { Summary = "Save as preset", Refusal = "A preset name has 1–60 characters" };
        }

        var windows = Windows().Where(w => !IsFrozen(w.Pid)).ToList();
        var chosen = windows.Where(w => ticked.Contains(w.Hwnd)).ToList();
        if (chosen.Count == 0)
        {
            return new PreparedAction { Summary = "Save as preset", Refusal = "Tick at least one open window" };
        }

        return new PreparedAction
        {
            Summary = "Save " + name,
            Immediate = new ActionResult { Action = req.Action, Status = ActionStatus.Ok, Message = "Saved " + name + ": " + chosen.Count + (chosen.Count == 1 ? " window" : " windows") + (chosen.Count(w => launch.Contains(w.Hwnd) && LaunchOf(w) is not null) is var n and > 0 ? ", " + n + " opens if missing" : string.Empty) },
            Done = _ =>
            {
                var preset = PresetBuilder.FromOpen(name, windows, ticked, new MonitorMap(Monitors()));
                var slots = preset.Slots.Zip(chosen, (slot, w) => launch.Contains(w.Hwnd) && LaunchOf(w) is { } l ? slot with { Launch = l } : slot).ToList();
                var file = _store.Current;
                _store.Save(file with { Presets = [.. file.Presets.Where(p => !string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase)), preset with { Slots = slots }] });
                Context.UpdateState(s => s);
            },
        };
    }

    private JsonNode PullPresets()
    {
        var now = Context.Clock.UtcNow;
        var list = new JsonArray();
        foreach (var p in _store.Current.Presets)
        {
            var key = "user:" + p.Name.ToUpperInvariant();
            list.Add((JsonNode)new JsonObject
            {
                ["name"] = p.Name,
                ["slots"] = p.Slots.Count,
                ["launches"] = p.Slots.Count(s => s.Launch is not null),
                ["layouts"] = p.Fancyzones?.Count ?? 0,
                ["hotkey"] = p.Hotkey,
                ["can_undo"] = _undos.TryGetValue(key, out var snapshot) && !snapshot.Expired(now),
            });
        }

        return new JsonObject { ["presets"] = list };
    }

    private void StartWait(LaunchWait wait)
    {
        EndWait();
        _wait = wait;
        wait.DeadlineTimer = Context.Loop.At(wait.Deadline, EndWait);
        SchedulePass(wait, TimeSpan.FromMilliseconds(500)); // catch-up: a window shown before the lifecycle hook was in
    }

    /// <summary>A top-level window was shown (loop): match the waiting slots shortly after (bursts coalesce).</summary>
    public void OnShown()
    {
        if (_wait is not { } wait)
        {
            return;
        }

        if (wait.Running)
        {
            wait.Dirty = true;
        }
        else if (wait.PassTimer is null)
        {
            SchedulePass(wait, TimeSpan.FromMilliseconds(300)); // let the new window settle its first placement
        }
    }

    private void SchedulePass(LaunchWait wait, TimeSpan delay) =>
        wait.PassTimer = Context.Loop.At(Context.Clock.UtcNow + delay, () => Pass(wait));

    private void EndWait()
    {
        if (_wait is not { } wait)
        {
            return;
        }

        _wait = null;
        wait.DeadlineTimer?.Dispose();
        wait.PassTimer?.Dispose();
        wait.Lease?.Dispose();
    }

    private void Pass(LaunchWait wait)
    {
        wait.PassTimer = null;
        if (!Enabled || _wait != wait || Interlocked.Read(ref _generation) != wait.Generation || Context.Clock.UtcNow >= wait.Deadline)
        {
            return;
        }

        wait.Running = true;
        var deadline = wait.Deadline;
        var generation = wait.Generation;
        int[] pending = [.. wait.Pending];
        var existing = wait.Existing.ToHashSet();
        Context.OffLoop(() =>
        {
            var monitors = Monitors();
            var windows = Windows().Where(w => !existing.Contains(w.Hwnd)).Select(w => w with { Frozen = w.Frozen || IsFrozen(w.Pid) }).ToList();
            var found = new List<(int Slot, SlotPlan Plan)>();
            var dropped = new List<(int Slot, long Hwnd)>();
            if (monitors.Count > 0)
            {
                var subset = wait.Preset with { Slots = pending.Select(i => wait.Preset.Slots[i] with { Launch = null }).ToList() };
                foreach (var plan in PresetPlanner.Plan(subset, windows, new MonitorMap(monitors), false))
                {
                    if (plan.Window is not { } w || Interlocked.Read(ref _generation) != generation || Context.Clock.UtcNow >= deadline) continue;
                    var slot = pending[plan.Slot];
                    if (plan.Outcome == SlotOutcome.Move && plan.TargetPx is { } rect && !UserChangedSince(w.Hwnd, wait.Started) && !IsFrozen(w.Pid) && Move(new WindowMove(w.Hwnd, rect, plan.Show, plan.Focus))) found.Add((slot, plan));
                    else dropped.Add((slot, w.Hwnd));
                }
            }

            Context.Loop.Post(() =>
            {
                wait.Running = false;
                if (!Enabled || _wait != wait || Interlocked.Read(ref _generation) != generation) return;
                foreach (var (slot, hwnd) in dropped)
                {
                    wait.Pending.Remove(slot);
                    wait.Existing.Add(hwnd);
                }

                foreach (var (slot, plan) in found)
                {
                    wait.Pending.Remove(slot);
                    wait.Existing.Add(plan.Window!.Hwnd);
                    var entry = (plan.Window.Hwnd, plan.Window.WindowPx, plan.Window.Show);
                    var previous = _undos.GetValueOrDefault(wait.UndoKey);
                    _undos[wait.UndoKey] = new UndoSnapshot(Context.Clock.UtcNow, [.. (previous?.Before ?? []), entry]);
                    _latestUndo = wait.UndoKey;
                }

                if (found.Count > 0) { ScheduleUndoExpiry(); OfferUndo(wait.UndoKey); }
                if (wait.Pending.Count == 0) EndWait();
                else if (wait.Dirty) { wait.Dirty = false; SchedulePass(wait, TimeSpan.FromMilliseconds(300)); }
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
        EndWait();
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
                EndWait();
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
