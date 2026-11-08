using Tasktor.Capture.Deep;

namespace Tasktor.Capture.Media;

/// <summary>
/// C20.1: maps an SMTC source (its AUMID) to the app id the audio sessions use (the lower-cased exe), so metadata and
/// peaks meet in <see cref="MediaSessionLogic"/>; and maps SMTC playback status without inventing one.
/// </summary>
public static class MediaAppMap
{
    // Browsers register a short AUMID of their own rather than their exe.
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Chrome"] = "chrome.exe",
        ["MSEdge"] = "msedge.exe",
        ["Brave"] = "brave.exe",
        ["Opera"] = "opera.exe",
        ["Vivaldi"] = "vivaldi.exe",
        ["Firefox"] = "firefox.exe",
    };

    /// <summary>The app id for an AUMID: an exe path or name → its file name; a packaged app → its package family; else null.</summary>
    public static string? FromAumid(string? aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return null;
        var a = aumid.Trim();
        if (Known.TryGetValue(a, out var known)) return known;
        if (a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return Path.GetFileName(a.Replace('/', '\\').Split('\\')[^1]).ToLowerInvariant();
        var bang = a.IndexOf('!', StringComparison.Ordinal);
        return (bang > 0 ? a[..bang] : a).ToLowerInvariant();
    }

    /// <summary>SMTC's playback status (Closed 0, Opened 1, Changing 2, Stopped 3, Playing 4, Paused 5) or null: never a guess.</summary>
    public static MediaStatus? Status(int smtc) => smtc switch
    {
        3 => MediaStatus.Stopped,
        4 => MediaStatus.Playing,
        5 => MediaStatus.Paused,
        _ => null,
    };
}

/// <summary>One audio session's sample: its process, peak (0–1) and whether it is active.</summary>
public readonly record struct AudioSessionSample(uint Pid, float Peak, bool Active);

/// <summary>
/// C20.1 / §5.6: once a second, every render session's peak, folded to one peak per app (the largest across its active
/// sessions). An app whose active sessions all ended or went inactive gets "audio stopped" once. Sessions whose process
/// maps to no app are ignored. Single-threaded (the meter thread).
/// </summary>
public sealed class AudioMeterLogic(Func<uint, string?> appOf, Action<string, DateTimeOffset, float> peak, Action<string> stopped)
{
    private readonly Dictionary<uint, string?> _apps = [];
    private HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);

    public bool AnyActive => _active.Count > 0;

    public void Sample(DateTimeOffset at, IReadOnlyList<AudioSessionSample> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        var max = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sessions)
        {
            if (!s.Active || App(s.Pid) is not { } app) continue;
            max[app] = Math.Max(max.GetValueOrDefault(app), float.IsFinite(s.Peak) ? Math.Clamp(s.Peak, 0, 1) : 0);
        }

        foreach (var (app, p) in max.OrderBy(m => m.Key, StringComparer.Ordinal)) peak(app, at, p);
        foreach (var app in _active.Where(a => !max.ContainsKey(a)).Order(StringComparer.Ordinal)) stopped(app);
        _active = new HashSet<string>(max.Keys, StringComparer.OrdinalIgnoreCase);
        if (_apps.Count > 512) _apps.Clear();
    }

    private string? App(uint pid)
    {
        if (!_apps.TryGetValue(pid, out var app)) _apps[pid] = app = appOf(pid);
        return app;
    }
}

/// <summary>
/// C20.1: which SMTC sources came and went. Several sessions of one app count once; an app leaves when its last session
/// does (then the media logic forgets it: "source removed").
/// </summary>
public sealed class SmtcSessionSet
{
    private HashSet<string> _apps = new(StringComparer.OrdinalIgnoreCase);

    public (IReadOnlyList<string> Added, IReadOnlyList<string> Removed) Sync(IEnumerable<string?> aumids)
    {
        ArgumentNullException.ThrowIfNull(aumids);
        var now = new HashSet<string>(aumids.Select(MediaAppMap.FromAumid).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        var added = now.Where(a => !_apps.Contains(a)).Order(StringComparer.Ordinal).ToList();
        var removed = _apps.Where(a => !now.Contains(a)).Order(StringComparer.Ordinal).ToList();
        _apps = now;
        return (added, removed);
    }
}
