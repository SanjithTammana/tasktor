using Tasktor.Contracts.Presets;
using Tasktor.Core.Apps;

namespace Tasktor.Core.Windows;

/// <summary>What happened to one preset slot (D-W6's per-slot result list).</summary>
public enum SlotOutcome : byte
{
    Move,
    Launch,
    NotOpen,
    SkippedFrozen,
    SkippedHung,
    SkippedAdmin,
}

/// <summary>One slot's decision: the window it claimed (if any), the target window rect in px and show state.</summary>
public sealed record SlotPlan(int Slot, SlotOutcome Outcome, WindowInfo? Window, RectPx? TargetPx, ShowState Show, string? Reason)
{
    public bool Focus { get; init; }
}

/// <summary>The previous placement of every window a plan moves (kept 30 min for Undo).</summary>
public sealed record UndoSnapshot(DateTimeOffset At, IReadOnlyList<(long Hwnd, RectPx WindowPx, ShowState Show)> Before)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    public bool Expired(DateTimeOffset now) => now - At > Lifetime;
}

/// <summary>
/// I5 / D-W5 / D-W6 planning, pure: match slots to open windows greedily per app in MRU order (each window claimed once;
/// class, nth-of-app and the user's own title pattern), skip frozen-job, hung and elevated windows, turn stored DIP rects
/// into window rects on today's monitors (visible frame + the window's own invisible border), and say which missing
/// slots launch. Launching is allowed only for user-initiated calls; rules may only arrange (reversible).
/// </summary>
public static class PresetPlanner
{
    public static List<SlotPlan> Plan(Preset preset, IReadOnlyList<WindowInfo> mruOrdered, MonitorMap monitors, bool userInitiated)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(mruOrdered);
        ArgumentNullException.ThrowIfNull(monitors);
        var claimed = new HashSet<long>();
        var plans = new List<SlotPlan>();
        for (var i = 0; i < preset.Slots.Count; i++)
        {
            var slot = preset.Slots[i];
            var candidates = mruOrdered.Where(w => SameApp(w, slot.App) && MatchesClass(w, slot.Match) && MatchesTitle(w, slot.Match)).ToList();
            var nth = slot.Match?.Nth ?? 0;
            // The slot's own nth window of that app if still free, else the most recent free one (greedy, each claimed once).
            var preferred = nth >= 0 && nth < candidates.Count && !claimed.Contains(candidates[nth].Hwnd) ? candidates[nth] : null;
            var window = preferred ?? candidates.FirstOrDefault(w => !claimed.Contains(w.Hwnd));
            var (_, frame) = monitors.Resolve(slot.Place);
            if (window is null)
            {
                var launch = userInitiated && slot.Launch is not null;
                plans.Add(new SlotPlan(i, launch ? SlotOutcome.Launch : SlotOutcome.NotOpen, null, frame, slot.Place.Show, launch ? null : "not open") { Focus = slot.Focus == true });
                continue;
            }

            claimed.Add(window.Hwnd);
            var skip = window.Frozen ? (SlotOutcome.SkippedFrozen, "frozen job")
                : window.Hung ? (SlotOutcome.SkippedHung, "not responding")
                : window.Elevated ? (SlotOutcome.SkippedAdmin, "admin window")
                : ((SlotOutcome, string)?)null;
            if (skip is { } s)
            {
                plans.Add(new SlotPlan(i, s.Item1, window, null, slot.Place.Show, s.Item2));
                continue;
            }

            plans.Add(new SlotPlan(i, SlotOutcome.Move, window, WindowRectFor(frame, window), slot.Place.Show, null) { Focus = slot.Focus == true });
        }

        return plans;
    }

    /// <summary>Target rects are visible-frame rects; add back the window's own invisible resize border (frame vs window rect).</summary>
    public static RectPx WindowRectFor(RectPx frame, WindowInfo w)
    {
        ArgumentNullException.ThrowIfNull(w);
        if (w.WindowPx.Area == 0 || w.FramePx.Area == 0)
        {
            return frame;
        }

        var left = w.FramePx.X - w.WindowPx.X;
        var top = w.FramePx.Y - w.WindowPx.Y;
        var right = w.WindowPx.Right - w.FramePx.Right;
        var bottom = w.WindowPx.Bottom - w.FramePx.Bottom;
        return new RectPx(frame.X - left, frame.Y - top, frame.W + left + right, frame.H + top + bottom);
    }

    /// <summary>The before-state of every window the plan moves.</summary>
    public static UndoSnapshot Snapshot(IEnumerable<SlotPlan> plans, DateTimeOffset now) =>
        new(now, [.. plans.Where(p => p.Outcome == SlotOutcome.Move && p.Window is not null).Select(p => (p.Window!.Hwnd, p.Window.WindowPx, p.Window.Show))]);

    /// <summary>Undo: move back every window that still exists, unless the snapshot is older than 30 min.</summary>
    public static List<(long Hwnd, RectPx WindowPx, ShowState Show)> Undo(UndoSnapshot snapshot, IReadOnlyCollection<long> openWindows, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(openWindows);
        return snapshot.Expired(now) ? [] : [.. snapshot.Before.Where(b => openWindows.Contains(b.Hwnd))];
    }

    private static bool SameApp(WindowInfo w, PresetApp app) =>
        string.Equals(w.AppId, app.AppId, StringComparison.OrdinalIgnoreCase) || (app.Exe is { } exe && w.Exe is { } we && string.Equals(Path.GetFileName(exe), we, StringComparison.OrdinalIgnoreCase));

    private static bool MatchesClass(WindowInfo w, PresetMatch? m) => m?.Class is not { } c || string.Equals(c, w.Class, StringComparison.Ordinal);

    private static bool MatchesTitle(WindowInfo w, PresetMatch? m) => m?.TitlePattern is not { } p || Glob.IsMatch(p, w.Title.Reveal());
}

/// <summary>
/// I5 "save what is open": a preset from the windows the user ticked, with app, class, nth-of-app and placement in DIPs
/// relative to each window's monitor work area. Titles are never captured (rule 5b); launch stays off until the user
/// confirms arguments on the review screen.
/// </summary>
public static class PresetBuilder
{
    public static Preset FromOpen(string name, IReadOnlyList<WindowInfo> mruOrdered, IReadOnlySet<long> ticked, MonitorMap monitors)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(mruOrdered);
        ArgumentNullException.ThrowIfNull(ticked);
        ArgumentNullException.ThrowIfNull(monitors);
        var slots = new List<PresetSlot>();
        foreach (var w in mruOrdered.Where(w => ticked.Contains(w.Hwnd)))
        {
            var nth = mruOrdered.TakeWhile(x => x.Hwnd != w.Hwnd).Count(x => string.Equals(x.AppId, w.AppId, StringComparison.OrdinalIgnoreCase) && x.Class == w.Class);
            var monitor = monitors.Of(w.FramePx);
            slots.Add(new PresetSlot
            {
                App = new PresetApp { AppId = w.AppId, Exe = w.Exe },
                Match = new PresetMatch { Class = w.Class, Nth = nth },
                Place = new PresetPlace { Monitor = monitor.DevicePath, Rect = MonitorMap.ToDip(w.FramePx, monitor), Show = w.Show },
            });
        }

        return new Preset { Name = name.Trim(), Slots = slots };
    }
}
