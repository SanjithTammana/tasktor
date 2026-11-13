using Tasktor.Agent.Host;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Snapshot;
using Tasktor.Contracts;
using Tasktor.Core.Plugins;
using Tasktor.Tests.Shared;

namespace Tasktor.Agent.Tests;

/// <summary>Plugin contract 2 in the agent: consent needs an explicit grant, notices say which plugin sent them, and turning
/// a plugin off takes its toasts and their buttons away.</summary>
public sealed class PluginNoticeTests
{
    private const string Manifest = """
        { "contract": 2, "id": "focus", "name": "Focus", "version": "2.0.0", "command": ["{python}", "main.py"], "timeout": "10s",
          "reads": ["nothing"], "network": [], "capabilities": ["notify"],
          "actions": [ { "id": "skip", "label": "Skip", "class": "reversible" } ] }
        """;

    private const string Output = """{ "contract": 2, "notify": [ { "id": "done", "title": "Block done", "body": "Take a break", "buttons": ["skip"] } ] }""";

    [Fact]
    public async Task A_notice_names_its_plugin_and_turning_it_off_withdraws_it()
    {
        using var store = new TempStore();
        var folder = Path.Combine(store.Layout.PluginsDir, "focus");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "main.py"), "# test");
        File.WriteAllText(Path.Combine(folder, "plugin.json"), Manifest);
        var notifier = new RecordingNotifier();
        await using var host = new AgentHost(new AgentOptions
        {
            Store = store.Layout,
            PipeName = "tasktor-test-" + Guid.NewGuid().ToString("N")[..12],
            Notifier = notifier,
            ProcessControl = new OneOutput(Output),
        });
        host.Start();
        async Task<ActionResult> Act(string action, Dictionary<string, string>? args = null) =>
            await (await host.Loop.InvokeAsync(() => host.Router.Submit(new ActionRequest
            {
                Action = action,
                Origin = Origin.Flyout,
                Target = new Dictionary<string, string> { ["plugin"] = "focus" },
                Args = args,
            }))).WaitAsync(TimeSpan.FromSeconds(10));

        var info = (await host.Loop.InvokeAsync(host.PluginsReport)).Plugins.Single(x => x.Id == "focus");
        Assert.Equal(["notify"], info.Capabilities!.Select(c => c.Name));

        // No ticked list: refused, so nothing is granted that the dialog did not show.
        Assert.NotEqual(ActionStatus.Ok, (await Act("plugin.enable")).Status);
        Assert.Equal(ActionStatus.Ok, (await Act("plugin.enable", new() { ["granted"] = "notify" })).Status);

        var toast = await WaitFor(host, () => notifier.Shown.FirstOrDefault());
        Assert.Equal("plugin/focus/done", toast.Tag);
        Assert.Equal("Take a break\nFrom the Focus plugin", toast.Body);
        var button = Assert.Single(toast.Buttons!).Request;
        Assert.True(await host.Loop.InvokeAsync(() => host.Router.Toasts.IsOffered(button)));

        Assert.Equal(ActionStatus.Ok, (await Act("plugin.disable")).Status);
        Assert.Contains("plugin/focus/done", await host.Loop.InvokeAsync(() => notifier.Withdrawn.ToList()));
        Assert.False(await host.Loop.InvokeAsync(() => host.Router.Toasts.IsOffered(button)));
    }

    private static async Task<T> WaitFor<T>(AgentHost host, Func<T?> probe)
        where T : class
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until)
        {
            if (await host.Loop.InvokeAsync(probe) is { } found)
            {
                return found;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("nothing arrived");
    }

    /// <summary>Every plugin run prints the same output.</summary>
    private sealed class OneOutput(string stdout) : PortableProcessControl(new ChildProcessRunner())
    {
        public override WorkerHandle StartWorker(WorkerSpec spec) => new("w", default, spec.Kind);

        public override Task<WorkerExit> WaitWorker(WorkerHandle handle, CancellationToken cancel) =>
            Task.FromResult(new WorkerExit(handle, 0, stdout, string.Empty, false, false));
    }
}
