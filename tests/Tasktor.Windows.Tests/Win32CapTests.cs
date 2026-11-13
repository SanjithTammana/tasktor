using System.Diagnostics;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Platform;
using Tasktor.Core.Features.Jobs;
using Tasktor.Core.Plugins;
using Tasktor.Tests.Shared;
using Tasktor.Windows.Processes;

namespace Tasktor.Windows.Tests;

/// <summary>F-PC5 on a real process: a CPU cap outlives the agent that set it (Windows keeps the job while the process is
/// in it), so a restarted agent must lift it, and quitting must lift it too.</summary>
public sealed class Win32CapTests
{
    [Fact]
    public void A_cap_left_by_a_crashed_agent_is_lifted_at_the_next_start()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var store = new TempStore();
        using var child = Ping();
        try
        {
            var key = new ProcKey((uint)child.Id, child.StartTime.ToFileTimeUtc());
            var capId = "cap:test-crash-" + child.Id;
            var crashed = Control(store);
            Assert.Equal(ActionStatus.Ok, Assert.Single(crashed.CapCores(capId, [key], 1)).Status);
            Assert.True(JobObject.CpuRate(Win32ProcessControl.CapName(capId)) > 0);

            // The first agent dies without releasing: its handles close, the capped process keeps its job (and the cap).
            crashed.AbandonCaps();
            Assert.True(JobObject.CpuRate(Win32ProcessControl.CapName(capId)) > 0, "the cap outlives the agent");

            Control(store).RecoverFrozen(); // the next start, same state folder

            Assert.Equal(0u, JobObject.CpuRate(Win32ProcessControl.CapName(capId)));
            Assert.Equal(string.Empty, File.ReadAllText(store.Layout.Caps));
        }
        finally
        {
            child.Kill();
        }
    }

    [Fact]
    public void Quitting_lifts_every_cap()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var store = new TempStore();
        using var child = Ping();
        try
        {
            var key = new ProcKey((uint)child.Id, child.StartTime.ToFileTimeUtc());
            var capId = "cap:test-quit-" + child.Id;
            var control = Control(store);
            control.CapCores(capId, [key], 1);
            control.ReleaseOnQuit();

            Assert.Equal(0u, JobObject.CpuRate(Win32ProcessControl.CapName(capId)));
        }
        finally
        {
            child.Kill();
        }
    }

    private static Win32ProcessControl Control(TempStore store) =>
        new(new ChildProcessRunner(), new FrozenRegistry(store.Layout.Frozen), () => DateTimeOffset.UtcNow, store.Layout.Caps);

    private static Process Ping() =>
        Process.Start(new ProcessStartInfo("ping", "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
}
