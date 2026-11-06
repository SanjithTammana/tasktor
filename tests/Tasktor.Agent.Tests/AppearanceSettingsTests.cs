using Tasktor.Agent.Host;
using Tasktor.Contracts.Actions;
using Tasktor.Tests.Shared;

namespace Tasktor.Agent.Tests;

/// <summary>U4 Settings → Appearance: hide deep-window pages, never Home, Your data or Settings.</summary>
public sealed class AppearanceSettingsTests
{
    [Fact]
    public async Task Pages_hide_and_show_and_the_forget_and_export_pages_cannot_be_hidden()
    {
        using var store = new TempStore();
        await using var host = new AgentHost(new AgentOptions { Store = store.Layout, PipeName = "tasktor-test-" + Guid.NewGuid().ToString("N")[..12] });
        host.Start();
        async Task<ActionResult> Page(string page, bool shown, Origin origin = Origin.Flyout) =>
            await (await host.Loop.InvokeAsync(() => host.Router.Submit(new ActionRequest { Action = "settings.page", Origin = origin, Args = new Dictionary<string, string> { ["page"] = page, ["shown"] = shown ? "true" : "false" } }))).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal((ActionStatus.Ok, "Hid Jobs"), ((await Page("jobs", false)).Status, (await Page("jobs", false)).Message));
        await Page("activity", false);
        Assert.Equal(["jobs", "activity"], (await host.Loop.InvokeAsync(() => host.State.Current.HiddenPages))!); // hiding twice keeps one entry
        Assert.Equal(["jobs", "activity"], host.Settings.Current.Flyout!.HiddenPages!);
        await Page("jobs", true);
        Assert.Equal(["activity"], (await host.Loop.InvokeAsync(() => host.State.Current.HiddenPages))!);

        Assert.Equal(ActionStatus.Refused, (await Page("data", false)).Status);
        Assert.Equal(ActionStatus.Refused, (await Page("settings", false)).Status);
        Assert.Equal(ActionStatus.Refused, (await Page("home", false)).Status);
        Assert.Equal(ActionStatus.Refused, (await Page("jobs", false, Origin.Rule)).Status); // people only
    }
}
