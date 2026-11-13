using Tasktor.Contracts.Platform;
using Tasktor.Core.Apps;
using Tasktor.Core.Learning;

namespace Tasktor.Core.Features.Jobs;

/// <summary>What the gate says about one process.</summary>
public enum GateVerdict
{
    NotWatched,
    Excluded,
    Candidate,

    /// <summary>B1.8: a kind that usually becomes a job, shown as "starting" at 60 s.</summary>
    Starting,
    Job,
}

/// <summary>
/// B1.1 / D-B1 job gate: a watch-list exe (plus the <c>agents</c> group, D-S3) not excluded by its command line becomes a
/// job when age ≥ 60 s and it used ≥ 30 s of CPU within the last 2 min; sticky once a job; a pin or a progress file
/// naming it bypasses the gate. An unreadable command line gates on the exe name only.
/// </summary>
public sealed class JobGate
{
    public static readonly string[] DefaultWatch = ["python.exe", "pythonw.exe", "node.exe", "ffmpeg.exe", "java.exe", "javaw.exe", "dotnet.exe", "cargo.exe", "rustc.exe", "rscript.exe", "julia.exe"];
    public static readonly string[] DefaultExcludes = ["--stdio", "languageserver", "pylance", "mcp"];
    public static readonly TimeSpan MinAge = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(2);
    public const double MinCpuInWindowS = 30;

    private readonly HashSet<string> _watch = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _watchGlobs = [];
    private readonly List<string> _excludes = [];
    private readonly HashSet<ProcKey> _pinned = [];

    public JobGate() => Configure([], []);

    public void Configure(IEnumerable<string> extraWatch, IEnumerable<string> extraExcludes)
    {
        _watch.Clear();
        _watchGlobs.Clear();
        foreach (var w in DefaultWatch.Concat(extraWatch))
        {
            var exe = w.Contains('.', StringComparison.Ordinal) || w.Contains('*', StringComparison.Ordinal) ? w : w + ".exe";
            if (exe.Contains('*', StringComparison.Ordinal) || exe.Contains('?', StringComparison.Ordinal))
            {
                _watchGlobs.Add(exe);
            }
            else
            {
                _watch.Add(exe);
            }
        }

        _excludes.Clear();
        _excludes.AddRange(DefaultExcludes.Concat(extraExcludes).Select(e => e.ToLowerInvariant()));
    }

    public bool IsWatched(string exe) =>
        _watch.Contains(exe) || _watch.Contains(Path.GetFileNameWithoutExtension(exe) + ".exe") || _watchGlobs.Any(g => Glob.IsMatch(g, exe));

    public bool IsExcluded(string? commandLine) =>
        commandLine is not null && _excludes.Any(e => commandLine.Contains(e, StringComparison.OrdinalIgnoreCase));

    public void Pin(ProcKey key) => _pinned.Add(key);

    public void Unpin(ProcKey key) => _pinned.Remove(key);

    public bool IsPinned(ProcKey key) => _pinned.Contains(key);

    /// <summary>
    /// Whether a pinned process has this PID. The names-only scan has no creation time, so it admits the PID as a
    /// candidate; <see cref="Classify"/> then checks the full key, so a reused PID is not taken for the pinned process.
    /// </summary>
    public bool IsPinnedPid(uint pid) => _pinned.Any(k => k.Pid == pid);

    /// <summary>Classify a candidate from its CPU samples (oldest first) and whether a progress file names it.</summary>
    public GateVerdict Classify(string exe, string? commandLine, ProcKey key, DateTimeOffset firstSeen, IReadOnlyList<(DateTimeOffset At, double CpuS)> samples, DateTimeOffset now, bool bypass, bool alreadyJob, bool learnedStarting)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (alreadyJob)
        {
            return GateVerdict.Job; // sticky
        }

        if (bypass || _pinned.Contains(key))
        {
            return GateVerdict.Job;
        }

        if (!IsWatched(exe))
        {
            return GateVerdict.NotWatched;
        }

        if (IsExcluded(commandLine))
        {
            return GateVerdict.Excluded;
        }

        var age = now - (key.CreatedUtc ?? firstSeen);
        if (age >= MinAge && CpuInWindow(samples, now) >= MinCpuInWindowS)
        {
            return GateVerdict.Job;
        }

        return learnedStarting && age >= MinAge ? GateVerdict.Starting : GateVerdict.Candidate;
    }

    /// <summary>CPU seconds used within the last 2 min: now's sample minus the oldest one inside the window.</summary>
    public static double CpuInWindow(IReadOnlyList<(DateTimeOffset At, double CpuS)> samples, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count < 2)
        {
            return 0;
        }

        var latest = samples[^1];
        var start = samples.FirstOrDefault(s => now - s.At <= Window + TimeSpan.FromSeconds(5));
        return start == default ? 0 : Math.Max(0, latest.CpuS - start.CpuS);
    }

    /// <summary>The job kind's input: <c>lower(exe) + "|" + script</c>, script = basename of the first non-flag argument (or the module after <c>-m</c>).</summary>
    public static string KindText(string exe, string? commandLine) => exe.ToLowerInvariant() + "|" + Script(commandLine);

    public static KindId Kind(KeyedHasher hasher, string exe, string? commandLine)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        return hasher.Hash(KindText(exe, commandLine));
    }

    /// <summary>The label shown on the row: the script, else the exe name (a progress file's label wins).</summary>
    public static string Label(string exe, string? commandLine) => Script(commandLine) is { Length: > 0 } s ? s : exe;

    public static string Script(string? commandLine)
    {
        var tokens = Tokenize(commandLine);
        for (var i = 1; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t == "-m" && i + 1 < tokens.Count)
            {
                return tokens[i + 1];
            }

            if (t.StartsWith('-'))
            {
                continue;
            }

            return Path.GetFileName(t.Replace('\\', '/'));
        }

        return string.Empty;
    }

    /// <summary>Windows-style command-line split: spaces separate, double quotes group.</summary>
    public static List<string> Tokenize(string? commandLine)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return list;
        }

        var sb = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var c in commandLine)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (sb.Length > 0)
                {
                    list.Add(sb.ToString());
                    sb.Clear();
                }
            }
            else
            {
                sb.Append(c);
            }
        }

        if (sb.Length > 0)
        {
            list.Add(sb.ToString());
        }

        return list;
    }
}
