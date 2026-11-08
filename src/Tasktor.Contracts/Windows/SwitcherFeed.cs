namespace Tasktor.Contracts.Windows;

/// <summary>
/// F2 (U3 D-W4): what the agent gives <c>tasktor-shell</c> for the switcher, by window handle and process id only. The
/// shell enumerates windows itself when it opens, so titles never cross the pipe and nothing about the switcher is stored.
/// </summary>
public sealed record SwitcherFeed
{
    /// <summary>Window handles, most recently in front first (in memory only, from foreground events).</summary>
    public IReadOnlyList<long> Mru { get; set; } = [];

    /// <summary>Watched jobs whose processes may own windows: a tile gets a job badge, or offers Resume first when frozen.</summary>
    public IReadOnlyList<SwitcherJob> Jobs { get; set; } = [];

    /// <summary>The switcher's hotkey (Settings → Windows, default <c>Alt+`</c>).</summary>
    public string Hotkey { get; set; } = "Alt+`";

    /// <summary>"This desktop only": windows on other virtual desktops are left out instead of badged.</summary>
    public bool ThisDesktopOnly { get; set; }
}

/// <summary>One watched job: its id (for <c>job.resume</c>), its label, whether it is frozen and its processes.</summary>
public sealed record SwitcherJob
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    public bool Frozen { get; init; }

    public IReadOnlyList<uint> Pids { get; set; } = [];
}
