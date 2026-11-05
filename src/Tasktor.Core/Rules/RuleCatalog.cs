namespace Tasktor.Core.Rules;

/// <summary>The names rules may use (U1 D-H6 + U2 H-S1 + U3 H-W5 + B's events). New names are catalog rows, not grammar changes (D-S1).</summary>
public static class RuleCatalog
{
    public static readonly IReadOnlySet<string> Metrics = new HashSet<string>(StringComparer.Ordinal)
    {
        "system.cpu_pct", "system.ram_used_pct", "system.commit_pct", "system.gpu_pct", "system.ram_available_gb",
        "disk.active_pct", "disk.free_pct", "disk.free_gb", "disk.fill_gb_per_h",
        "net.rx_bps", "net.tx_bps", "battery.pct", "battery.time_left_s",
        "self.private_mb", "self.cpu_pct", "jobs.running", "jobs.silent",
        "connectivity.drops_10m", "policy.false_alarm_pct", "policy.p1_distance", "policy.threshold_change",
    };

    /// <summary>States: boolean ones, plus <c>fullscreen</c> which takes a value via <c>is</c>.</summary>
    public static readonly IReadOnlySet<string> States = new HashSet<string>(StringComparer.Ordinal)
    {
        "on_battery", "has_battery", "low_memory", "in_call", "meeting_now", "event_mode", "focus_block", "offline",
        "captive_portal", "fullscreen", "tracking_paused", "capture_on", "jobs.running", "jobs.silent", "agent_needs_permission",
    };

    public static readonly IReadOnlySet<string> Events = new HashSet<string>(StringComparer.Ordinal)
    {
        "file.created", "window.missing", "agent.state", "net.dropped", "job.new", "job.silent", "job.stuck", "job.done",
        "job.failed", "job.hogging", "job.longer_than_usual", "process.started", "process.exited", "power.changed",
        "low_memory", "focus.block_end", "meeting.soon", "self.over_budget",
    };

    /// <summary>Learned stats: <c>silence</c> (B2.2), <c>duration</c> (B2.7), <c>fill_rate</c> (A5), <c>growth</c> (A6).</summary>
    public static readonly IReadOnlySet<string> LearnedStats = new HashSet<string>(StringComparer.Ordinal)
    {
        "silence", "duration", "fill_rate", "growth", "heartbeat",
    };

    public static readonly IReadOnlySet<string> PauseTargets = new HashSet<string>(StringComparer.Ordinal)
    {
        "refits", "tracking", "capture", "content",
    };

    public static readonly IReadOnlySet<string> KnownFolders = new HashSet<string>(StringComparer.Ordinal)
    {
        "Downloads", "Documents", "Desktop", "UserProfile", "LocalAppData", "Temp", "Pictures", "Music", "Videos",
    };
}
