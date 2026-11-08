using System.Text.RegularExpressions;
using Tasktor.Contracts.Workflow;
using Tasktor.Core.Apps;

namespace Tasktor.Core.Workflow;

/// <summary>C3.2 rule strengths.</summary>
public enum RuleStrength : byte
{
    /// <summary>Keep app and time, drop title and domain.</summary>
    DropTitle,

    /// <summary>Write only <c>excluded_start</c> / <c>excluded_end</c>: one Private row.</summary>
    Exclude,

    /// <summary>As if the window never had focus (system UI).</summary>
    Ignore,
}

/// <summary>
/// C3 redaction, always on: emails → ‹email›, drive / UNC / home paths → ‹path›, UUIDs and hex ids → ‹id›, the user's
/// names → ‹me›, runs of 4+ digits → ‹num›. Redaction tokens never enter features (the tokenizer drops them).
/// </summary>
public sealed partial class Redactor
{
    private readonly Regex? _me;

    public Redactor(IEnumerable<string> ownNames)
    {
        var names = ownNames.Where(n => n.Trim().Length >= 3).Select(n => Regex.Escape(n.Trim())).ToList();
        _me = names.Count == 0 ? null : new Regex(@"\b(?:" + string.Join('|', names) + @")\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    [GeneratedRegex(@"(?:[A-Za-z]:[\\/]|\\\\)[^\s""<>|]*|(?:~|/home|/Users)/[^\s""<>|]*", RegexOptions.CultureInvariant)]
    private static partial Regex PathShape();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b|\b(?=[0-9a-fA-F]*[0-9])(?=[0-9a-fA-F]*[a-fA-F])[0-9a-fA-F]{8,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex HexId();

    [GeneratedRegex(@"\d{4,}", RegexOptions.CultureInvariant)]
    private static partial Regex Digits();

    public string Apply(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var t = Email().Replace(title, "‹email›");
        t = PathShape().Replace(t, "‹path›");
        t = HexId().Replace(t, "‹id›");
        if (_me is not null)
        {
            t = _me.Replace(t, "‹me›");
        }

        return Digits().Replace(t, "‹num›");
    }
}

/// <summary>
/// C3 privacy gate, before anything is written: pause → app rule → private-window detector → title rule → redaction →
/// domain rule. Pause wins over everything; a browser private window is excluded as a whole; a browser without a passing
/// detector would yield no title or domain (every shipped browser has one: its title marker). Loop-only.
/// </summary>
public sealed class PrivacyGate
{
    public static readonly IReadOnlyDictionary<string, RuleStrength> DefaultApps = new Dictionary<string, RuleStrength>(StringComparer.OrdinalIgnoreCase)
    {
        // Password managers, credential prompts, Windows Security: excluded.
        ["1password.exe"] = RuleStrength.Exclude,
        ["bitwarden.exe"] = RuleStrength.Exclude,
        ["keepass.exe"] = RuleStrength.Exclude,
        ["keepassxc.exe"] = RuleStrength.Exclude,
        ["lastpass.exe"] = RuleStrength.Exclude,
        ["dashlane.exe"] = RuleStrength.Exclude,
        ["proton pass.exe"] = RuleStrength.Exclude,
        ["credentialuibroker.exe"] = RuleStrength.Exclude,
        ["consent.exe"] = RuleStrength.Exclude,
        ["*sechealthui*"] = RuleStrength.Exclude,
        ["securityhealthhost.exe"] = RuleStrength.Exclude,

        // Messengers and mail: app and time only.
        ["whatsapp.exe"] = RuleStrength.DropTitle,
        ["signal.exe"] = RuleStrength.DropTitle,
        ["telegram.exe"] = RuleStrength.DropTitle,
        ["discord.exe"] = RuleStrength.DropTitle,
        ["ms-teams.exe"] = RuleStrength.DropTitle,
        ["teams.exe"] = RuleStrength.DropTitle,
        ["slack.exe"] = RuleStrength.DropTitle,
        ["outlook.exe"] = RuleStrength.DropTitle,
        ["olk.exe"] = RuleStrength.DropTitle,
        ["thunderbird.exe"] = RuleStrength.DropTitle,

        // System UI: never splits a run, zero dwell.
        ["shellexperiencehost.exe"] = RuleStrength.Ignore,
        ["startmenuexperiencehost.exe"] = RuleStrength.Ignore,
        ["searchhost.exe"] = RuleStrength.Ignore,
        ["searchapp.exe"] = RuleStrength.Ignore,
        ["widgets.exe"] = RuleStrength.Ignore,
        ["lockapp.exe"] = RuleStrength.Ignore,
        ["tasktor-flyout.exe"] = RuleStrength.Ignore,
    };

    /// <summary>Per-browser private-window markers in the title (C2.3; each needs its A1 probe on Windows).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> PrivateMarkers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["msedge.exe"] = ["InPrivate"],
        ["chrome.exe"] = ["Incognito"],
        ["brave.exe"] = ["Private"],
        ["firefox.exe"] = ["Private Browsing"],
        ["opera.exe"] = ["Private"],
        ["vivaldi.exe"] = ["Private"],
    };

    private readonly Redactor _redactor;
    private readonly List<(string Glob, RuleStrength Strength)> _apps;
    private readonly List<Regex> _excludeTitles;
    private bool _inExcluded;

    public PrivacyGate(Redactor redactor, IEnumerable<string>? excludeApps = null, IEnumerable<string>? excludeTitles = null)
    {
        _redactor = redactor;
        _apps = [.. (excludeApps ?? []).Select(a => (a.Contains('.', StringComparison.Ordinal) || a.Contains('*', StringComparison.Ordinal) ? a : a + ".exe", RuleStrength.Exclude)), .. DefaultApps.Select(kv => (kv.Key, kv.Value))];
        _excludeTitles = [.. (excludeTitles ?? []).Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)))];
    }

    public bool Paused { get; set; }

    public static bool IsBrowser(string? exe) => exe is not null && PrivateMarkers.ContainsKey(exe);

    public RuleStrength? RuleFor(string? exe) => exe is null ? null : _apps.Where(r => Glob.IsMatch(r.Glob, exe)).Select(r => (RuleStrength?)r.Strength).FirstOrDefault();

    /// <summary>The gated events for one raw event (none, the event, or an exclude boundary plus the event).</summary>
    public List<CaptureEvent> Apply(CaptureEvent raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var output = new List<CaptureEvent>(2);
        if (Paused && raw.Kind is not (CaptureKind.Pause or CaptureKind.Unpause))
        {
            return output;
        }

        if (raw.Kind is not (CaptureKind.Foreground or CaptureKind.Title or CaptureKind.Minimize or CaptureKind.Restore))
        {
            output.Add(raw with { Title = null, Domain = null });
            return output;
        }

        var rule = RuleFor(raw.App);
        if (rule == RuleStrength.Ignore)
        {
            return output;
        }

        var exclude = rule == RuleStrength.Exclude
            || (IsBrowser(raw.App) && raw.Title is { } t0 && PrivateMarkers[raw.App!].Any(m => t0.Contains(m, StringComparison.OrdinalIgnoreCase)))
            || (raw.Title is { } t1 && _excludeTitles.Any(r => r.IsMatch(t1)));
        if (exclude)
        {
            if (!_inExcluded)
            {
                _inExcluded = true;
                output.Add(new CaptureEvent { Kind = CaptureKind.ExcludedStart, TsUtcMs = raw.TsUtcMs, TzMin = raw.TzMin, MonoMs = raw.MonoMs });
            }

            return output;
        }

        if (_inExcluded && raw.Kind == CaptureKind.Foreground)
        {
            _inExcluded = false;
            output.Add(new CaptureEvent { Kind = CaptureKind.ExcludedEnd, TsUtcMs = raw.TsUtcMs, TzMin = raw.TzMin, MonoMs = raw.MonoMs });
        }
        else if (_inExcluded)
        {
            return output; // a title change of a background window while the excluded one has focus
        }

        var keepText = rule != RuleStrength.DropTitle;
        output.Add(raw with
        {
            Title = keepText && raw.Title is { Length: > 0 } title ? _redactor.Apply(title.Length > 512 ? title[..512] : title) : null,
            Domain = keepText && IsBrowser(raw.App) && ValidHost(raw.Domain) ? raw.Domain!.ToLowerInvariant() : null,
        });
        return output;
    }

    /// <summary>A hostname with a dot and a letter suffix; never a path, query or port.</summary>
    public static bool ValidHost(string? host) =>
        host is { Length: > 3 and <= 253 } h && h.Contains('.', StringComparison.Ordinal) && h.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-')
        && char.IsAsciiLetter(h[^1]) && !h.StartsWith('.') && !h.EndsWith('.');
}
