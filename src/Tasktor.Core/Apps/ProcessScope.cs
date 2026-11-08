namespace Tasktor.Core.Apps;

/// <summary>
/// Check mode (an isolated agent run with <c>--switcher-pid</c>): the processes a check may sample, its own fixture
/// processes and every descendant (a synthetic job's workers). Everything else is skipped before it is opened or named.
/// </summary>
public static class ProcessScope
{
    /// <summary>The running roots and their descendants in one process snapshot (pid, parent pid), in any order.</summary>
    public static HashSet<uint> Of(IReadOnlyCollection<(uint Pid, uint Parent)> snapshot, IReadOnlySet<uint> roots)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(roots);
        var scope = snapshot.Where(p => roots.Contains(p.Pid)).Select(p => p.Pid).ToHashSet();
        bool grew;
        do
        {
            grew = false;
            foreach (var (pid, parent) in snapshot)
            {
                if (scope.Contains(parent) && scope.Add(pid))
                {
                    grew = true;
                }
            }
        }
        while (grew); // terminates: each pass adds at least one of finitely many pids
        return scope;
    }
}
