namespace Tasktor.Agent.Tests;

/// <summary>An isolated agent for checks: its own store, pipe and single-instance lock; the switcher limited to test windows.</summary>
public sealed class AgentArgsTests
{
    [Fact]
    public void Data_dir_pipe_and_switcher_pids_are_read_and_bad_values_ignored()
    {
        var a = AgentArgs.Parse(["--data-dir", "T", "--pipe", "tasktor-check-1", "--switcher-pid", "4242", "--switcher-pid", "nope", "--switcher-pid", "77"]);
        Assert.Equal(("T", "tasktor-check-1"), (a.DataDir, a.Pipe));
        Assert.Equal([4242u, 77u], a.SwitcherPids);

        var none = AgentArgs.Parse([]);
        Assert.Equal((null, null), (none.DataDir, none.Pipe));
        Assert.Empty(none.SwitcherPids);
        Assert.Null(AgentArgs.Parse(["--pipe", "bad name/with slash"]).Pipe); // only a plain pipe name
    }

    [Fact]
    public void The_single_instance_lock_follows_the_pipe_so_a_check_agent_runs_beside_the_real_one()
    {
        Assert.NotEqual(SingleInstance.LockName(null), SingleInstance.LockName("tasktor-check-1"));
        Assert.Equal(SingleInstance.LockName("tasktor-check-1"), SingleInstance.LockName("tasktor-check-1"));
    }
}
