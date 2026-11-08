using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Config;
using Tasktor.Core.Features.Calendar;
using Tasktor.Core.Features.Focus;
using Tasktor.Core.Rules;
using Tasktor.Tests.Shared;

namespace Tasktor.Core.Tests;

/// <summary>U2 E11: presets, event-mode coupling, the block-end toast, the cross-validation review and notes.</summary>
public sealed class FocusTests : IDisposable
{
    private readonly FeatureRig _rig = new();
    private readonly EventMode _eventMode;
    private readonly FocusTimer _focus;
    private BlockMeasures? _measures = new(0.8, 2, 0.1, null, 0.9);

    public FocusTests()
    {
        _eventMode = new EventMode(_rig.Context, null);
        _rig.Features.Add(_eventMode); // the Module may be off: focus blocks still open windows
        _focus = new FocusTimer(_rig.Context, _eventMode, new NotesStore(_rig.Store.Layout.NotesDir)) { TrackingOn = () => true };
        _focus.Measure = (_, _, _) => _measures;
        _rig.Add(_focus);
    }

    public void Dispose() => _rig.Dispose();

    private ActionResult Act(string action, Dictionary<string, string>? args = null, Dictionary<string, string>? target = null)
    {
        var r = _rig.Act(new ActionRequest { Action = action, Origin = Origin.Flyout, Args = args, Target = target });
        _rig.Loop.RunPending();
        return r;
    }

    private string State(string name)
    {
        var inputs = new RuleInputs();
        foreach (var f in _rig.Features)
        {
            f.Contribute(inputs, _rig.Clock.UtcNow);
        }

        return inputs.States.GetValueOrDefault(name, "?");
    }

    [Fact]
    public void A_focus_block_is_an_event_mode_window_and_breaks_are_not_and_every_4th_break_is_long()
    {
        Act("focus.start");
        Assert.Equal("Pomodoro 25/5", _focus.PresetName);
        Assert.Equal("true", State("focus_block"));
        Assert.Equal("true", State("event_mode"));
        var kinds = new List<(string, int)>();
        for (var i = 0; i < 8; i++)
        {
            kinds.Add((_focus.Block!.Kind, _focus.Block.Minutes));
            _rig.Advance(TimeSpan.FromMinutes(_focus.Block.Minutes));
            Assert.True(_focus.Waiting);
            Assert.Equal("false", State("event_mode"));
            Act("focus.next");
        }

        Assert.Equal([("focus", 25), ("break", 5), ("focus", 25), ("break", 5), ("focus", 25), ("break", 5), ("focus", 25), ("long_break", 15)], kinds);
        Assert.Equal(4, _focus.Results.Count);
    }

    [Fact]
    public void Block_end_toasts_next_plus_5_and_stop_and_plus_5_reopens_the_block()
    {
        _rig.LoadRules();
        Act("focus.start", new() { ["preset"] = "50/10" });
        _rig.Advance(TimeSpan.FromMinutes(50));
        var toast = Assert.Single(_rig.Rules.Evaluate(), o => o.Toast is not null).Toast!;
        Assert.Equal("Focus done. Next: Break · 10 min", toast.Title);
        Assert.Equal(["focus.next", "focus.extend", "focus.stop"], toast.Buttons.Select(b => b.Request.Action));
        Assert.Equal(["Next", "+5 min", "Stop"], toast.Buttons.Select(b => b.Label));

        Assert.Equal(ActionStatus.Ok, _rig.Act(toast.Buttons[1].Request).Status);
        _rig.Loop.RunPending();
        Assert.True(_focus.Running);
        Assert.Equal("true", State("event_mode"));
        _rig.Advance(TimeSpan.FromMinutes(5));
        Assert.True(_focus.Waiting);
        Act("focus.stop");
        var block = Assert.Single(_focus.Results);
        Assert.Equal(55, block.Minutes);
        Assert.Equal(BlockEnd.Completed, block.End);
        Assert.Null(_rig.State.Current.Focus);
    }

    [Fact]
    public void Pausing_closes_the_event_window_and_resuming_keeps_the_time_left()
    {
        Act("focus.start");
        _rig.Advance(TimeSpan.FromMinutes(10));
        Act("focus.pause");
        Assert.True(_focus.Paused);
        Assert.Equal("false", State("event_mode"));
        _rig.Advance(TimeSpan.FromMinutes(30));
        Assert.False(_focus.Waiting);
        Act("focus.pause");
        Assert.Equal(_rig.Clock.UtcNow.AddMinutes(15), _rig.State.Current.Focus!.Until);
        Assert.Equal("true", State("event_mode"));
    }

    [Fact]
    public void Users_presets_come_from_config_and_skipped_and_stopped_blocks_are_recorded_as_such()
    {
        _rig.Settings.Update(c => c with { Focus = new FocusSettings { Presets = [new FocusPreset { Name = "Sprint 15/3", Blocks = [new() { Kind = "focus", Minutes = 15 }, new() { Kind = "break", Minutes = 3 }] }] } });
        Act("focus.start", new() { ["preset"] = "Sprint 15/3" });
        _rig.Advance(TimeSpan.FromMinutes(5));
        Act("focus.skip");
        Assert.Equal("break", _focus.Block!.Kind);
        Act("focus.skip");
        _rig.Advance(TimeSpan.FromMinutes(2));
        Act("focus.stop");
        Assert.Equal([BlockEnd.Skipped, BlockEnd.Stopped], _focus.Results.Select(r => r.End));
        Assert.All(_focus.Results, r => Assert.Equal("Sprint 15/3", r.Preset));
    }

