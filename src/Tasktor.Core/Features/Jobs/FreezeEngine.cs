using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Platform;

namespace Tasktor.Core.Features.Jobs;

/// <summary>What a thread resume did.</summary>
public enum ResumeResult
{
    Resumed,

    /// <summary>The recorded thread no longer exists (exited, or its id now names another thread): the record is obsolete.</summary>
    Gone,

    /// <summary>The thread is still there but could not be resumed (access denied, an OS error): keep the record and retry.</summary>
    Failed,
}

/// <summary>A thread opened for suspending: the open handle pins it, so its id cannot name another thread meanwhile.</summary>
public interface IOpenThread : IDisposable
{
    /// <summary>The thread's own creation time.</summary>
    long Created { get; }

    /// <summary>Suspend it once. False when the suspend failed.</summary>
    bool Suspend();
}

/// <summary>The OS calls freeze and thaw need. Thread ids are reused, so every suspend and resume checks the thread's
/// identity on the opened handle: its owner process (pid and creation time) and, on resume, its own creation time.</summary>
public interface IThreadApi
{
    /// <summary>True when the process with this pid is running and has this creation time.</summary>
    bool IsAlive(ProcKey key);

    /// <summary>The threads of these processes, with their owner pid.</summary>
    IEnumerable<(uint Pid, uint Tid)> Threads(IReadOnlySet<uint> pids);

    /// <summary>Open the thread for suspending, only if it belongs to <paramref name="owner"/>. Null when it cannot be
    /// opened (<paramref name="denied"/>: access denied) or is not that process's thread any more.</summary>
    IOpenThread? Open(uint tid, ProcKey owner, out bool denied);

    /// <summary>Resume the thread once, only if it still belongs to <paramref name="owner"/> (a creation time of 0 checks the
    /// pid only) and, when <paramref name="threadCreated"/> is not 0, is the same thread.</summary>
    ResumeResult Resume(uint tid, ProcKey owner, long threadCreated);
}

/// <summary>
/// B3.7 freeze and thaw over an <see cref="IThreadApi"/> (proofs/process-control.md P1–P3). Invariant: every thread Tasktor
/// suspended and has not resumed is recorded in the <see cref="FrozenRegistry"/> under its job, once, with its owner's
/// (pid, creation time) and its own creation time. Freeze, thaw and start-up recovery of one job never interleave (a lock
/// stripe per job id); a second freeze adds to the first; a thread suspended after its record vanished is resumed at once;
/// thaw resumes a recorded thread only while it is still that thread, and keeps the record of one it could not resume.
/// Each pass records the threads it is about to suspend before suspending any of them (one write per pass), and forgets
/// the ones whose suspend failed, so a crash mid-freeze never leaves a suspended thread unrecorded (F-PC4). Residual: a
/// crash mid-thaw, after a resume and before the record is cleared, resumes that thread once more at the next start;
/// that only matters if its own program suspended it in between.
/// </summary>
public sealed class FreezeEngine(FrozenRegistry frozen, IThreadApi threads, Func<DateTimeOffset> now)
{
    private const int MaxPasses = 8;
    private readonly Lock[] _stripes = Enumerable.Range(0, 64).Select(_ => new Lock()).ToArray();

    private Lock For(string jobId) => _stripes[(StringComparer.Ordinal.GetHashCode(jobId) & int.MaxValue) % _stripes.Length];

