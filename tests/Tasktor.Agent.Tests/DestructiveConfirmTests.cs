using Tasktor.Agent.Host;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Config;
using Tasktor.Core.Actions;
using Tasktor.Core.Storage;
using Tasktor.Tests.Shared;

namespace Tasktor.Agent.Tests;

/// <summary>
/// U1 D-H4 (proofs/action-routing.md I3): a destructive action never takes effect on its first flyout request. It asks for
/// the confirm step, or refuses; it never comes back done.
/// </summary>
public sealed class DestructiveConfirmTests
{
    private static readonly string[] Modules = ["ports", "agents", "next_event", "deadlines", "event_mode", "meeting_guard", "folders", "connectivity", "focus", "batteries"];

    [Fact]
    public async Task No_destructive_action_completes_without_its_confirm()
    {
        using var store = new TempStore();
        new SettingsStore(store.Layout).Save(new ConfigFile { Modules = Modules.ToDictionary(m => m, _ => true) });
        await using var host = Composition.Create(store.Layout);
        host.Start();
        var done = new List<string>();
        var handled = 0;
        foreach (var def in ActionCatalog.All.Where(d => d.Class == ActionClass.Destructive))
        {
            var result = await (await host.Loop.InvokeAsync(() => host.Router.Submit(new ActionRequest { Action = def.Id, Origin = Origin.Flyout }))).WaitAsync(TimeSpan.FromSeconds(10));
            // agent.hooks.remove answers "No Tasktor hooks to remove" without doing anything when none are installed: the
            // no-op shortcut, which the router unit test covers. Any other Ok here did something without a confirm.
            if (result.Status is ActionStatus.Ok && !(def.Id == "agent.hooks.remove" && result.Message == "No Tasktor hooks to remove"))
            {
                done.Add(def.Id);
            }

            if (result.Reason != "no_handler")
            {
                handled++;
            }
        }

        Assert.Empty(done);
        Assert.True(handled >= 5, "only " + handled + " destructive actions had a handler: the test is not reaching them");
    }

    [Fact]
    public async Task Forgetting_what_was_learned_asks_first_then_forgets()
    {
        using var store = new TempStore();
        new SettingsStore(store.Layout).Save(new ConfigFile { Modules = Modules.ToDictionary(m => m, _ => true) });
        await using var host = Composition.Create(store.Layout);
        host.Start();
        var first = await (await host.Loop.InvokeAsync(() => host.Router.Submit(new ActionRequest { Action = "learned.forget", Origin = Origin.Flyout }))).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ActionStatus.NeedsConfirm, first.Status);
        var second = await (await host.Loop.InvokeAsync(() => host.Router.Submit(new ActionRequest { Action = "learned.forget", Origin = Origin.Flyout, Confirm = first.Token }))).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((ActionStatus.Ok, "Forgotten"), (second.Status, second.Message));
    }
}
