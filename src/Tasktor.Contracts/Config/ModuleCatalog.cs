namespace Tasktor.Contracts.Config;

/// <summary>
/// One Module as its consent dialog describes it (U1 H1: "one dialog that says what it reads and where it is stored").
/// <see cref="Network"/> names what it contacts, or is null when it makes no connection (privacy rule 1).
/// </summary>
public sealed record ModuleInfo(string Id, string Name, string Reads, string Stores, string? Network = null);

/// <summary>The Modules a user can turn on (all off by default; 01-catalog, U2), in the Settings page's order.</summary>
public static class ModuleCatalog
{
    private const string Nothing = "Nothing is kept beyond what the card shows.";

    public static IReadOnlyList<ModuleInfo> All { get; } =
    [
        new("tracking", "Activity tracking", "Which app is in front and its window title, idle time, and apps starting and stopping.", "On this PC in track\\: events for 30 days, summaries until you forget them."),
        new("capture", "Capture", "What you type and the text on screen, except in the apps, sites and fields you block.", "On this PC in capture\\, encrypted, for 7 days."),
        new("focus", "Focus", "Your focus sessions and the notes you write.", "On this PC: notes\\ and the focus history."),
        new("projects", "Time per project", "Your tracked activity, grouped by the project names you give it.", "On this PC, beside the tracking data."),
        new("presets", "Window presets", "The position and size of the windows you save in a preset.", "On this PC in presets.json."),
        new("switcher", "Window switcher", "Which window was in front most recently, and your open windows with their titles, shown only while the switcher is open.", "Nothing: the order lives in memory only."),
        new("windows_watch", "Required-window watch", "Whether the windows you choose to watch are still open.", Nothing),
        new("agents", "AI agent watch", "Which coding agents run and whether they are busy.", "On this PC in agents\\, while they run."),
        new("ports", "Dev ports", "Which programs listen on network ports.", Nothing),
        new("connectivity", "Connectivity watch", "Wi-Fi changes, drops and captive portals, as Windows reports them.", "On this PC: drops per hour for 30 days."),
        new("batteries", "Device batteries", "Battery levels of paired Bluetooth devices.", Nothing),
        new("deadlines", "Deadlines", "The deadlines file or calendar you choose.", Nothing, "the calendar address you give, if it is a web address"),
        new("next_event", "Next event", "Your calendar's events for today.", Nothing, "the calendar address you give"),
        new("event_mode", "Event mode", "The time window you set for an event.", Nothing),
        new("meeting_guard", "Meeting guard", "Whether a call app is in a call; it lowers the jobs you opted in.", Nothing),
        new("folders", "Repo hygiene", "Whether repositories under the folders you pick have uncommitted or unpushed work.", Nothing),
    ];

    public static ModuleInfo? Find(string? id) => All.FirstOrDefault(m => m.Id == id);
}

/// <summary>The agent's <c>modules</c> pull: each Module's state now and after the next restart.</summary>
public sealed record ModulesReport
{
    public required IReadOnlyList<ModuleState> Modules { get; init; }
}

/// <summary><see cref="Running"/>: on since the agent started; <see cref="On"/>: on in config.json (applies at the next start).</summary>
public sealed record ModuleState
{
    public required string Id { get; init; }

    public bool On { get; init; }

    public bool Running { get; init; }

    public bool Consented { get; init; }
}
