using System.Text.Json.Nodes;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Cards;
using Tasktor.Hosts.Common.Pipe;
using Tasktor.Hosts.Common.Render;
using Tasktor.Hosts.Common.Shelf;

namespace Tasktor.Hosts.Common.Tests;

/// <summary>U4 amended D-G11: unfold a noun, run its action, confirm inside the sentence, back out; and the placement.</summary>
public sealed class ShelfControllerTests
{
    private static readonly BindingResolver Resolver = new(JsonNode.Parse("{}"));

    private static readonly CardAction Lower = new() { Id = "lower", Label = "Lower Chrome's priority", Action = "priority.set", Noun = "Chrome" };
    private static readonly CardAction End = new() { Id = "end", Label = "End train.py", Action = "job.end", Noun = "train.py", Destructive = true };

    private static readonly IReadOnlyList<Card> Cards =
    [
        new Card { Id = "core.memory", Title = "Memory", Severity = Value.Literal("warn"), Blocks = [new BadgeBlock { Text = Value.Literal("Chrome holds 5.7 GB") }], Actions = [Lower] },
        new Card { Id = "core.jobs", Title = "Jobs", Severity = Value.Literal("warn"), Blocks = [new BadgeBlock { Text = Value.Literal("train.py stalled") }], Actions = [End] },
    ];

    private sealed class FakeAgent
    {
        public List<ActionRequest> Sent { get; } = [];

        public Func<ActionRequest, ActionResult> Reply { get; set; } = r => new ActionResult { Action = r.Action, Status = ActionStatus.Ok, Message = "Done." };

        public Task<ActionResult> Act(ActionRequest r, CancellationToken ct)
        {
            Sent.Add(r);
            return Task.FromResult(Reply(r));
        }
    }

    private static (ShelfController Shelf, FakeAgent Agent) Rig()
    {
        var agent = new FakeAgent();
        var shelf = new ShelfController(new ActionInvoker(agent.Act, Origin.Shelf));
        shelf.Update(Cards, Resolver);
        return (shelf, agent);
    }

    [Fact]
    public void Tapping_a_noun_unfolds_it_and_tapping_again_folds_it()
    {
        var (shelf, _) = Rig();
        shelf.Toggle("core.memory:Chrome");
        Assert.Equal("lower", Assert.Single(shelf.Unfolded!.Actions).Id);
        shelf.Toggle("core.jobs:train.py");
        Assert.Equal("core.jobs:train.py", shelf.Unfolded!.Id);
        shelf.Toggle("core.jobs:train.py");
        Assert.Null(shelf.Unfolded);
    }

    [Fact]
    public async Task A_reversible_action_runs_with_origin_shelf_and_leaves_a_notice()
    {
        var (shelf, agent) = Rig();
        shelf.Toggle("core.memory:Chrome");
        var outcome = await shelf.RunAsync(Lower);

        Assert.True(outcome!.Ok);
        var sent = Assert.Single(agent.Sent);
        Assert.Equal(("priority.set", Origin.Shelf, (string?)null), (sent.Action, sent.Origin, sent.Confirm));
        Assert.Equal("Done.", shelf.Notice);
        Assert.Null(shelf.Unfolded);
        Assert.False(shelf.Busy);
    }

    private static FakeAgent TwoStep(FakeAgent agent)
    {
        agent.Reply = r => r.Confirm is null
            ? new ActionResult { Action = r.Action, Status = ActionStatus.NeedsConfirm, Token = "t1", Summary = "End train.py? It's 52% done." }
            : new ActionResult { Action = r.Action, Status = ActionStatus.Ok, Message = "Ended train.py." };
        return agent;
    }

    [Fact]
    public async Task A_destructive_action_confirms_inside_the_sentence_and_only_yes_resends_the_token()
    {
        var (shelf, agent) = Rig();
        TwoStep(agent);
        var run = shelf.RunAsync(End);

        Assert.Equal("End train.py? It's 52% done.", shelf.Confirming!.Summary);
        Assert.True(shelf.Busy);
        shelf.Toggle("core.memory:Chrome"); // ignored while confirming
        Assert.Null(shelf.Unfolded);

        shelf.Answer(true);
        var outcome = await run;
        Assert.True(outcome!.Ok);
        Assert.Equal([null, "t1"], agent.Sent.Select(r => r.Confirm));
        Assert.Equal("Ended train.py.", shelf.Notice);
        Assert.Null(shelf.Confirming);
    }

    [Fact]
    public async Task Esc_and_focus_loss_answer_keep()
    {
        var (shelf, agent) = Rig();
        TwoStep(agent);
        var run = shelf.RunAsync(End);
        Assert.False(shelf.Escape()); // backs out of the confirm, the shelf stays
        var outcome = await run;
        Assert.True(outcome!.Declined);
        Assert.Single(agent.Sent);
        Assert.Null(shelf.Notice);

        run = shelf.RunAsync(End);
        shelf.Closed();
        Assert.True((await run)!.Declined);
        Assert.Equal(2, agent.Sent.Count);
    }

    [Fact]
    public void Esc_folds_before_it_hides()
    {
        var (shelf, _) = Rig();
        shelf.Toggle("core.memory:Chrome");
        Assert.False(shelf.Escape());
        Assert.Null(shelf.Unfolded);
        Assert.True(shelf.Escape());
    }