    public IReadOnlyList<ProcessOutcome> Freeze(string jobId, IReadOnlyList<ProcKey> tree)
    {
        lock (For(jobId))
        {
            var alive = tree.Where(threads.IsAlive).ToList();
            frozen.Begin(jobId, alive, now()); // intent first: a crash from here on is recovered at the next start
            var owners = alive.GroupBy(k => k.Pid).ToDictionary(g => g.Key, g => g.First());

            // Threads an earlier freeze of this job already suspended are not suspended again: one resume each must free them.
            var suspended = (frozen.Find(jobId)?.Threads ?? []).Select(t => t.Tid).ToHashSet();
            var denied = new HashSet<uint>();
            for (var pass = 0; pass < MaxPasses; pass++)
            {
                var opened = new List<(FrozenThread Record, IOpenThread Thread)>();
                try
                {
                    foreach (var (pid, tid) in threads.Threads(owners.Keys.ToHashSet()))
                    {
                        if (suspended.Contains(tid))
                        {
                            continue;
                        }

                        var owner = owners[pid];
                        if (threads.Open(tid, owner, out var wasDenied) is { } t)
                        {
                            opened.Add((new FrozenThread(pid, tid, owner.CreatedTicks, t.Created), t));
                        }
                        else if (wasDenied)
                        {
                            denied.Add(pid);
                        }
                    }

                    // Recorded first, suspended second: a crash in between leaves a record of a running thread (resuming it
                    // later is a no-op), never a suspended thread with no record.
                    if (opened.Count == 0 || !frozen.AddThreads(jobId, opened.Select(o => o.Record).ToList()))
                    {
                        break;
                    }

                    var failed = new List<FrozenThread>();
                    foreach (var (record, thread) in opened)
                    {
                        if (thread.Suspend())
                        {
                            suspended.Add(record.Tid);
                        }
                        else
                        {
                            failed.Add(record);
                        }
                    }

                    if (!frozen.RemoveThreads(jobId, failed))
                    {
                        // The entry vanished meanwhile: nothing would resume these, so resume them now.
                        foreach (var (record, _) in opened.Where(o => !failed.Contains(o.Record)))
                        {
                            threads.Resume(record.Tid, owners[record.Pid], record.ThreadCreated);
                        }

                        break;
                    }

                    if (failed.Count == opened.Count)
                    {
                        break; // a pass that suspended nothing new
                    }
                }
                finally
                {
                    foreach (var (_, thread) in opened)
                    {
                        thread.Dispose();
                    }
                }
            }

            // A freeze that suspended nothing leaves no entry behind (F-PC8): it would mark the processes frozen for nothing.
            if (frozen.Find(jobId) is { Threads.Count: 0 })
            {
                frozen.Retain(jobId, []);
            }

            var byPid = frozen.Find(jobId)?.Threads.Select(t => t.Pid).ToHashSet() ?? [];
            return tree.Select(k => !owners.ContainsKey(k.Pid)
                ? new ProcessOutcome(k, ActionStatus.Failed, "Already exited")
                : byPid.Contains(k.Pid) ? new ProcessOutcome(k, ActionStatus.Ok) : new ProcessOutcome(k, ActionStatus.Refused, denied.Contains(k.Pid) ? "Access denied" : "No threads")).ToList();
        }
    }

    public IReadOnlyList<ProcessOutcome> Thaw(string jobId, IReadOnlyList<ProcKey> tree)
    {
        lock (For(jobId))
        {
            return ThawLocked(jobId, tree);
        }
    }

    /// <summary>Start-up recovery: thaw every job a previous run left frozen, each under its job's lock, so a freeze that
    /// arrives meanwhile is never mistaken for the old record. A job whose record cannot be saved keeps it for the next start
    /// and does not stop the others.</summary>
    public IReadOnlyList<string> Recover()
    {
        var ids = new List<string>();
        foreach (var job in frozen.Jobs)
        {
            try
            {
                lock (For(job.JobId))
                {
                    ThawLocked(job.JobId, job.Processes.Select(p => new ProcKey(p.Pid, p.Created)).ToList());
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            ids.Add(job.JobId);
        }

        return ids;
    }

    private IReadOnlyList<ProcessOutcome> ThawLocked(string jobId, IReadOnlyList<ProcKey> tree)
    {
        var entry = frozen.Find(jobId);
        if (entry is null)
        {
            return tree.Select(k => new ProcessOutcome(k, ActionStatus.Failed, "Not frozen")).ToList();
        }

        var resumed = new HashSet<uint>();
        var failed = new HashSet<uint>();
        var keep = new List<FrozenThread>();
        foreach (var t in entry.Threads)
        {
            // Files from before the owner's creation time was kept per thread: take it from the process list only if that is
            // unambiguous; an ambiguous owner cannot be verified, so it is not resumed (and the record is dropped).
            var created = t.Created != 0 ? t.Created
                : entry.Processes.Where(p => p.Pid == t.Pid).Select(p => p.Created).Distinct().ToList() is [var only] ? only : (long?)null;
            if (created is null)
            {
                continue;
            }

            switch (threads.Resume(t.Tid, new ProcKey(t.Pid, created.Value), t.ThreadCreated))
            {
                case ResumeResult.Resumed:
                    resumed.Add(t.Pid); // once each: only the threads Tasktor suspended, and only while they are still them
                    break;
                case ResumeResult.Failed:
                    keep.Add(t); // still suspended: keep it for the next resume or start-up
                    failed.Add(t.Pid);
                    break;
            }
        }

        frozen.Retain(jobId, keep); // cleared last; what could not be resumed stays recorded
        return tree.Select(k => failed.Contains(k.Pid)
            ? new ProcessOutcome(k, ActionStatus.Failed, "Could not resume every thread; Resume again")
            : resumed.Contains(k.Pid) ? new ProcessOutcome(k, ActionStatus.Ok) : new ProcessOutcome(k, ActionStatus.Failed, "Already exited")).ToList();
    }
}
