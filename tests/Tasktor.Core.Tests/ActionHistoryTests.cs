using Tasktor.Contracts.Actions;
using Tasktor.Core.Actions;

namespace Tasktor.Core.Tests;

/// <summary>U4 D-G3 Home "Undo": undoable actions of the last 30 minutes.</summary>
public sealed class ActionHistoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);
    private readonly ActionHistory _h = new();

    private static ActionRequest Req(string action, string? app = "chrome", Origin origin = Origin.Shelf) =>
        new() { Action = action, Origin = origin, Target = app is null ? null : new Dictionary<string, string> { ["app"] = app } };

    private static ActionResult Ok(string action) => new() { Action = action, Status = ActionStatus.Ok };

    [Fact]
    public void Undoable_successes_are_kept_newest_first_with_their_undo()
    {
        _h.Record(Req("priority.set"), Ok("priority.set"), "Lower Chrome", T0);
        _h.Record(Req("job.pause", null, Origin.Rule), Ok("job.pause"), "Pause train.py", T0.AddMinutes(1));
        Assert.Equal(["Pause train.py", "Lower Chrome"], _h.Items.Select(i => i.Text));
        var lower = _h.Items[1];
        Assert.Equal(("priority.restore", "shelf", "chrome"), (lower.Undo, lower.Origin, lower.Target!["app"]));
        Assert.Equal("rule", _h.Items[0].Origin);
    }

    [Fact]
    public void Failures_reads_and_actions_without_an_undo_are_not_kept()
    {
        _h.Record(Req("priority.set"), new ActionResult { Action = "priority.set", Status = ActionStatus.Refused }, "Lower Chrome", T0);
        _h.Record(Req("open", null), Ok("open"), "Open Tasktor", T0);
        _h.Record(Req("export.data", null), Ok("export.data"), "Export to a path", T0);
        _h.Record(Req("app.end"), Ok("app.end"), "End Chrome", T0);
        Assert.Empty(_h.Items);
    }

    [Fact]
    public void Running_the_undo_on_the_same_target_marks_it_undone()
    {
        var changes = 0;
        _h.Changed += () => changes++;
        _h.Record(Req("priority.set", "chrome"), Ok("priority.set"), "Lower Chrome", T0);
        _h.Record(Req("priority.set", "code"), Ok("priority.set"), "Lower Code", T0);
        _h.Record(Req("priority.restore", "chrome"), Ok("priority.restore"), "Restore Chrome", T0.AddMinutes(1));
        Assert.Null(_h.Items.Single(i => i.Text == "Lower Chrome").Undo);
        Assert.Equal("priority.restore", _h.Items.Single(i => i.Text == "Lower Code").Undo);
        Assert.Equal(3, changes);
    }

    [Fact]
    public void Entries_expire_after_30_minutes_and_the_list_is_capped()
    {
        _h.Record(Req("priority.set"), Ok("priority.set"), "Lower Chrome", T0);
        _h.Prune(T0.AddMinutes(29));
        Assert.Single(_h.Items);
        _h.Prune(T0.AddMinutes(31));
        Assert.Empty(_h.Items);

        for (var i = 0; i < ActionHistory.Max + 5; i++)
        {
            _h.Record(Req("priority.set", "app" + i), Ok("priority.set"), "Lower " + i, T0.AddSeconds(i));
        }

        Assert.Equal(ActionHistory.Max, _h.Items.Count);
        Assert.Equal("Lower " + (ActionHistory.Max + 4), _h.Items[0].Text);
    }
}
