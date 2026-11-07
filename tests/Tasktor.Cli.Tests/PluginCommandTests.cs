namespace Tasktor.Cli.Tests;

/// <summary>Plugin authoring without the agent: <c>tasktor plugin new</c> and <c>tasktor plugin check</c>.</summary>
public sealed class PluginCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tasktor-plugin-cli-" + Guid.NewGuid().ToString("N")[..8]);

    public PluginCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static async Task<(int Code, string Out, string Err)> Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await CliApp.RunAsync(args, new CliIo { Out = o, Err = e });
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public async Task A_new_plugin_passes_check_and_runs_including_its_action()
    {
        var (made, _, _) = await Run("plugin", "new", "my-plugin", "--to", _dir);
        Assert.Equal(ExitCode.Ok, made);
        var folder = Path.Combine(_dir, "my-plugin");

        var check = await Run("plugin", "check", folder);
        Assert.Equal(ExitCode.Ok, check.Code);
        Assert.Contains("plugin.json: ok", check.Out, StringComparison.Ordinal);
        Assert.Contains("Declares no network use", check.Out, StringComparison.Ordinal);
        Assert.Contains("main.py", check.Out, StringComparison.Ordinal); // the exact command line, as the dialog shows it

        var run = await Run("plugin", "check", folder, "--run", "--set", "label=Checks");
        Assert.True(run.Code == ExitCode.Ok, run.Err);
        Assert.Contains("Output: ok, 1 card", run.Out, StringComparison.Ordinal);
        Assert.Contains("Checks 1", run.Out, StringComparison.Ordinal); // the setting reached it; a fresh data folder per check
        Assert.Equal(ExitCode.Ok, (await Run("plugin", "check", folder, "--run", "--action", "reset")).Code);
        Assert.Equal(ExitCode.Error, (await Run("plugin", "check", folder, "--run", "--action", "nope")).Code);

        Assert.Equal(ExitCode.Error, (await Run("plugin", "new", "my-plugin", "--to", _dir)).Code); // never over an existing folder
        Assert.Equal(ExitCode.Error, (await Run("plugin", "new", "Bad Id", "--to", _dir)).Code);
    }

    [Fact]
    public async Task The_shipped_example_passes_with_its_setting()
    {
        var example = Path.Combine(RepoRoot(), "examples", "plugins", "todo-count");
        var file = Path.Combine(_dir, "todo.md");
        File.WriteAllText(file, "- [ ] one\n- [x] done\nTODO: two\n");
        var run = await Run("plugin", "check", example, "--run", "--set", "file=" + file);
        Assert.True(run.Code == ExitCode.Ok, run.Err);
        Assert.Contains("Open 2", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_broken_manifest_or_output_is_refused_with_the_agents_reason()
    {
        var bad = Path.Combine(_dir, "bad");
        Directory.CreateDirectory(bad);
        File.WriteAllText(Path.Combine(bad, "plugin.json"), """{ "contract": 1, "id": "other-id", "name": "Bad", "version": "1", "command": ["x"], "interval": "5m", "timeout": "10s", "reads": [], "network": [] }""");
        var manifest = await Run("plugin", "check", bad);
        Assert.Equal(ExitCode.Error, manifest.Code);
        Assert.Contains("plugin.json:", manifest.Err, StringComparison.Ordinal);

        var noisy = Path.Combine(_dir, "noisy");
        Directory.CreateDirectory(noisy);
        File.WriteAllText(Path.Combine(noisy, "main.py"), "print('not json')");
        File.WriteAllText(Path.Combine(noisy, "plugin.json"), """{ "contract": 1, "id": "noisy", "name": "Noisy", "version": "1", "command": ["{python}", "main.py"], "interval": "5m", "timeout": "10s", "reads": ["nothing"], "network": [], "settings": [], "actions": [] }""");
        var output = await Run("plugin", "check", noisy, "--run");
        Assert.Equal(ExitCode.Error, output.Code);
        Assert.Contains("Output refused: output is not valid", output.Err, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Tasktor.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
