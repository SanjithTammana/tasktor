using Tasktor.Contracts.Calendar;
using Tasktor.Contracts.Platform;
using Tasktor.Core.Calendar;

namespace Tasktor.Core.Tests;

/// <summary>U2 CalendarSource reading: the ICS subset, recurrences, zones, overrides, the 14-day / 200-instance window.</summary>
public sealed class CalendarTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    // A fixed "+01:00 / +02:00 with EU rules" zone, so tests do not depend on the machine's tz database.
    private const string BerlinZone = """
        BEGIN:VTIMEZONE
        TZID:Custom/Berlin
        BEGIN:DAYLIGHT
        TZOFFSETFROM:+0100
        TZOFFSETTO:+0200
        DTSTART:19700329T020000
        RRULE:FREQ=YEARLY;BYMONTH=3;BYDAY=-1SU
        END:DAYLIGHT
        BEGIN:STANDARD
        TZOFFSETFROM:+0200
        TZOFFSETTO:+0100
        DTSTART:19701025T030000
        RRULE:FREQ=YEARLY;BYMONTH=10;BYDAY=-1SU
        END:STANDARD
        END:VTIMEZONE
        """;

    private static string Cal(string body) => "BEGIN:VCALENDAR\r\nVERSION:2.0\r\n" + body.Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\nEND:VCALENDAR\r\n";

    private static List<CalEvent> Expand(string ics, DateTimeOffset from, TimeZoneInfo? local = null, int days = 14)
        => CalendarExpansion.Expand(IcsReader.Parse(Cal(ics)), from, from.AddDays(days), local ?? Utc, CalendarMarkers.Default);

    private static readonly DateTimeOffset Mon = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero); // a Monday

    [Fact]
    public void Folded_lines_params_and_escapes_are_read_and_descriptions_are_not()
    {
        var cal = IcsReader.Parse(Cal("""
            BEGIN:VEVENT
            UID:a@x
            DTSTART:20261102T090000Z
            DTEND:20261102T093000Z
            SUMMARY:Stand\, up \; sync
              continued
            LOCATION;LANGUAGE=en:"Room: 4"
            DESCRIPTION:secret agenda
            ATTENDEE;CN="Someone":mailto:x@y
            END:VEVENT
            """));
        var ev = Assert.Single(cal.Events);
        Assert.Equal("Stand, up ; sync continued", ev.Summary);
        Assert.Equal("\"Room: 4\"", ev.Location);
        Assert.Equal(TimeSpan.FromMinutes(30), ev.End!.Value.Value - ev.Start.Value);
        Assert.DoesNotContain(typeof(IcsEvent).GetProperties(), p => p.Name is "Description" or "Attendees");
    }

    [Fact]
    public void Weekly_byday_with_count_counts_dtstart_and_respects_the_window()
    {
        var rows = Expand("""
            BEGIN:VEVENT
            UID:w
            DTSTART:20261102T090000Z
            DURATION:PT30M
            RRULE:FREQ=WEEKLY;BYDAY=MO,WE,FR;COUNT=5
            SUMMARY:Gym
            END:VEVENT
            """, Mon);
        Assert.Equal(["11-02", "11-04", "11-06", "11-09", "11-11"], rows.Select(r => r.Start.ToString("MM-dd", System.Globalization.CultureInfo.InvariantCulture)));
        Assert.All(rows, r => Assert.Equal(TimeSpan.FromMinutes(30), r.End - r.Start));
    }

    [Fact]
    public void Monthly_last_friday_and_yearly_and_interval_and_until()
    {
        var rows = Expand("""
            BEGIN:VEVENT
            UID:m
            DTSTART:20260130T100000Z
            RRULE:FREQ=MONTHLY;BYDAY=-1FR
            SUMMARY:Review
            END:VEVENT
            BEGIN:VEVENT
            UID:d
            DTSTART:20261101T080000Z
            RRULE:FREQ=DAILY;INTERVAL=3;UNTIL=20261110T080000Z
            SUMMARY:Water plants
            END:VEVENT
            BEGIN:VEVENT
            UID:y
            DTSTART;VALUE=DATE:20201105
            RRULE:FREQ=YEARLY
            SUMMARY:Anniversary
            END:VEVENT
            """, Mon, days: 60);
        Assert.Equal([new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 12, 25, 10, 0, 0, TimeSpan.Zero)], rows.Where(r => r.Key.Uid == "m").Select(r => r.Start));
        Assert.Equal([4, 7, 10], rows.Where(r => r.Key.Uid == "d").Select(r => r.Start.Day));
        var y = Assert.Single(rows, r => r.Key.Uid == "y");
        Assert.True(y.AllDay);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero), y.Start);
    }

    [Fact]
    public void Exdate_and_recurrence_id_overrides_and_cancelled_instances()
    {
        var rows = Expand("""
            BEGIN:VEVENT
            UID:s
            DTSTART:20261102T090000Z
            DTEND:20261102T091500Z
            RRULE:FREQ=DAILY;COUNT=5
            EXDATE:20261103T090000Z
            SUMMARY:Standup
            END:VEVENT
            BEGIN:VEVENT
            UID:s
            RECURRENCE-ID:20261104T090000Z
            DTSTART:20261104T140000Z
            DTEND:20261104T141500Z
            SUMMARY:Standup (moved)
            END:VEVENT
            BEGIN:VEVENT
            UID:s
            RECURRENCE-ID:20261105T090000Z
            DTSTART:20261105T090000Z
            STATUS:CANCELLED
            SUMMARY:Standup
            END:VEVENT
            """, Mon);
        Assert.Equal(["02 09", "04 14", "06 09"], rows.Select(r => r.Start.ToString("dd HH", System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal("Standup (moved)", rows[1].Title.Reveal());
    }

    [Fact]
    public void Zoned_times_keep_their_wall_clock_across_a_dst_change_using_the_files_vtimezone()
    {
        // 25 Oct 2026 is the EU change: 09:00 local is 07:00Z before and 08:00Z after.
        var rows = Expand(BerlinZone + """

            BEGIN:VEVENT
            UID:z
            DTSTART;TZID=Custom/Berlin:20261023T090000
            DTEND;TZID=Custom/Berlin:20261023T100000
            RRULE:FREQ=DAILY;COUNT=4
            SUMMARY:Daily
            END:VEVENT
            """, new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal([7, 7, 8, 8], rows.Select(r => r.Start.UtcDateTime.Hour));
        Assert.All(rows, r => Assert.Equal(TimeSpan.FromHours(1), r.End - r.Start));
    }

    [Fact]
    public void Floating_times_and_dates_are_local()
    {
        var local = TimeZoneInfo.CreateCustomTimeZone("Test+5", TimeSpan.FromHours(5), "Test+5", "Test+5");
        var rows = Expand("""
            BEGIN:VEVENT
            UID:f
            DTSTART:20261102T090000
            SUMMARY:Floating
            END:VEVENT
            """, Mon, local);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.FromHours(5)), Assert.Single(rows).Start);
    }

    [Fact]
    public void Ongoing_events_stay_and_finished_ones_go_and_at_most_200_instances_are_kept()
    {
        var now = new DateTimeOffset(2026, 11, 2, 9, 30, 0, TimeSpan.Zero);
        var rows = Expand("""
            BEGIN:VEVENT
            UID:on
            DTSTART:20261102T090000Z
            DTEND:20261102T100000Z
            SUMMARY:Ongoing
            END:VEVENT
            BEGIN:VEVENT
            UID:past
            DTSTART:20261102T080000Z
            DTEND:20261102T090000Z
            SUMMARY:Done
            END:VEVENT
            BEGIN:VEVENT
            UID:many
            DTSTART:20261102T100000Z
            RRULE:FREQ=DAILY
            BYDAY:ignored
            SUMMARY:x
            END:VEVENT
            """, now);
        Assert.Contains(rows, r => r.Key.Uid == "on");
        Assert.DoesNotContain(rows, r => r.Key.Uid == "past");

        var body = string.Concat(Enumerable.Range(0, 300).Select(i => $"BEGIN:VEVENT\nUID:e{i}\nDTSTART:20261103T{i / 60 % 24:00}{i % 60:00}00Z\nSUMMARY:x\nEND:VEVENT\n"));
        Assert.Equal(CalendarExpansion.MaxInstances, Expand(body.TrimEnd('\n'), Mon).Count);
    }

    [Fact]
    public void Garbage_and_unsupported_rules_are_skipped_not_thrown()
    {
        var cal = IcsReader.Parse(Cal("""
            no colon here
            BEGIN:VEVENT
            UID:h
            DTSTART:20261102T090000Z
            RRULE:FREQ=HOURLY
            SUMMARY:Hourly becomes single
            END:VEVENT
            BEGIN:VEVENT
            UID:bad
            DTSTART:notadate
            END:VEVENT
            """));
        Assert.Single(cal.Events);
        Assert.Null(cal.Events[0].Rule);
        Assert.True(cal.Skipped >= 2);
    }

    [Theory]
    [InlineData("https://teams.microsoft.com/l/meetup-join/abc", true)]
    [InlineData("Join: https://us02web.zoom.us/j/123?pwd=x", true)]
    [InlineData("https://meet.google.com/abc-defg-hij", true)]
    [InlineData("https://company.webex.com/meet/someone", true)]
    [InlineData("https://zoom.us.evil.example/j/1", false)]
    [InlineData("Room 4, https://example.com", false)]
    public void Meeting_links_match_known_hosts_only(string location, bool meeting)
        => Assert.Equal(meeting, MeetingLinks.Find(location) is not null);

    [Fact]
    public void Deadline_and_event_markers()
    {
        var rows = Expand("""
            BEGIN:VEVENT
            UID:due
            DTSTART;VALUE=DATE:20261105
            SUMMARY:Due: tax form
            END:VEVENT
            BEGIN:VEVENT
            UID:timed
            DTSTART:20261105T090000Z
            SUMMARY:Due soon but timed
            END:VEVENT
            BEGIN:VEVENT
            UID:focus
            DTSTART:20261105T130000Z
            DTEND:20261105T150000Z
            SUMMARY:Writing [focus]
            END:VEVENT
            """, Mon);
        Assert.True(rows.Single(r => r.Key.Uid == "due").Deadline);
        Assert.False(rows.Single(r => r.Key.Uid == "timed").Deadline);
        Assert.True(rows.Single(r => r.Key.Uid == "focus").EventWindow);
    }

    [Fact]
    public void The_deadlines_file()
    {
        var (rows, bad) = DeadlinesFile.Parse("# mine\n2026-11-20 17:00  Project report\n2026-11-21  Whole day thing\nnot a line\n\n", Utc);
        Assert.Equal(1, bad);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 17, 0, 0, TimeSpan.Zero), rows[0].Start);
        Assert.Equal("Project report", rows[0].Title.Reveal());
        Assert.Equal(new DateTimeOffset(2026, 11, 21, 23, 59, 0, TimeSpan.Zero), rows[1].Start);
        Assert.Equal("Whole day thing", rows[1].Title.Reveal());
        Assert.All(rows, r => Assert.True(r.Deadline));
    }

    [Fact]
    public void Plugin_rows_are_filtered_to_the_window_and_titles_stay_sensitive()
    {
        var file = new CalendarFile
        {
            GeneratedAt = Mon,
            Events =
            [
                new CalendarEvent { Uid = "p1", Start = Mon.AddHours(10), End = Mon.AddHours(11), Title = "Call", Location = "https://meet.google.com/x" },
                new CalendarEvent { Uid = "p2", Start = Mon.AddDays(20), Title = "Far" },
                new CalendarEvent { Uid = "p3", Start = Mon.AddHours(-3), End = Mon.AddHours(-2), Title = "Past" },
            ],
        };
        var rows = CalendarExpansion.FromPlugin(file, Mon, Mon.AddDays(14), CalendarMarkers.Default);
        var r = Assert.Single(rows);
        Assert.NotNull(r.MeetingUrl);
        Assert.Equal(Sensitive<string>.Redacted, r.Title.ToString());
        Assert.Equal(CalendarOrigin.Plugin, r.Origin);
    }

    [Fact]
    public void A_vtimezone_without_a_rule_and_the_onset_boundaries()
    {
        var zone = new IcsTimeZone("Z", [new IcsObservance(new DateTime(2000, 1, 1), TimeSpan.FromHours(3), TimeSpan.FromHours(4), null, [])]);
        Assert.Equal(TimeSpan.FromHours(3), IcsZones.FileOffset(zone, new DateTime(1999, 1, 1)));
        Assert.Equal(TimeSpan.FromHours(4), IcsZones.FileOffset(zone, new DateTime(2001, 1, 1)));
    }
}

