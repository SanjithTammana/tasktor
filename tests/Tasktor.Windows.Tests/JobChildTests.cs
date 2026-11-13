using System.Diagnostics;
using Tasktor.Contracts;
using Tasktor.Core.Plugins;
using Tasktor.Windows.Processes;

namespace Tasktor.Windows.Tests;

/// <summary>F-PC9 on real processes: a worker is created inside its kill-on-close job, so even a child it starts at once
/// and leaves behind ends with it; stdin, stdout, stderr and the exit code still work as with <see cref="Process"/>.</summary>
public sealed class JobChildTests
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    [Fact]
    public async Task A_child_the_worker_leaves_behind_ends_with_it()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // A copy of ping under a name no other test uses, so the leftover is unmistakable.
        var dir = Directory.CreateTempSubdirectory("tasktor-jobchild-");
        var sleeper = Path.Combine(dir.FullName, Sleeper + ".exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), sleeper);
        Assert.DoesNotContain(' ', sleeper); // cmd's start takes it unquoted
        var runner = new ChildProcessRunner { Launch = JobChild.Start };

        // cmd starts a second cmd that starts the sleeper, and both exit at once: it is orphaned two levels down (no living
        // parent links it to the worker) and was started before anything could have put it in a job afterwards.
        var handle = runner.Start(Spec("start /b cmd /d /c start /b " + sleeper + " -n 30 127.0.0.1 ^>nul 2^>nul ^<nul"));
        var orphans = new List<Process>();
        for (var i = 0; i < 150 && orphans.Count == 0; i++)
        {
            await Task.Delay(100);
            orphans = [.. Process.GetProcessesByName(Sleeper)];
        }

        try
        {
            if (orphans.Count == 0)
            {
                var early = await runner.WaitAsync(handle, CancellationToken.None);
                Assert.Fail($"no sleeper: exit {early.ExitCode}, out [{early.Stdout}], err [{early.StderrTail}]");
            }

            var clock = Stopwatch.StartNew();
            await runner.WaitAsync(handle, CancellationToken.None);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), "the leftover should not hold the worker's output open");
            Assert.All(orphans, p => Assert.True(p.WaitForExit(2000), "the orphaned child should end with the worker's job"));
        }
        finally
        {
            foreach (var p in orphans.Where(p => !p.HasExited))
            {
                p.Kill();
                p.WaitForExit(2000);
            }

            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Stdin_stdout_stderr_and_the_exit_code_come_through()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var runner = new ChildProcessRunner { Launch = JobChild.Start };
        var handle = runner.Start(Spec("set /p line= & call echo got %line% & echo oops 1>&2 & exit 3") with { Stdin = "héllo world\r\n" });
        var exit = await runner.WaitAsync(handle, CancellationToken.None);

        Assert.Equal(3, exit.ExitCode);
        Assert.Contains("got", exit.Stdout, StringComparison.Ordinal);
        Assert.Contains("world", exit.Stdout, StringComparison.Ordinal);
        Assert.Contains("oops", exit.StderrTail, StringComparison.Ordinal);
        Assert.NotEqual(0, handle.Key.CreatedTicks);
    }

    [Fact]
    public async Task A_timeout_ends_the_worker_and_its_children()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var runner = new ChildProcessRunner { Launch = JobChild.Start };
        var handle = runner.Start(Spec("ping -n 30 127.0.0.1") with { TimeoutMs = 300 });
        var exit = await runner.WaitAsync(handle, CancellationToken.None);

        Assert.True(exit.TimedOut);
        Assert.Null(exit.ExitCode);
    }

    [Fact]
    public void Arguments_survive_the_command_line_round_trip()
    {
        var line = JobChild.CommandLine(@"C:\a b\x.exe", ["plain", "two words", "q\"uote", @"trail\", @"tr ail\", string.Empty, @"a\\""b"]);
        Assert.Equal("\"C:\\a b\\x.exe\" plain \"two words\" \"q\\\"uote\" trail\\ \"tr ail\\\\\" \"\" \"a\\\\\\\\\\\"b\"\0", line);
    }

    private static WorkerSpec Spec(string script) => new()
    {
        Kind = WorkerKind.Plugin,
        Exe = Cmd,
        Args = ["/d", "/c", script],
        WorkingDir = Environment.SystemDirectory,
        TimeoutMs = 20000,
    };

    private const string Sleeper = "tasktor-jobchild-sleeper";
}
