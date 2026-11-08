using Tasktor.Core.Apps;

namespace Tasktor.Core.Tests;

/// <summary>Check mode: only the check's own processes and their descendants are sampled.</summary>
public sealed class ProcessScopeTests
{
    [Fact]
    public void Roots_and_their_descendants_are_in_scope_in_any_snapshot_order_and_nothing_else()
    {
        // 50 → 51 → 52 (a job and its workers), 60 unrelated, 70 child of 60, a reused-pid loop 80 ↔ 81.
        (uint Pid, uint Parent)[] snapshot = [(52, 51), (60, 4), (51, 50), (70, 60), (50, 1), (80, 81), (81, 80)];
        Assert.Equal([50u, 51u, 52u], ProcessScope.Of(snapshot, new HashSet<uint> { 50 }).Order());
        Assert.Empty(ProcessScope.Of(snapshot, new HashSet<uint> { 99 })); // a root that is not running: nothing
        Assert.Equal([80u, 81u], ProcessScope.Of(snapshot, new HashSet<uint> { 80 }).Order()); // a cycle terminates
    }
}
