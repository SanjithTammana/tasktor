using Tasktor.Agent.Host;
using Tasktor.Contracts.Actions;
using Tasktor.Core.Storage;
using Tasktor.Tests.Shared;

namespace Tasktor.Agent.Tests;

internal sealed class TwoCards : Contracts.Platform.ICardProvider
{
    public IReadOnlyList<Contracts.Cards.Card> Cards() =>
    [
        new Contracts.Cards.Card { Id = "test.one", Title = "One", Blocks = [] },
        new Contracts.Cards.Card { Id = "test.two", Title = "Two", Blocks = [] },
    ];
}

/// <summary>U4: <c>open</c> deep-links the flyout to a card; <c>card.pin</c> / <c>card.unpin</c> keep Home's order in config.</summary>
public sealed class OpenAndPinTests
{
    [Fact]
    public async Task Open_launches_the_flyout_link_and_pins_persist_in_order()
    {
        using var store = new TempStore();
        var launched = new List<string>();
        await using var host = new AgentHost(new AgentOptions
        {
            Store = store.Layout,
            PipeName = "tasktor-test-" + Guid.NewGuid().ToString("N")[..12],
            Launch = uri =>
            {
                lock (launched)
                {
                    launched.Add(uri);
                }

                return true;
            },
        });
        host.Start();
        async Task<ActionResult> Act(string action, string? card) =>
            await (await host.Loop.InvokeAsync(() => host.Router.Submit(new ActionRequest { Action = action, Origin = Origin.Flyout, Target = card is null ? null : new Dictionary<string, string> { ["card"] = card } }))).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ActionStatus.Ok, (await Act("open", "core.jobs")).Status);
        Assert.Equal(ActionStatus.Refused, (await Act("open", "../../etc")).Status);
        lock (launched)
        {
            Assert.Equal(["tasktor://open?card=core.jobs"], launched);
        }

        await host.Loop.InvokeAsync(() =>
        {
            host.Cards.Add(new TwoCards());
            return host.Cards.Refresh();
        });
        var ids = await host.Loop.InvokeAsync(() => host.Cards.Cards.Select(c => c.Id).Take(2).ToList());
        Assert.Equal(2, ids.Count);
        Assert.Equal(ActionStatus.Ok, (await Act("card.pin", ids[1])).Status);
        Assert.Equal(ActionStatus.Ok, (await Act("card.pin", ids[0])).Status);
        Assert.Equal(ActionStatus.Refused, (await Act("card.pin", "no.such")).Status);
        await host.FlushWritesAsync(); // config is written by the coalescing writer, off the loop
        // The first pin adds to Home's defaults (U4 OQ-G2: System, Jobs, Workflow); it does not replace them.
        string[] expected = [.. Hosts.Common.Deep.DeepPages.DefaultPinned.Where(d => d != ids[0] && d != ids[1]), ids[1], ids[0]];
        Assert.Equal(expected, new SettingsStore(store.Layout).Load().Flyout!.PinnedCards);
        Assert.Equal(expected, await host.Loop.InvokeAsync(() => host.State.Current.Pinned)); // the viewer reads it from the snapshot
        Assert.Equal(ActionStatus.Ok, (await Act("card.unpin", ids[1])).Status);
        Assert.Equal([.. expected.Where(x => x != ids[1])], host.Settings.Current.Flyout!.PinnedCards);
    }

    [Fact]
    public async Task Unpinning_a_default_keeps_the_other_defaults_and_an_empty_home_stays_empty()
    {
        using var store = new TempStore();
        await using var host = new AgentHost(new AgentOptions { Store = store.Layout, PipeName = "tasktor-test-" + Guid.NewGuid().ToString("N")[..12], Launch = _ => true });
        host.Start();
        async Task<ActionResult> Act(string action, string card) =>
            await (await host.Loop.InvokeAsync(() => host.Router.Submit(new ActionRequest { Action = action, Origin = Origin.Flyout, Target = new Dictionary<string, string> { ["card"] = card } }))).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(await host.Loop.InvokeAsync(() => host.State.Current.Pinned)); // nothing chosen yet: the defaults
        Assert.Equal(ActionStatus.Ok, (await Act("card.unpin", "core.jobs")).Status);
        Assert.Equal(["core.system", "core.workflow"], host.Settings.Current.Flyout!.PinnedCards);
        await Act("card.unpin", "core.system");
        await Act("card.unpin", "core.workflow");
        Assert.Equal([], host.Settings.Current.Flyout!.PinnedCards!); // not null: an empty Home is a choice, not "the defaults"
        Assert.Equal([], (await host.Loop.InvokeAsync(() => host.State.Current.Pinned))!);
    }
}
