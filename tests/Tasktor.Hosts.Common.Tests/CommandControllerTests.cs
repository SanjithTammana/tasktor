using System.Text.Json.Nodes;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Cards;
using Tasktor.Hosts.Common.Command;
using Tasktor.Hosts.Common.Pipe;
using Tasktor.Hosts.Common.Render;

namespace Tasktor.Hosts.Common.Tests;

/// <summary>U4 amended D-G12: typing, moving the highlight, Enter, the in-place confirm, pages, Esc.</summary>
public sealed class CommandControllerTests
{
    private static readonly BindingResolver Resolver = new(JsonNode.Parse("""
        { "state": { "apps": [ { "app_id": "chrome", "name": "Chrome" } ], "jobs": [ { "id": "j1", "label": "train.py", "status": "working" } ] } }
        """));

    private readonly List<ActionRequest> _sent = [];

    private CommandController Rig(Func<ActionRequest, ActionResult>? reply = null)
    {
        reply ??= r => new ActionResult { Action = r.Action, Status = ActionStatus.Ok };
        var c = new CommandController(new ActionInvoker((r, _) =>
        {
            _sent.Add(r);
            return Task.FromResult(reply(r));
        }, Origin.Command));
        c.Update([], Resolver);
        return c;
    }

    [Fact]
    public void Typing_requeries_and_highlights_the_first_result()
    {
        var c = Rig();
        c.Type("chr");
        Assert.Equal("Lower Chrome's priority", c.Highlighted!.Label);
        c.Type("zzz");
        Assert.Equal(-1, c.Selected);
        Assert.Null(c.Highlighted);
    }

    [Fact]
    public void Up_and_down_wrap_across_sections()
    {
        var c = Rig();
        c.Type("chr");
        var n = c.Items.Count;
        c.Move(-1);
        Assert.Equal(n - 1, c.Selected);
        c.Move(1);
        Assert.Equal(0, c.Selected);
        c.Move(2);
        Assert.Equal("End Chrome…", c.Highlighted!.Label);
    }

    [Fact]
    public async Task Enter_runs_the_highlight_with_origin_command_and_closes()
    {
        var c = Rig();
        var done = 0;
        c.Done += () => done++;
        c.Type("chr");
        var outcome = await c.EnterAsync();
        Assert.True(outcome!.Ok);
        var sent = Assert.Single(_sent);
        Assert.Equal(("priority.set", Origin.Command, "below_normal"), (sent.Action, sent.Origin, sent.Args!["priority"]));
        Assert.Equal(1, done);
    }

    [Fact]
    public async Task Destructive_confirms_in_place_and_enter_answers_yes_esc_answers_keep()
    {
        var c = Rig(r => r.Confirm is null
            ? new ActionResult { Action = r.Action, Status = ActionStatus.NeedsConfirm, Token = "t", Summary = "End train.py? It's 52% done." }
            : new ActionResult { Action = r.Action, Status = ActionStatus.Ok });
        c.Type("train");
        c.Move(1); // "End train.py…"
        var run = c.EnterAsync();
        Assert.Equal("End train.py? It's 52% done.", c.Confirming);
        c.Move(1); // ignored while confirming
        await c.EnterAsync(); // Enter = yes
        Assert.True((await run)!.Ok);
        Assert.Equal([null, "t"], _sent.Select(r => r.Confirm));

        _sent.Clear();
        run = c.EnterAsync();
        Assert.False(c.Escape());
        Assert.True((await run)!.Declined);
        Assert.Single(_sent);
        Assert.True(c.Escape()); // nothing pending: close
    }

    [Fact]
    public async Task A_page_asks_the_view_to_open_it_and_sends_nothing()
    {
        var c = Rig();
        string? opened = null;
        c.OpenPage += p => opened = p;
        c.Type("settings");
        await c.EnterAsync();
        Assert.Equal("settings", opened);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task A_refusal_stays_open_with_a_notice_and_closing_resets()
    {
        var c = Rig(r => new ActionResult { Action = r.Action, Status = ActionStatus.Refused, Reason = "guard:denylist" });
        var done = 0;
        c.Done += () => done++;
        c.Type("chr");
        await c.EnterAsync();
        Assert.Equal(0, done);
        Assert.Equal("Couldn't do that (guard:denylist).", c.Notice);
        c.Closed();
        Assert.Equal(string.Empty, c.Text);
        Assert.Null(c.Notice);
    }

    [Fact]
    public void A_card_update_keeps_the_highlighted_row()
    {
        var c = Rig();
        c.Type("chr");
        c.Move(1);
        var label = c.Highlighted!.Label;
        c.Update([new Card { Id = "x", Title = "X", Blocks = [] }], Resolver);
        Assert.Equal(label, c.Highlighted!.Label);
    }

    [Fact]
    public async Task Typing_searches_your_activity_after_a_pause_and_shows_the_hits_last()
    {
        var asked = new List<string>();
        var c = new CommandController(new ActionInvoker((r, _) => Task.FromResult(new ActionResult { Action = r.Action, Status = ActionStatus.Ok }), Origin.Command))
        {
            SearchActivity = (q, _) =>
            {
                lock (asked)
                {
                    asked.Add(q);
                }

                IReadOnlyList<Contracts.Capture.ActivityHit> hits = q == "revenue"
                    ? [new() { Start = new DateTimeOffset(2026, 11, 6, 14, 5, 0, TimeSpan.Zero), End = new DateTimeOffset(2026, 11, 6, 14, 20, 0, TimeSpan.Zero), Snippet = "Quarterly [revenue] review" }]
                    : [];
                return Task.FromResult<IReadOnlyList<Contracts.Capture.ActivityHit>?>(hits);
            },
            SearchDelay = TimeSpan.FromMilliseconds(50),
            Zone = TimeZoneInfo.Utc,
        };
        c.Update([], Resolver);
        c.Type("re");
        c.Type("revenue");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!c.View.Sections.Any(s => s.Heading == "Your activity") && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        var section = c.View.Sections.Last();
        Assert.Equal("Your activity", section.Heading);
        Assert.Equal("Fri 14:05 · Quarterly [revenue] review", section.Items.Single().Label);
        lock (asked)
        {
            Assert.Equal(["revenue"], asked); // "re" is too short, and the pause means one search per settled query
        }

        c.Type("revenues");
        Assert.DoesNotContain(c.View.Sections, s => s.Heading == "Your activity"); // stale hits never show under new text
    }
}
