namespace Tasktor.Shell.Tests;

/// <summary>A check's switcher sees only the windows of the processes it was given; no other window's title is read.</summary>
public sealed class ShellArgsTests
{
    [Fact]
    public void Pipe_and_only_pids_are_read()
    {
        var a = ShellArgs.Parse(["--pipe", "tasktor-check-1", "--only-pid", "10", "--only-pid", "x", "--only-pid", "20"]);
        Assert.Equal("tasktor-check-1", a.Pipe);
        Assert.Equal([10u, 20u], a.OnlyPids);
        Assert.True(a.Includes(10));
        Assert.False(a.Includes(11));

        var all = ShellArgs.Parse([]);
        Assert.Null(all.Pipe);
        Assert.True(all.Includes(11)); // no limit: every window
    }
}