    [Fact]
    public void Presets_are_compared_only_after_5_blocks_each_and_low_confidence_blocks_are_left_out()
    {
        var t = DateTimeOffset.UnixEpoch;
        BlockResult R(string p, double share, double conf = 0.9) => FocusReview.Result(t = t.AddHours(1), p, 25, BlockEnd.Completed, "wf-1", new BlockMeasures(share, 1, 0, null, conf));
        var results = Enumerable.Range(0, 5).Select(_ => R("25/5", 0.71)).Concat(Enumerable.Range(0, 4).Select(_ => R("50/10", 0.82))).ToList();
        Assert.StartsWith("Comparing presets after 5 blocks", FocusReview.Compare(FocusReview.Stats(results), true), StringComparison.Ordinal);
        results.Add(R("50/10", 0.10, conf: 0.2)); // unsure: excluded
        Assert.StartsWith("Comparing presets", FocusReview.Compare(FocusReview.Stats(results), true), StringComparison.Ordinal);
        results.Add(R("50/10", 0.82));
        var stats = FocusReview.Stats(results);
        Assert.Equal("50/10 kept you on course longer than 25/5 (82% vs 71%, 5 blocks each)", FocusReview.Compare(stats, true));
        Assert.Equal(1, stats.Single(s => s.Preset == "50/10").Excluded);
        Assert.Equal("Turn on tracking to see how focused blocks went", FocusReview.Compare(stats, false));
    }

    [Fact]
    public void The_focus_pull_lists_presets_recent_blocks_and_the_comparison_and_carries_no_note_text()
    {
        _rig.Settings.Update(c => c with { Focus = new FocusSettings { Presets = [new FocusPreset { Name = "Sprint 15/3", Blocks = [new() { Kind = "focus", Minutes = 15 }, new() { Kind = "break", Minutes = 3 }] }] } });
        new NotesStore(_rig.Store.Layout.NotesDir).Save("Today", "secret note body");
        Act("focus.start", new() { ["preset"] = "50/10" });
        _rig.Advance(TimeSpan.FromMinutes(20));
        Act("focus.stop");
        var pulled = _rig.Pulls["focus"](new Contracts.Pipe.PullMessage { Table = "focus" })!;
        Assert.Equal(["Pomodoro 25/5", "50/10", "90/20", "Sprint 15/3"], pulled["presets"]!.AsArray().Select(p => (string)p!["name"]!));
        Assert.Equal("25 min focus, 5 min break, a long break every 4", (string)pulled["presets"]![0]!["summary"]!);
        var block = Assert.Single(pulled["blocks"]!.AsArray());
        Assert.Equal(("50/10", 20, "stopped"), ((string)block!["preset"]!, (int)block["minutes"]!, (string)block["end"]!));
        Assert.Equal(0.8, (double)block["on_intent_share"]!);
        Assert.Equal("50/10", (string)pulled["stats"]![0]!["preset"]!);
        Assert.StartsWith("Comparing presets", (string)pulled["review"]!, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", pulled.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Today", pulled.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("wf-", pulled.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_focus_pull_with_no_blocks_is_empty_lists()
    {
        var pulled = _rig.Pulls["focus"](new Contracts.Pipe.PullMessage { Table = "focus" })!;
        Assert.Empty(pulled["blocks"]!.AsArray());
        Assert.Empty(pulled["stats"]!.AsArray());
        Assert.Equal(3, pulled["presets"]!.AsArray().Count);
    }

    [Fact]
    public void Without_tracking_blocks_store_no_numbers_and_the_card_says_to_turn_it_on()
    {
        _focus.TrackingOn = () => false;
        Act("focus.start");
        _rig.Advance(TimeSpan.FromMinutes(25));
        Act("focus.stop");
        Assert.Null(Assert.Single(_focus.Results).OnIntentShare);
        Assert.Contains(_focus.Cards()[0].Blocks, b => b is Contracts.Cards.BadgeBlock bb && bb.Text.ToString().Contains("Turn on tracking", StringComparison.Ordinal));
        Assert.DoesNotContain("title", File.ReadAllText(_rig.Store.Layout.Focus), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Notes_are_plain_files_pinned_lines_show_on_the_card_and_forget_tracking_keeps_them()
    {
        Assert.Equal(ActionStatus.Ok, Act("note.save", new() { ["name"] = "Today", ["text"] = "ship the report\nreply to review\nthird line" }).Status);
        Act("note.pin", target: new() { ["note"] = "Today" });
        var list = Assert.Single(_focus.Cards()[0].Blocks.OfType<Contracts.Cards.ListBlock>());
        Assert.Contains("ship the report", list.Items.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("third line", list.Items.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_rig.Store.Layout.NotesDir, "Today.txt")));
        Assert.Equal(ActionStatus.NeedsConfirm, Act("note.delete", target: new() { ["note"] = "Today" }).Status);

        Act("focus.start");
        _rig.Advance(TimeSpan.FromMinutes(25));
        Act("focus.stop");
        new Storage.Forgetter(_rig.Store.Layout).ForgetAsync(Storage.ForgetScope.Tracking).GetAwaiter().GetResult();
        Assert.False(File.Exists(_rig.Store.Layout.Focus));
        Assert.True(File.Exists(Path.Combine(_rig.Store.Layout.NotesDir, "Today.txt")));
    }
}
