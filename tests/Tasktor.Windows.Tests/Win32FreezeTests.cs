using System.Diagnostics;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Platform;
using Tasktor.Core.Features.Jobs;
using Tasktor.Core.Plugins;
using Tasktor.Tests.Shared;
using Tasktor.Windows.Processes;

namespace Tasktor.Windows.Tests;

/// <summary>B3.7 on real threads: freeze suspends a live child's threads, a second freeze adds nothing, and one thaw (with
/// its owner check) leaves none suspended.</summary>
public sealed class Win32FreezeTests
{
    [Fact]
    public void Freeze_twice_then_thaw_once_leaves_a_real_process_running()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var store = new TempStore();
        using var child = Process.Start(new ProcessStartInfo("ping", "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        try
        {
            var key = new ProcKey((uint)child.Id, child.StartTime.ToFileTimeUtc());
            var control = new Win32ProcessControl(new ChildProcessRunner(), new FrozenRegistry(store.Layout.Frozen), () => DateTimeOffset.UtcNow);

            Assert.Equal(ActionStatus.Ok, Assert.Single(control.Freeze("test", [key])).Status);
            Assert.True(AllSuspended(child), "every thread should be suspended");
            control.Freeze("test", [key]);
            Assert.Equal(ActionStatus.Ok, Assert.Single(control.Thaw("test", [key])).Status);
            Assert.False(AnySuspended(child), "one thaw after two freezes should leave no thread suspended");
            Assert.False(child.HasExited);
        }
        finally
        {
            child.Kill();
        }
    }

    private static bool AllSuspended(Process p)
    {
        p.Refresh();
        return p.Threads.Cast<ProcessThread>().All(IsSuspended);
    }

    private static bool AnySuspended(Process p)
    {
        p.Refresh();
        return p.Threads.Cast<ProcessThread>().Any(IsSuspended);
    }

    private static bool IsSuspended(ProcessThread t) => t.ThreadState == System.Diagnostics.ThreadState.Wait && t.WaitReason == ThreadWaitReason.Suspended;
}
