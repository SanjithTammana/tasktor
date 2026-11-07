using Tasktor.Contracts.Platform;
using Tasktor.Core.Apps;

namespace Tasktor.Core.Features.SystemMonitor;

/// <summary>A resolved action target: the processes (leaf-first for ending), a display name and the count.</summary>
public sealed record ResolvedTarget(IReadOnlyList<ProcKey> Keys, string Name, string? Exe);

/// <summary>
/// Resolves action targets to processes from the latest process table (A3, B3, D-H6 targets): <c>pid</c> (+ <c>created</c>),
/// <c>app</c> (an A2 group), <c>group</c> (a rules group of exe names), <c>process</c> (an exe glob), <c>job</c>
/// (B's job tree, registered by the job feature). Order is leaf-first so End tree ends children before parents.
/// </summary>
public sealed class ProcessTargets
{
    private readonly Func<IReadOnlyDictionary<ProcKey, ProcessRow>> _table;
    private readonly Func<string, AppGroup?> _app;
    private readonly Func<string, IReadOnlyList<string>?> _group;

    public ProcessTargets(Func<IReadOnlyDictionary<ProcKey, ProcessRow>> table, Func<string, AppGroup?> app, Func<string, IReadOnlyList<string>?> group)
    {
        _table = table;
        _app = app;
        _group = group;
    }

    /// <summary>Set by B: a job id → its tree (root first).</summary>
    public Func<string, ResolvedTarget?>? Job { get; set; }

    public ResolvedTarget? Resolve(IReadOnlyDictionary<string, string>? target)
    {
        if (target is null)
        {
            return null;
        }

        var table = _table();
        if (target.TryGetValue("pid", out var pidText) && uint.TryParse(pidText, out var pid))
        {
            var row = table.Values.FirstOrDefault(r => r.Key.Pid == pid);
            if (row is null)
            {
                return target.TryGetValue("created", out var c) && long.TryParse(c, out var created) ? new ResolvedTarget([new ProcKey(pid, created)], "PID " + pid, null) : null;
            }

            if (target.TryGetValue("created", out var c2) && long.TryParse(c2, out var created2) && created2 != row.Key.CreatedTicks)
            {
                return new ResolvedTarget([new ProcKey(pid, created2)], row.Exe, row.Exe); // the guard reports "Process changed"
            }

            return new ResolvedTarget([row.Key], row.Exe, row.Exe);
        }

        if (target.TryGetValue("pids", out var pidList))
        {
            var wanted = pidList.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => uint.TryParse(x, out var v) ? v : 0).ToHashSet();
            var keys = table.Values.Where(r => wanted.Contains(r.Key.Pid)).Select(r => r.Key).ToList();
            var name = target.GetValueOrDefault("name") ?? "processes";
            return keys.Count == 0 ? null : new ResolvedTarget(LeafFirst(keys, table), name, name);
        }

        if (target.TryGetValue("app", out var appId) && _app(appId) is { } g)
        {
            return new ResolvedTarget(LeafFirst(g.Members, table), g.Name, g.Members.Count > 0 && table.TryGetValue(g.Members[0], out var r0) ? r0.Exe : null);
        }

        if (target.TryGetValue("group", out var groupName) && _group(groupName) is { } exes)
        {
            var keys = table.Values.Where(r => exes.Any(e => Glob.IsMatch(e, r.Exe))).Select(r => r.Key).ToList();
            return new ResolvedTarget(LeafFirst(keys, table), "group " + groupName, null);
        }

        if (target.TryGetValue("process", out var pattern))
        {
            var keys = table.Values.Where(r => Glob.IsMatch(pattern, r.Exe)).Select(r => r.Key).ToList();
            return new ResolvedTarget(LeafFirst(keys, table), pattern, pattern);
        }

        if (target.TryGetValue("job", out var jobId) && Job?.Invoke(jobId) is { } job)
        {
            return job with { Keys = LeafFirst(job.Keys, table) };
        }

        return null;
    }

    /// <summary>Order processes deepest-first (children before parents) within the set.</summary>
    public static List<ProcKey> LeafFirst(IReadOnlyList<ProcKey> keys, IReadOnlyDictionary<ProcKey, ProcessRow> table)
    {
        var byPid = table.Values.ToDictionary(r => r.Key.Pid);
        int Depth(ProcKey k)
        {
            var d = 0;
            var seen = new HashSet<uint>();
            var pid = k.Pid;
            while (byPid.TryGetValue(pid, out var r) && seen.Add(pid) && byPid.ContainsKey(r.ParentPid) && r.ParentPid != pid)
            {
                d++;
                pid = r.ParentPid;
            }

            return d;
        }

        return keys.Select((k, i) => (k, i, d: Depth(k))).OrderByDescending(x => x.d).ThenBy(x => x.i).Select(x => x.k).ToList();
    }
}
