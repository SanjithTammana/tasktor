using Tasktor.Contracts.Actions;

namespace Tasktor.Core.Actions;

/// <summary>Where an action may be offered (U4 H-G3).</summary>
[Flags]
public enum Surface
{
    None = 0,
    Flyout = 1,
    Widget = 2,
    Tray = 4,
    Toast = 8,
    Cli = 16,
    All = Flyout | Widget | Tray | Toast | Cli,
}

/// <summary>
/// One catalog row. <see cref="Undo"/> names the action that reverses it (rules' <c>undo</c>). <see cref="UserOnly"/>
/// actions run only from a person (click, hotkey, CLI), never from a rule: <c>run</c>, <c>preset.apply</c>.
/// <see cref="LiftsBlock"/> actions lift a capture block: only the user may run them (U4 D-G6).
/// <see cref="ConfirmArgs"/> lists args that make a read action need a confirm like a destructive one (export include flags).
/// </summary>
public sealed record ActionDef(
    string Id,
    ActionClass Class,
    string Label,
    Surface Surfaces = Surface.Flyout | Surface.Cli,
    string? Undo = null,
    bool UserOnly = false,
    bool LiftsBlock = false,
    bool ProcessTargets = false,
    bool BuiltinRulesOnly = false,
    string[]? ConfirmArgs = null);