    [Fact]
    public async Task Refusals_and_a_missing_agent_read_as_notices()
    {
        var (shelf, agent) = Rig();
        agent.Reply = r => new ActionResult { Action = r.Action, Status = ActionStatus.Refused, Reason = "not_on_surface" };
        await shelf.RunAsync(Lower);
        Assert.Equal("Couldn't do that (not_on_surface).", shelf.Notice);

        var gone = new ShelfController(new ActionInvoker((_, _) => throw new IOException("pipe closed"), Origin.Shelf));
        gone.Update(Cards, Resolver);
        Assert.False((await gone.RunAsync(Lower))!.Ok);
        Assert.Equal("Tasktor is not running.", gone.Notice);
    }

    [Fact]
    public void An_unfolded_noun_that_leaves_the_sentence_folds()
    {
        var (shelf, _) = Rig();
        shelf.Toggle("core.memory:Chrome");
        shelf.Update([Cards[1]], Resolver);
        Assert.Null(shelf.Unfolded);
        shelf.Toggle("core.jobs:train.py");
        shelf.Update(Cards, Resolver);
        Assert.Equal("core.jobs:train.py", shelf.Unfolded!.Id);
    }

    [Fact]
    public void Changes_are_dispatched_to_the_view_thread()
    {
        var posted = 0;
        var shelf = new ShelfController(new ActionInvoker((r, _) => Task.FromResult(new ActionResult { Action = r.Action, Status = ActionStatus.Ok }), Origin.Shelf), a =>
        {
            posted++;
            a();
        });
        shelf.Update(Cards, Resolver);
        _ = shelf.RunAsync(Lower);
        Assert.Equal(1, posted);
    }

    [Fact]
    public void A_launch_asks_for_the_shelf_unless_it_says_open()
    {
        Assert.Equal(ViewerSignal.ShowShelf, ViewerSignal.ForArgs(["Tasktor.Flyout.exe"]));
        Assert.Equal(ViewerSignal.OpenDeep, ViewerSignal.ForArgs(["Tasktor.Flyout.exe", "--open"]));
        Assert.Equal(ViewerSignal.ShowCommand, ViewerSignal.ForArgs(["Tasktor.Flyout.exe", "--command"]));
        foreach (var s in new[] { ViewerSignal.ShowShelf, ViewerSignal.ShowCommand, ViewerSignal.OpenDeep })
        {
            Assert.Equal(s, ViewerSignal.ForArgs(ViewerSignal.ArgFor(s) is { } a ? ["x", a] : ["x"]));
        }
        Assert.StartsWith(@"Local\", ViewerSignal.Instance, StringComparison.Ordinal);
    }

    private static readonly PxRect Monitor = new(0, 0, 2560, 1440);

    [Fact]
    public void Bottom_taskbar_edge_sits_on_its_top_and_the_shelf_grows_up()
    {
        var p = ShelfLayout.Place(Monitor, new PxRect(0, 0, 2560, 1368), 1.5, 140);
        Assert.Equal(TaskbarEdge.Bottom, p.Taskbar);
        Assert.Equal(new PxRect(0, 1368, 2560, 9), p.Edge);
        Assert.Equal(new PxRect(0, 1158, 2560, 210), p.Shelf);
    }

    [Fact]
    public void Top_hidden_and_vertical_taskbars()
    {
        var top = ShelfLayout.Place(Monitor, new PxRect(0, 48, 2560, 1392), 1, 100);
        Assert.Equal((TaskbarEdge.Top, new PxRect(0, 42, 2560, 6), new PxRect(0, 48, 2560, 100)), (top.Taskbar, top.Edge, top.Shelf));

        var hidden = ShelfLayout.Place(Monitor, Monitor, 1, 100);
        Assert.Equal((TaskbarEdge.Hidden, new PxRect(0, 1434, 2560, 6), new PxRect(0, 1340, 2560, 100)), (hidden.Taskbar, hidden.Edge, hidden.Shelf));

        var left = ShelfLayout.Place(Monitor, new PxRect(62, 0, 2498, 1440), 1, 100);
        Assert.Equal((TaskbarEdge.Left, new PxRect(62, 1434, 2498, 6), new PxRect(62, 1340, 2498, 100)), (left.Taskbar, left.Edge, left.Shelf));
        Assert.Equal(TaskbarEdge.Right, ShelfLayout.TaskbarOf(Monitor, new PxRect(0, 0, 2498, 1440)));
    }

    [Fact]
    public void Shelf_height_is_clamped_to_half_the_work_area_and_monitors_can_be_offset()
    {
        var p = ShelfLayout.Place(new PxRect(-1920, 0, 1920, 1080), new PxRect(-1920, 0, 1920, 1032), 1, 5000);
        Assert.Equal(new PxRect(-1920, 516, 1920, 516), p.Shelf);
        Assert.Equal(6, ShelfLayout.Place(Monitor, Monitor, 1, 0).Shelf.Height);
        Assert.Throws<ArgumentOutOfRangeException>(() => ShelfLayout.Place(Monitor, Monitor, 0, 10));
    }
}
