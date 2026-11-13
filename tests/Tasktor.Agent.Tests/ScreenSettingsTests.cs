using Tasktor.Agent.Host;
using Tasktor.Contracts.Actions;
using Tasktor.Tests.Shared;

namespace Tasktor.Agent.Tests;

/// <summary>The screen the shelf and command line open on: kept in config, published in the snapshot, people only.</summary>
public sealed class ScreenSettingsTests
{
    [Fact]
    public async Task The_choice_is_kept_published_and_survives_a_restart()
    {
        using var store = new TempStore();
        await using (var host = new AgentHost(new AgentOptions { Store = store.Layout, PipeName = "tasktor-test-" + Guid.NewGuid().ToString("N")[..12] }))
        {
            host.Start();
            async Task<ActionResult> Screen(string screen, Origin origin = Origin.Flyout) =>
                await (await host.Loop.InvokeAsync(() => host.Router.Submit(new ActionRequest { Action = "settings.screen", Origin = origin, Args = new Dictionary<string, string> { ["screen"] = screen } }))).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Null(await host.Loop.InvokeAsync(() => host.State.Current.Screen)); // not chosen yet
            var main = await Screen("main");
            Assert.Equal((ActionStatus.Ok, "Tasktor opens on your main screen"), (main.Status, main.Message));
            Assert.Equal("main", await host.Loop.InvokeAsync(() => host.State.Current.Screen));
            Assert.Equal("main", host.Settings.Current.Flyout!.Screen);

            Assert.Equal(ActionStatus.Refused, (await Screen("left")).Status);
            Assert.Equal(ActionStatus.Refused, (await Screen("working", Origin.Rule)).Status); // people only
            Assert.Equal("main", host.Settings.Current.Flyout!.Screen);
        }

        await using var again = new AgentHost(new AgentOptions { Store = store.Layout, PipeName = "tasktor-test-" + Guid.NewGuid().ToString("N")[..12] });
        again.Start();
        Assert.Equal("main", await again.Loop.InvokeAsync(() => again.State.Current.Screen));
    }
}
