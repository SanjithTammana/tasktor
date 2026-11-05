namespace Tasktor.Core.Storage;

public enum ForgetScope
{
    Learned,
    Tracking,
    Capture,
    All,
}

/// <summary>What a forget removed.</summary>
public sealed record ForgetResult(ForgetScope Scope, IReadOnlyList<string> Removed, int CaptureRecords);

/// <summary>
/// The <c>forget</c> family (U1 D-H8 + U2 / U3 amendments). Before deleting, it asks the owning features to stop and close
/// their stores (<see cref="Before"/>). Never touches settings, rules, consents, the install key, notes, presets, plugins
/// or the frozen-job recovery file. Runs off the loop.
/// </summary>
public sealed class Forgetter
{
    private readonly StoreLayout _layout;

    public Forgetter(StoreLayout layout) => _layout = layout;

    /// <summary>Hooks per scope that stop features and close their stores first (e.g. the event store before <c>track\</c> goes).</summary>
    public Dictionary<ForgetScope, List<Func<Task>>> Before { get; } = new()
    {
        [ForgetScope.Learned] = [],
        [ForgetScope.Tracking] = [],
        [ForgetScope.Capture] = [],
        [ForgetScope.All] = [],
    };

    /// <summary>Hooks per scope that run after the files are gone (features reopen fresh stores).</summary>
    public Dictionary<ForgetScope, List<Func<Task>>> After { get; } = new()
    {
        [ForgetScope.Learned] = [],
        [ForgetScope.Tracking] = [],
        [ForgetScope.Capture] = [],
        [ForgetScope.All] = [],
    };

    /// <summary>T1: delete capture records newer than a time (U4 D-G6 "forget the last 15 min / hour / today"). Set by the capture feature.</summary>
    public Func<DateTimeOffset, Task<int>>? ForgetCaptureSince { get; set; }

    /// <summary>The files and folders a scope deletes (shown before asking).</summary>
    public IReadOnlyList<string> PathsFor(ForgetScope scope)
    {
        var learned = new[] { _layout.JobKinds, _layout.LearnedVolumes, Path.Combine(_layout.StateDir, "app-kinds.json") };
        var tracking = new[] { _layout.TrackDir, _layout.Focus, _layout.Projects };
        var capture = new[] { _layout.CaptureDir };
        return scope switch
        {
            ForgetScope.Learned => learned,
            ForgetScope.Tracking => tracking,
            ForgetScope.Capture => capture,
            _ => [.. learned, .. tracking, .. capture, _layout.AgentsDir, _layout.JobsDir, _layout.LogDir, _layout.SelfUse, _layout.Snapshot, _layout.CacheDir],
        };
    }

    public async Task<ForgetResult> ForgetAsync(ForgetScope scope, DateTimeOffset? captureSince = null)
    {
        var scopes = scope == ForgetScope.All ? new[] { ForgetScope.Learned, ForgetScope.Tracking, ForgetScope.Capture, ForgetScope.All } : [scope];
        foreach (var s in scopes)
        {
            foreach (var hook in Before[s])
            {
                await hook().ConfigureAwait(false);
            }
        }

        if (scope == ForgetScope.Capture && captureSince is { } since)
        {
            var n = ForgetCaptureSince is null ? 0 : await ForgetCaptureSince(since).ConfigureAwait(false);
            return new ForgetResult(scope, [], n);
        }

        var removed = new List<string>();
        foreach (var path in PathsFor(scope))
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
                removed.Add(path);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
                removed.Add(path);
            }
        }

        foreach (var s in scopes)
        {
            foreach (var hook in After[s])
            {
                await hook().ConfigureAwait(false);
            }
        }

        return new ForgetResult(scope, removed, 0);
    }

    public static bool TryParse(string? text, out ForgetScope scope)
    {
        scope = text switch { "learned" => ForgetScope.Learned, "tracking" => ForgetScope.Tracking, "capture" => ForgetScope.Capture, "all" => ForgetScope.All, _ => (ForgetScope)(-1) };
        return (int)scope >= 0;
    }
}
