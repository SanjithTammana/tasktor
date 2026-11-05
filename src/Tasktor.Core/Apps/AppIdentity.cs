using Tasktor.Contracts.Rules;

namespace Tasktor.Core.Apps;

/// <summary>What an exe is to the grouping engine (A2.1).</summary>
public enum AppRole
{
    /// <summary>An ordinary app: its own group unless it is a same-exe child.</summary>
    Normal,

    /// <summary>Known helper (WebView2, conhost, crash handlers): folds into the nearest non-helper ancestor (step 2).</summary>
    Helper,

    /// <summary>Tool process (node, python, cmd, …): counts under the launching app (step 3, signed default).</summary>
    Tool,

    /// <summary>Launcher (explorer, services, svchost, sihost, userinit): never swallows what it starts (step 4).</summary>
    Launcher,
}

/// <summary>The resolved identity of one exe: app id (shared with C's features), display name, role and any user rule.</summary>
public sealed record AppInfo(string AppId, string Name, AppRole Role, GroupingRule? Rule);

/// <summary>
/// exe / path / package → app id + display name + role, resolved <b>once per distinct exe</b> and cached (A's O(u·r)).
/// User grouping rules go first; shipped defaults are read-only. C's workflow features take app ids from here.
/// </summary>
public sealed class AppIdentity
{
    public static readonly IReadOnlySet<string> DefaultHelpers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "msedgewebview2.exe", "conhost.exe", "openconsole.exe", "crashpad_handler.exe", "chrome_crashpad_handler.exe",
        "werfault.exe", "dllhost.exe", "runtimebroker.exe",
    };

    public static readonly IReadOnlySet<string> DefaultTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "node.exe", "python.exe", "pythonw.exe", "py.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "bash.exe", "wsl.exe",
        "java.exe", "javaw.exe", "dotnet.exe", "ffmpeg.exe", "git.exe", "cargo.exe", "rustc.exe", "rscript.exe", "julia.exe",
        "uv.exe", "npm.exe", "make.exe",
    };

    public static readonly IReadOnlySet<string> DefaultLaunchers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "services.exe", "svchost.exe", "sihost.exe", "userinit.exe", "wininit.exe", "winlogon.exe",
    };

    /// <summary>A3 critical-process denylist (ship read-only): End and Freeze disabled.</summary>
    public static readonly IReadOnlySet<string> CriticalDenylist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "csrss.exe", "wininit.exe", "winlogon.exe", "smss.exe", "services.exe", "lsass.exe", "dwm.exe", "explorer.exe",
        "tasktor-core.exe", "system", "registry", "memory compression",
    };

    private readonly Dictionary<string, AppInfo> _cache = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<GroupingRule> _rules;
    private readonly Func<string, string?>? _describe;

    /// <param name="rules">Grouping rules, user entries first.</param>
    /// <param name="describe">Display name for an exe path (Windows: version-info description); null → exe stem.</param>
    public AppIdentity(IReadOnlyList<GroupingRule>? rules = null, Func<string, string?>? describe = null)
    {
        _rules = rules ?? [];
        _describe = describe;
    }

    public int CacheSize => _cache.Count;

    /// <summary>Replace the grouping rules (hot reload); clears the cache.</summary>
    public void SetRules(IReadOnlyList<GroupingRule> rules)
    {
        _rules = rules;
        _cache.Clear();
    }

    public AppInfo Resolve(string exe, string? path = null, string? package = null)
    {
        ArgumentNullException.ThrowIfNull(exe);
        var cacheKey = package is null ? exe + "|" + path : "pkg:" + package;
        if (_cache.TryGetValue(cacheKey, out var hit))
        {
            return hit;
        }

        GroupingRule? rule = null;
        foreach (var r in _rules)
        {
            if (Matches(r.Match, exe, path, package))
            {
                rule = r;
                break;
            }
        }

        var role = DefaultHelpers.Contains(exe) ? AppRole.Helper
            : DefaultTools.Contains(exe) ? AppRole.Tool
            : DefaultLaunchers.Contains(exe) ? AppRole.Launcher
            : AppRole.Normal;
        var appId = package is not null ? "pkg:" + package.ToLowerInvariant() : exe.ToLowerInvariant();
        var name = rule?.GroupAs
            ?? (path is not null ? _describe?.Invoke(path) : null)
            ?? Stem(exe);
        if (rule?.GroupAs is { } groupAs)
        {
            appId = "group:" + groupAs.ToLowerInvariant();
        }

        var info = new AppInfo(appId, name, role, rule);
        _cache[cacheKey] = info;
        return info;
    }

    public static bool IsCritical(string exe) => CriticalDenylist.Contains(exe);

    private static bool Matches(GroupingMatch m, string exe, string? path, string? package)
    {
        if (m.Exe is null && m.Path is null && m.Package is null)
        {
            return false;
        }

        return (m.Exe is null || Glob.IsMatch(m.Exe, exe))
            && (m.Path is null || (path is not null && Glob.IsMatch(m.Path, path)))
            && (m.Package is null || (package is not null && Glob.IsMatch(m.Package, package)));
    }

    private static string Stem(string exe)
    {
        var stem = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe[..^4] : exe;
        return stem.Length == 0 ? exe : char.ToUpperInvariant(stem[0]) + stem[1..];
    }
}
