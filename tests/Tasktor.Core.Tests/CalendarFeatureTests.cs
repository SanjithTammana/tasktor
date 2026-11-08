using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Config;
using Tasktor.Contracts.Platform;
using Tasktor.Core.Calendar;
using Tasktor.Core.Features;
using Tasktor.Core.Features.Calendar;
using Tasktor.Core.Rules;
using Tasktor.Tests.Shared;

namespace Tasktor.Core.Tests;

/// <summary>U2 E1 / E2 / I13 over the shared calendar feed: countdowns, meeting_now, Join, deadlines, event windows.</summary>
public sealed class CalendarFeatureTests : IDisposable
{
    private readonly FeatureRig _rig = new();
    private readonly CalendarSource _source;
    private readonly CalendarFeed _feed;
    private readonly string _ics;

    public CalendarFeatureTests()
    {
        _ics = Path.Combine(_rig.Store.Root, "work.ics");
        _rig.Settings.Update(c => c with { Calendar = new CalendarSettings { IcsFiles = [_ics] } });
        _source = new CalendarSource(() => _rig.Settings.Current.Calendar, _rig.Store.Layout, _rig.Clock, TimeZoneInfo.Utc);
        _feed = new CalendarFeed(_rig.Context, _source, TimeZoneInfo.Utc);
    }

    public void Dispose() => _rig.Dispose();

