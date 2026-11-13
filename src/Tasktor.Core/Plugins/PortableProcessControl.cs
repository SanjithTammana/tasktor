using System.Diagnostics;
using Tasktor.Contracts;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Snapshot;

namespace Tasktor.Core.Plugins;

/// <summary>
/// <see cref="IProcessControl"/> without Win32: workers through <see cref="ChildProcessRunner"/>, end / priority through
/// <see cref="Process"/>. Freeze, EcoQoS, trims and caps are refused ("not on this platform"). Used by the Linux build
/// and tests; Windows uses <c>Win32ProcessControl</c>, which adds the rest.
/// </summary>
public class PortableProcessControl(ChildProcessRunner runner) : IProcessControl
{
    protected const string NotHere = "Not available on this platform";

    public ChildProcessRunner Runner { get; } = runner;

    public virtual IReadOnlyList<ProcessOutcome> Close(IReadOnlyList<ProcKey> keys) => Each(keys, p => p.CloseMainWindow() ? null : "No window to close");

    public virtual IReadOnlyList<ProcessOutcome> End(IReadOnlyList<ProcKey> keys) => Each(keys, p =>
    {
        p.Kill();
        return null;
    });

    public virtual IReadOnlyList<(ProcessOutcome Outcome, ProcessPriority Previous)> SetPriority(IReadOnlyList<ProcKey> keys, ProcessPriority priority)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var list = new List<(ProcessOutcome, ProcessPriority)>();
        foreach (var k in keys)
        {
            var previous = ProcessPriority.Unknown;
            var outcome = One(k, p =>
            {
                previous = From(p.PriorityClass);
                p.PriorityClass = To(priority);
                return null;
            });
            list.Add((outcome, previous));
        }

        return list;
    }

    public virtual IReadOnlyList<ProcessOutcome> SetEfficiency(IReadOnlyList<ProcKey> keys, bool on) => Refuse(keys);

    public virtual IReadOnlyList<ProcessOutcome> Freeze(string jobId, IReadOnlyList<ProcKey> tree) => Refuse(tree);

    public virtual IReadOnlyList<ProcessOutcome> Thaw(string jobId, IReadOnlyList<ProcKey> tree) => Refuse(tree);

    public virtual (ProcessOutcome Outcome, long Before, long After) TrimMemory(ProcKey key) => (new ProcessOutcome(key, ActionStatus.Refused, NotHere), 0, 0);

    public virtual IReadOnlyList<string> RecoverFrozen() => [];

    public virtual void ReleaseOnQuit()
    {
    }

    public virtual IReadOnlyList<ProcessOutcome> CapCores(string capId, IReadOnlyList<ProcKey> keys, int maxCores) => Refuse(keys);

    public virtual WorkerHandle StartWorker(WorkerSpec spec) => Runner.Start(spec);

    public virtual Task<WorkerExit> WaitWorker(WorkerHandle handle, CancellationToken cancel) => Runner.WaitAsync(handle, cancel);

    public virtual void CancelWorker(WorkerHandle handle) => Runner.Cancel(handle);

    public virtual bool SendControl(WorkerHandle handle, ControlMessage message) => Runner.Send(handle, message);

    protected static IReadOnlyList<ProcessOutcome> Refuse(IReadOnlyList<ProcKey> keys) =>
        keys.Select(k => new ProcessOutcome(k, ActionStatus.Refused, NotHere)).ToList();

    private static IReadOnlyList<ProcessOutcome> Each(IReadOnlyList<ProcKey> keys, Func<Process, string?> act) => keys.Select(k => One(k, act)).ToList();

    private static ProcessOutcome One(ProcKey key, Func<Process, string?> act)
    {
        try
        {
            using var p = Process.GetProcessById((int)key.Pid);
            if (key.CreatedTicks != 0 && p.StartTime.ToFileTimeUtc() != key.CreatedTicks)
            {
                return new ProcessOutcome(key, ActionStatus.Refused, "Process changed, refresh");
            }

            var reason = act(p);
            return new ProcessOutcome(key, reason is null ? ActionStatus.Ok : ActionStatus.Failed, reason);
        }
        catch (ArgumentException)
        {
            return new ProcessOutcome(key, ActionStatus.Failed, "Already exited");
        }
        catch (InvalidOperationException)
        {
            return new ProcessOutcome(key, ActionStatus.Failed, "Already exited");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new ProcessOutcome(key, ActionStatus.Refused, "Access denied");
        }
    }

    private static ProcessPriority From(ProcessPriorityClass c) => c switch
    {
        ProcessPriorityClass.Idle => ProcessPriority.Idle,
        ProcessPriorityClass.BelowNormal => ProcessPriority.BelowNormal,
        ProcessPriorityClass.Normal => ProcessPriority.Normal,
        ProcessPriorityClass.AboveNormal => ProcessPriority.AboveNormal,
        ProcessPriorityClass.High => ProcessPriority.High,
        ProcessPriorityClass.RealTime => ProcessPriority.Realtime,
        _ => ProcessPriority.Unknown,
    };

    protected static ProcessPriorityClass To(ProcessPriority p) => p switch
    {
        ProcessPriority.Idle => ProcessPriorityClass.Idle,
        ProcessPriority.BelowNormal => ProcessPriorityClass.BelowNormal,
        ProcessPriority.AboveNormal => ProcessPriorityClass.AboveNormal,
        ProcessPriority.High => ProcessPriorityClass.High,
        _ => ProcessPriorityClass.Normal,
    };
}
