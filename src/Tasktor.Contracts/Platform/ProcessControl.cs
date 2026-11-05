using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Snapshot;

namespace Tasktor.Contracts.Platform;

/// <summary>Outcome for one process of a process action.</summary>
public sealed record ProcessOutcome(ProcKey Key, ActionStatus Status, string? Reason = null);

/// <summary>A started worker (D-H9): its process key and an opaque handle id.</summary>
public sealed record WorkerHandle(string Id, ProcKey Key, WorkerKind Kind);

/// <summary>A finished worker run: exit code (null when killed), captured stdout, the stderr tail (≤ 4 KB), and whether it timed out.</summary>
public sealed record WorkerExit(WorkerHandle Handle, int? ExitCode, string Stdout, string StderrTail, bool TimedOut, bool OutputTooLarge);

/// <summary>
/// Process actions (A3, B3, B3.7, D-H9). Every call runs off the logic loop and its result is posted back
/// as an event (D-A5 §7). Implementations re-check (PID, creation time) before each call.
/// </summary>
public interface IProcessControl
{
    /// <summary>A3.0: ask the processes' top-level windows to close (graceful).</summary>
    IReadOnlyList<ProcessOutcome> Close(IReadOnlyList<ProcKey> keys);

    /// <summary>A3.1 / A3.2: terminate, leaf-first in the given order.</summary>
    IReadOnlyList<ProcessOutcome> End(IReadOnlyList<ProcKey> keys);

    /// <summary>A3.3 / B3.1: set the priority class; returns the previous class per process for restore.</summary>
    IReadOnlyList<(ProcessOutcome Outcome, ProcessPriority Previous)> SetPriority(IReadOnlyList<ProcKey> keys, ProcessPriority priority);

    /// <summary>A3.4 / B3.2: EcoQoS on or off.</summary>
    IReadOnlyList<ProcessOutcome> SetEfficiency(IReadOnlyList<ProcKey> keys, bool on);

    /// <summary>B3.7: suspend every thread; records intent in the frozen registry before the first suspend.</summary>
    IReadOnlyList<ProcessOutcome> Freeze(string jobId, IReadOnlyList<ProcKey> tree);

    /// <summary>B3.7: resume only the threads Tasktor suspended, once each.</summary>
    IReadOnlyList<ProcessOutcome> Thaw(string jobId, IReadOnlyList<ProcKey> tree);

    /// <summary>B3.7 Free RAM: working-set trim; returns bytes before / after.</summary>
    (ProcessOutcome Outcome, long Before, long After) TrimMemory(ProcKey key);

    /// <summary>Called once at agent start: thaws whatever a previous run left frozen. Returns the job ids thawed.</summary>
    IReadOnlyList<string> RecoverFrozen();

    /// <summary>I4 / B3.5: cap a set of processes to <paramref name="maxCores"/> (Job Object CPU rate); 0 releases.</summary>
    IReadOnlyList<ProcessOutcome> CapCores(string capId, IReadOnlyList<ProcKey> keys, int maxCores);

    /// <summary>D-H9: start a child of a closed kind in a kill-on-close Job Object with a memory cap.</summary>
    WorkerHandle StartWorker(WorkerSpec spec);

    /// <summary>Waits for a started worker to exit (or be killed at its timeout). Runs off-loop.</summary>
    Task<WorkerExit> WaitWorker(WorkerHandle handle, CancellationToken cancel);

    void CancelWorker(WorkerHandle handle);

    /// <summary>D-H9 condition 3: a closed set of control messages.</summary>
    bool SendControl(WorkerHandle handle, ControlMessage message);
}