    // The rig's clock starts 2026-11-04 09:00Z.
    private void WriteIcs(string events) => File.WriteAllText(_ics, "BEGIN:VCALENDAR\r\n" + events.Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\nEND:VCALENDAR\r\n");

    private const string Meeting = """
        BEGIN:VEVENT
        UID:standup
        DTSTART:20261104T093000Z
        DTEND:20261104T094500Z
        SUMMARY:Standup
        LOCATION:https://meet.google.com/abc-defg-hij
        END:VEVENT
        BEGIN:VEVENT
        UID:lunch
        DTSTART:20261104T120000Z
        DTEND:20261104T130000Z
        SUMMARY:Lunch
        END:VEVENT
        BEGIN:VEVENT
        UID:tomorrow
        DTSTART:20261105T100000Z
        SUMMARY:Tomorrow thing
        END:VEVENT
        """;

    private RuleInputs Inputs()
    {
        var inputs = new RuleInputs();
        foreach (var f in _rig.Features)
        {
            f.Contribute(inputs, _rig.Clock.UtcNow);
        }

        return inputs;
    }

    [Fact]
    public void The_next_event_card_counts_down_and_lists_the_rest_of_today_only()
    {
        WriteIcs(Meeting);
        var next = _rig.Add(new NextEvent(_rig.Context, _feed));
        _rig.Pump();
        Assert.Equal("standup", next.Next!.Key.Uid);
        var card = Assert.Single(next.Cards());
        var countdown = Assert.IsType<CountdownBlock>(card.Blocks[0]);
        Assert.Equal("Standup", countdown.Label);
        var list = Assert.IsType<ListBlock>(card.Blocks.Last());
        Assert.Contains("12:00  Lunch", list.Items.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Tomorrow", list.Items.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, _rig.State.Current.Calendar!.Count); // today only, in memory (snapshot), never in the log
        Assert.DoesNotContain(_rig.LogLines.Lines, l => l.Contains("Standup", StringComparison.Ordinal));
    }

    [Fact]
    public void Urgency_turns_warn_at_15_and_critical_at_5_minutes_and_meeting_soon_fires_once_at_1()
    {
        WriteIcs(Meeting);
        _rig.LoadRules();
        var next = _rig.Add(new NextEvent(_rig.Context, _feed));
        _rig.Pump();
        Assert.Equal(Severity.Normal, next.UrgencyAt(_rig.Clock.UtcNow));
        _rig.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(Severity.Warn, next.UrgencyAt(_rig.Clock.UtcNow));
        _rig.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(Severity.Critical, next.UrgencyAt(_rig.Clock.UtcNow));
        Assert.Empty(Inputs().Events);
        _rig.Advance(TimeSpan.FromMinutes(4)); // 09:29, the timer woke at 09:29 exactly
        var soon = Assert.Single(_rig.Rules.Evaluate(), o => o.Toast is not null);
        Assert.Equal("Standup starts at 09:30", soon.Toast!.Title);
        var join = soon.Toast.Buttons[0].Request;
        Assert.Equal("url.open", join.Action);
        Assert.Empty(_rig.Launched); // no auto-open (D-H6)
        _rig.Advance(TimeSpan.FromSeconds(30));
        Assert.DoesNotContain(_rig.Rules.Evaluate(), o => o.Toast is not null); // once
    }

    [Fact]
    public void Meeting_now_is_true_only_inside_a_meeting_with_a_link_and_join_opens_that_link_only()
    {
        WriteIcs(Meeting);
        var next = _rig.Add(new NextEvent(_rig.Context, _feed));
        _rig.Pump();
        Assert.Equal("false", Inputs().States["meeting_now"]);
        _rig.Advance(TimeSpan.FromMinutes(35));
        Assert.Equal("true", Inputs().States["meeting_now"]);
        var join = Assert.Single(next.Cards()[0].Actions!);
        var r = _rig.Act(new ActionRequest { Action = "url.open", Origin = Origin.Flyout, Target = join.Target });
        Assert.Equal(ActionStatus.Ok, r.Status);
        Assert.Equal(["https://meet.google.com/abc-defg-hij"], _rig.Launched);
        var bad = _rig.Act(new ActionRequest { Action = "url.open", Origin = Origin.Flyout, Target = CardKit.Target("event", "lunch|0") });
        Assert.NotEqual(ActionStatus.Ok, bad.Status);
        _rig.Advance(TimeSpan.FromMinutes(15)); // 09:50, the meeting ended at 09:45
        Assert.Equal("false", Inputs().States["meeting_now"]);
    }

    [Fact]
    public void A_file_change_rereads_after_a_debounce_and_disabling_frees_the_rows()
    {
        WriteIcs(Meeting);
        var next = _rig.Add(new NextEvent(_rig.Context, _feed));
        _rig.Pump();
        var reads = _feed.Reads;
        WriteIcs("BEGIN:VEVENT\nUID:new\nDTSTART:20261104T110000Z\nSUMMARY:New\nEND:VEVENT");
        // Wait for the new event itself, not just any read: on Windows the first write's change notice can arrive late
        // and re-read the old file, which bumps the read count before the second write has been seen.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (next.Next?.Key.Uid != "new" && DateTime.UtcNow < deadline)
        {
            _rig.Advance(CalendarFeed.Debounce);
            Thread.Sleep(20);
        }

        Assert.True(_feed.Reads > reads); // it was a re-read after the change
        Assert.Equal("new", next.Next!.Key.Uid);
        next.Disable();
        Assert.Empty(_feed.Rows);
        Assert.Null(_rig.State.Current.Calendar);
    }

    [Fact]
    public void Feed_statuses_name_files_never_their_contents()
    {
        _rig.Settings.Update(c => c with { Calendar = new CalendarSettings { IcsFiles = [_ics, Path.Combine(_rig.Store.Root, "missing.ics")] } });
        WriteIcs(Meeting);
        _rig.Add(new NextEvent(_rig.Context, _feed));
        _rig.Pump();
        Assert.Equal([("work", true), ("missing", false)], _feed.Feeds.Select(f => (f.Name, f.Ok)));
    }

    [Fact]
    public void Deadlines_from_the_file_and_calendar_markers_with_48h_and_24h_severity()
    {
        File.WriteAllText(Path.Combine(_rig.Store.Root, "deadlines.txt"), "2026-11-06 09:00  Report\n2026-11-04 20:00  Tonight\n2026-11-01 09:00  Old\n");
        WriteIcs("BEGIN:VEVENT\nUID:due\nDTSTART;VALUE=DATE:20261110\nSUMMARY:Due: taxes\nEND:VEVENT");
        _rig.LoadRules("""{ "rules": [ { "id": "deadline-24h", "enabled": true } ] }""");
        var d = _rig.Add(new Deadlines(_rig.Context, _feed));
        _rig.Pump();
        Assert.Equal(["Tonight", "Report", "Due: taxes"], d.Upcoming.Select(x => x.Title.Reveal()));
        Assert.Equal(Severity.Critical, Deadlines.SeverityFor(d.Upcoming[0].Start - _rig.Clock.UtcNow));
        Assert.Equal(Severity.Warn, Deadlines.SeverityFor(d.Upcoming[1].Start - _rig.Clock.UtcNow));
        Assert.Equal(Severity.Normal, Deadlines.SeverityFor(d.Upcoming[2].Start - _rig.Clock.UtcNow));
        var toast = Assert.Single(_rig.Rules.Evaluate(), o => o.Toast is not null);
        Assert.Contains("Tonight", toast.Toast!.Title, StringComparison.Ordinal);
        Assert.Equal(3, d.Cards()[0].Blocks.Count);
    }

    [Fact]
    public void Event_mode_from_a_calendar_marker_and_by_hand_turns_the_state_on_and_off_at_its_edges()
    {
        WriteIcs("BEGIN:VEVENT\nUID:w\nDTSTART:20261104T100000Z\nDTEND:20261104T110000Z\nSUMMARY:Write [focus]\nEND:VEVENT");
        _rig.LoadRules();
        var em = _rig.Add(new EventMode(_rig.Context, _feed));
        var began = 0;
        em.Began += _ => began++;
        _rig.Pump();
        Assert.Equal("false", Inputs().States["event_mode"]);
        _rig.Advance(TimeSpan.FromHours(1));
        Assert.Equal("true", Inputs().States["event_mode"]);
        Assert.Equal(1, began);
        var pause = Assert.Single(_rig.Rules.Evaluate(), o => o.Action is not null);
        Assert.Equal("refits.pause", pause.Action!.Action);
        _rig.Advance(TimeSpan.FromHours(1));
        Assert.Equal("false", Inputs().States["event_mode"]);
        var undo = Assert.Single(_rig.Rules.Evaluate(), o => o.Action is not null);
        Assert.Equal("refits.resume", undo.Action!.Action);

        var r = _rig.Act(new ActionRequest { Action = "event_mode.start", Origin = Origin.Cli, Args = new Dictionary<string, string> { ["minutes"] = "30" } });
        Assert.Equal(ActionStatus.Ok, r.Status);
        Assert.Equal("true", Inputs().States["event_mode"]);
        _rig.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal("false", Inputs().States["event_mode"]);
        Assert.DoesNotContain(_rig.LogLines.Lines, l => l.Contains("Write", StringComparison.Ordinal));
    }

    [Fact]
    public void Focus_blocks_open_event_windows_even_with_the_module_off()
    {
        var em = new EventMode(_rig.Context, null);
        _rig.Features.Add(em); // not enabled
        em.Begin(new EventWindow("focus", "Focus", _rig.Clock.UtcNow, _rig.Clock.UtcNow.AddMinutes(25), "focus"));
        Assert.Equal("true", Inputs().States["event_mode"]);
        em.End("focus");
        Assert.Equal("false", Inputs().States["event_mode"]);
    }
}