/// <summary>The closed action list (U1 D-H4). Unknown actions are rejected by the router.</summary>
public static class ActionCatalog
{
    private static readonly ActionDef[] Rows =
    [
        // read
        new("status", ActionClass.Read, "Status", Surface.Cli),
        new("summary", ActionClass.Read, "Summary", Surface.Cli | Surface.Flyout),
        new("open", ActionClass.Read, "Open", Surface.All),
        new("export.data", ActionClass.Read, "Export my data", Surface.Flyout | Surface.Cli, ConfirmArgs: ["include_raw", "include_capture"]),
        new("settings.export", ActionClass.Read, "Export settings", Surface.Flyout | Surface.Cli),
        new("settings.page", ActionClass.Reversible, "Show or hide a page", Surface.Flyout, UserOnly: true),
        new("settings.hotkey", ActionClass.Reversible, "Set the command line's hotkey", Surface.Flyout | Surface.Cli, UserOnly: true),
        new("log.bundle", ActionClass.Read, "Log bundle", Surface.Flyout | Surface.Cli),
        new("proc.open_location", ActionClass.Read, "Open file location", Surface.Flyout | Surface.Widget, ProcessTargets: true),
        new("proc.open_taskmgr", ActionClass.Read, "Open Task Manager", Surface.Flyout | Surface.Widget),
        new("job.log", ActionClass.Read, "Open log", Surface.Flyout | Surface.Widget | Surface.Toast),
        new("job.folder", ActionClass.Read, "Open folder", Surface.Flyout | Surface.Widget | Surface.Toast),
        new("disk.open", ActionClass.Read, "Open in Explorer", Surface.Flyout | Surface.Widget | Surface.Toast),
        new("disk.storage_settings", ActionClass.Read, "Open Storage settings", Surface.Flyout | Surface.Toast),
        new("disk.scan", ActionClass.Read, "Biggest folders", Surface.Flyout | Surface.Widget | Surface.Toast | Surface.Cli),
        new("space.report", ActionClass.Read, "Space report", Surface.Flyout | Surface.Cli),
        new("space.copy_cleanup", ActionClass.Read, "Copy cleanup command", Surface.Flyout),
        new("battery.settings", ActionClass.Read, "Battery settings", Surface.Flyout | Surface.Toast),
        new("url.open", ActionClass.Read, "Join", Surface.Flyout | Surface.Widget | Surface.Toast),
        new("net.signin", ActionClass.Read, "Open browser", Surface.Flyout | Surface.Toast),
        new("agent.show", ActionClass.Read, "Show", Surface.Flyout | Surface.Widget | Surface.Toast),

        // reversible: priority / efficiency / caps (A3, B3, I4)
        new("priority.set", ActionClass.Reversible, "Lower priority", Surface.Flyout | Surface.Widget | Surface.Toast | Surface.Cli, Undo: "priority.restore", ProcessTargets: true),
        new("priority.restore", ActionClass.Reversible, "Restore priority", Surface.Flyout | Surface.Widget | Surface.Cli, ProcessTargets: true),
        new("efficiency.set", ActionClass.Reversible, "Efficiency mode", Surface.Flyout | Surface.Widget | Surface.Toast | Surface.Cli, Undo: "efficiency.restore", ProcessTargets: true),
        new("efficiency.restore", ActionClass.Reversible, "Efficiency off", Surface.Flyout | Surface.Widget | Surface.Cli, ProcessTargets: true),
        new("cores.cap", ActionClass.Reversible, "Cap cores", Surface.Flyout | Surface.Cli, Undo: "cores.release", ProcessTargets: true),
        new("cores.release", ActionClass.Reversible, "Release cap", Surface.Flyout | Surface.Cli),
        new("agent.lower_newest", ActionClass.Reversible, "Lower the newest", Surface.Flyout | Surface.Toast, Undo: "priority.restore", ProcessTargets: true),
        new("cores.release_all", ActionClass.Reversible, "Release all caps", Surface.Flyout | Surface.Widget | Surface.Cli),

        // reversible: pause / resume (B3.7, D-H6 pause targets)
        new("job.pause", ActionClass.Reversible, "Pause", Surface.Flyout | Surface.Widget | Surface.Cli, Undo: "job.resume", ProcessTargets: true),
        new("job.resume", ActionClass.Reversible, "Resume", Surface.Flyout | Surface.Widget | Surface.Toast | Surface.Cli, ProcessTargets: true),
        new("proc.freeze", ActionClass.Reversible, "Freeze", Surface.Flyout, Undo: "proc.resume", ProcessTargets: true),
        new("proc.resume", ActionClass.Reversible, "Resume", Surface.Flyout | Surface.Toast, ProcessTargets: true),
        new("job.pin", ActionClass.Reversible, "Watch as job", Surface.Flyout | Surface.Cli, Undo: "job.unpin"),
        new("job.unpin", ActionClass.Reversible, "Stop watching", Surface.Flyout | Surface.Cli),
        new("job.cancel", ActionClass.Reversible, "Cancel", Surface.Flyout | Surface.Widget),
        new("refits.pause", ActionClass.Reversible, "Pause refits", Surface.Flyout | Surface.Tray | Surface.Cli, Undo: "refits.resume"),
        new("refits.resume", ActionClass.Reversible, "Resume refits", Surface.Flyout | Surface.Tray | Surface.Cli),
        new("tracking.pause", ActionClass.Reversible, "Pause tracking", Surface.Flyout | Surface.Tray | Surface.Widget | Surface.Cli, Undo: "tracking.resume"),
        new("tracking.resume", ActionClass.Reversible, "Resume tracking", Surface.Flyout | Surface.Tray | Surface.Cli),
        new("plugin.pause", ActionClass.Reversible, "Pause plugin", Surface.Flyout | Surface.Cli, Undo: "plugin.resume"),
        new("plugin.resume", ActionClass.Reversible, "Resume plugin", Surface.Flyout | Surface.Cli),
        new("plugin.action", ActionClass.Reversible, "Plugin action", Surface.Flyout | Surface.Widget | Surface.Toast),
        new("plugin.enable", ActionClass.Reversible, "Enable plugin", Surface.Flyout, UserOnly: true),
        new("pack.rule", ActionClass.Reversible, "Tick a pack rule", Surface.Flyout, UserOnly: true),
        new("plugin.enable_pack", ActionClass.Reversible, "Enable plugin pack", Surface.Flyout, UserOnly: true),
        // Turning a Module on records its consent (U1 H1); both are the user's call, never a rule's.
        new("module.enable", ActionClass.Reversible, "Turn on", Surface.Flyout, UserOnly: true),
        new("module.disable", ActionClass.Reversible, "Turn off", Surface.Flyout | Surface.Cli, UserOnly: true),
        new("plugin.disable", ActionClass.Reversible, "Turn off plugin", Surface.Flyout | Surface.Cli),

        // capture blocks (U4 D-G6): rules may add, never lift
        new("capture.pause", ActionClass.Reversible, "Pause capture", Surface.Flyout | Surface.Tray | Surface.Widget | Surface.Cli),
        new("capture.resume", ActionClass.Reversible, "Resume capture", Surface.Flyout | Surface.Tray | Surface.Cli, LiftsBlock: true),
        new("capture.exclude", ActionClass.Reversible, "Don't capture this app", Surface.Flyout | Surface.Tray | Surface.Widget),
        new("capture.unexclude", ActionClass.Reversible, "Capture this app again", Surface.Flyout, LiftsBlock: true),
        new("capture.tab_pause", ActionClass.Reversible, "Pause for this tab", Surface.Flyout),
        new("capture.extension_setup", ActionClass.Read, "Set up the browser extension", Surface.Flyout, UserOnly: true), // registers the native host (HKCU)

        // reversible: alerts, cards, rules, focus, presets, learned
        new("alert.snooze", ActionClass.Reversible, "Snooze", Surface.Flyout | Surface.Toast | Surface.Tray),
        new("alerts.snooze_all", ActionClass.Reversible, "Snooze alerts for 1 hour", Surface.Tray | Surface.Flyout),
        new("rules.edit", ActionClass.Read, "Edit rules.json", Surface.Flyout | Surface.Cli),
        new("rule.enable", ActionClass.Reversible, "Turn rule on", Surface.Flyout | Surface.Cli, Undo: "rule.disable", UserOnly: true),
        new("rule.disable", ActionClass.Reversible, "Turn rule off", Surface.Flyout | Surface.Cli, Undo: "rule.enable", UserOnly: true),
        new("rule.its_fine", ActionClass.Reversible, "It's fine", Surface.Toast | Surface.Flyout),
        new("rule.undo", ActionClass.Reversible, "Undo", Surface.Flyout | Surface.Toast | Surface.Widget),
        new("card.pin", ActionClass.Reversible, "Pin", Surface.Flyout, Undo: "card.unpin"),
        new("card.unpin", ActionClass.Reversible, "Unpin", Surface.Flyout),
        new("badge.set", ActionClass.Reversible, "Badge", Surface.None, Undo: "badge.clear"),
        new("badge.clear", ActionClass.Reversible, "Clear badge", Surface.None),
        new("focus.start", ActionClass.Reversible, "Start", Surface.Flyout | Surface.Widget | Surface.Tray | Surface.Cli),
        new("focus.pause", ActionClass.Reversible, "Pause", Surface.Flyout | Surface.Widget),
        new("focus.skip", ActionClass.Reversible, "Skip", Surface.Flyout | Surface.Widget | Surface.Toast),
        new("focus.stop", ActionClass.Reversible, "Stop", Surface.Flyout | Surface.Widget | Surface.Toast),
        new("focus.extend", ActionClass.Reversible, "+5 min", Surface.Toast | Surface.Flyout),
        new("focus.next", ActionClass.Reversible, "Next", Surface.Toast | Surface.Flyout | Surface.Widget),
        new("note.save", ActionClass.Reversible, "Save note", Surface.Flyout),
        new("note.pin", ActionClass.Reversible, "Pin note", Surface.Flyout, Undo: "note.unpin"),
        new("note.unpin", ActionClass.Reversible, "Unpin note", Surface.Flyout),
        new("event_mode.start", ActionClass.Reversible, "Start event mode", Surface.Flyout | Surface.Tray | Surface.Cli, Undo: "event_mode.stop"),
        new("event_mode.stop", ActionClass.Reversible, "Stop event mode", Surface.Flyout | Surface.Tray | Surface.Cli),
        new("preset.arrange", ActionClass.Reversible, "Arrange", Surface.Flyout | Surface.Tray | Surface.Cli, Undo: "preset.undo"),
        new("preset.undo", ActionClass.Reversible, "Undo", Surface.Flyout | Surface.Toast),
        new("preset.apply", ActionClass.Reversible, "Apply", Surface.Flyout | Surface.Tray | Surface.Cli, UserOnly: true),
        new("learned.use_fallback", ActionClass.Reversible, "Use default", Surface.None, BuiltinRulesOnly: true),
        new("learned.relearn", ActionClass.Reversible, "Relearn", Surface.Flyout | Surface.Cli, BuiltinRulesOnly: true),
        new("learning.set", ActionClass.Reversible, "Learn from my jobs", Surface.Flyout | Surface.Cli),
        new("workflow.rename", ActionClass.Reversible, "Rename", Surface.Flyout | Surface.Cli),
        new("workflow.merge", ActionClass.Reversible, "Merge", Surface.Flyout | Surface.Cli),
        new("workflow.wrong", ActionClass.Reversible, "That's wrong", Surface.Flyout | Surface.Widget),
        new("project.map", ActionClass.Reversible, "Assign project", Surface.Flyout | Surface.Cli),
        new("log.verbose", ActionClass.Reversible, "Verbose log", Surface.Flyout | Surface.Cli),
        new("quit", ActionClass.Reversible, "Quit Tasktor", Surface.Tray | Surface.Flyout),

        // destructive: close / end / run / forget / replace
        new("proc.close", ActionClass.Destructive, "Close", Surface.Flyout | Surface.Widget | Surface.Toast | Surface.Cli, ProcessTargets: true),
        new("proc.end", ActionClass.Destructive, "End…", Surface.Flyout | Surface.Widget | Surface.Cli, ProcessTargets: true),
        new("proc.end_tree", ActionClass.Destructive, "End tree…", Surface.Flyout | Surface.Widget | Surface.Cli, ProcessTargets: true),
        new("app.close", ActionClass.Destructive, "Close", Surface.Flyout | Surface.Widget | Surface.Toast, ProcessTargets: true),
        new("app.end", ActionClass.Destructive, "End…", Surface.Flyout | Surface.Widget, ProcessTargets: true),
        new("job.end", ActionClass.Destructive, "End…", Surface.Flyout | Surface.Widget | Surface.Cli, ProcessTargets: true),
        new("run", ActionClass.Destructive, "Run", Surface.Toast, UserOnly: true),
        new("learned.forget", ActionClass.Destructive, "Forget", Surface.Flyout | Surface.Cli),
        new("forget", ActionClass.Destructive, "Forget", Surface.Flyout | Surface.Cli),
        new("capture.forget", ActionClass.Destructive, "Forget", Surface.Flyout | Surface.Widget | Surface.Cli),
        new("note.delete", ActionClass.Destructive, "Delete note…", Surface.Flyout),
        new("agent.hooks.install", ActionClass.Destructive, "Set up hooks…", Surface.Flyout | Surface.Cli),
        new("agent.hooks.remove", ActionClass.Destructive, "Remove hooks…", Surface.Flyout | Surface.Cli),
        new("settings.import", ActionClass.Destructive, "Import settings", Surface.Flyout | Surface.Cli),
        new("settings.reset", ActionClass.Destructive, "Reset settings", Surface.Flyout | Surface.Cli),
        new("uninstall", ActionClass.Destructive, "Uninstall", Surface.Cli),
    ];

    private static readonly Dictionary<string, ActionDef> ById = Rows.ToDictionary(r => r.Id, StringComparer.Ordinal);

    public static IReadOnlyList<ActionDef> All => Rows;

    public static ActionDef? Find(string id) => ById.GetValueOrDefault(id);

    /// <summary>Rows offered on <paramref name="surface"/> (the tray menu is built from these, U4 D-G4).</summary>
    public static IEnumerable<ActionDef> ForSurface(Surface surface) => Rows.Where(r => (r.Surfaces & surface) != 0);
}