/// <summary>The C# reader and the calendar plugin's Python reader expand the shared fixture identically.</summary>
public sealed class CalendarParityTests
{
    [Fact]
    public void The_shared_fixture_expands_to_the_expected_instances()
    {
        var dir = Path.Combine(global::Tasktor.Tests.Shared.RepoPaths.Root, "tests", "fixtures", "ics");
        using var expected = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "sample.expected.json")));
        var from = DateTimeOffset.Parse(expected.RootElement.GetProperty("from").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        var rows = CalendarExpansion.Expand(IcsReader.Parse(File.ReadAllText(Path.Combine(dir, "sample.ics"))), from, from.AddDays(expected.RootElement.GetProperty("days").GetInt32()), TimeZoneInfo.Utc, CalendarMarkers.Default);
        var want = expected.RootElement.GetProperty("instances").EnumerateArray().Select(a => a[0].GetString() + " " + a[1].GetString()).ToList();
        var got = rows.Select(r => r.Key.Uid + " " + r.Start.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(want, got);
        Assert.True(rows.Single(r => r.Key.Uid == "due@fixture").Deadline);
        Assert.True(rows.First(r => r.Key.Uid == "biweekly@fixture").EventWindow);
        Assert.NotNull(rows.First(r => r.Key.Uid == "standup@fixture").MeetingUrl);
    }
}
