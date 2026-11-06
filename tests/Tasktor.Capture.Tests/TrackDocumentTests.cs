using System.Text;
using Tasktor.Capture.Deep;

namespace Tasktor.Capture.Tests;

/// <summary>C20.3 side tracks (YouTube while chess) and the activity document (sections, first seen + new lines, 4 KB caps).</summary>
public sealed class TrackDocumentTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 6, 20, 0, 0, TimeSpan.Zero);
    private const long Screen = 1920 * 1080;

    private static MediaState Video(bool audible, MediaStatus status = MediaStatus.Playing) =>
        new("chrome.exe", "Endgame lecture", "A channel", status, 0, audible);

    [Fact]
    public void Youtube_audible_behind_chess_opens_track_2_after_60_s_backdated_and_closes_30_s_after_it_stops()
    {
        var t = new TrackManager();
        Assert.Empty(t.OnForeground("chess.exe", T0));
        Assert.Empty(t.On(Video(audible: true), T0.AddSeconds(5)));
        Assert.Empty(t.Tick(T0.AddSeconds(64))); // 59 s of sound: not yet
        var open = Assert.Single(t.Tick(T0.AddSeconds(65)));
        Assert.Equal(new TrackChange(2, "chrome.exe", true, T0.AddSeconds(5)), open);
        Assert.Equal("chrome.exe", t.SideTracks[2]);

        Assert.Empty(t.On(Video(audible: false, MediaStatus.Paused), T0.AddSeconds(300)));
        Assert.Empty(t.Tick(T0.AddSeconds(329)));
        var close = Assert.Single(t.Tick(T0.AddSeconds(330)));
        Assert.Equal(new TrackChange(2, "chrome.exe", false, T0.AddSeconds(300)), close); // ends when the sound ended
        Assert.Empty(t.SideTracks);
    }

    [Fact]
    public void A_large_visible_window_counts_a_small_one_does_not_and_the_foreground_is_never_a_side_track()
    {
        var t = new TrackManager();
        VisibleSet Set(DateTimeOffset at, long notesPx, string fg = "code.exe") =>
            new(at, Screen, [new VisibleWindow(1, fg, Screen / 2, true), new VisibleWindow(2, fg == "code.exe" ? "notes.exe" : "code.exe", notesPx, false)]);

        t.On(Set(T0, Screen / 5)); // 20 %: too small
        Assert.Empty(t.Tick(T0.AddMinutes(5)));
        t.On(Set(T0.AddMinutes(5), Screen / 3)); // 33 %
        var open = Assert.Single(t.Tick(T0.AddMinutes(6)));
        Assert.Equal((2, "notes.exe", true), (open.Track, open.App, open.Opened));

        var swap = t.On(Set(T0.AddMinutes(7), Screen / 3, fg: "notes.exe")); // the user switches to it: it is track 1 now
        var closed = Assert.Single(swap, c => !c.Opened);
        Assert.Equal((2, T0.AddMinutes(7)), (closed.Track, closed.At));
    }

    [Fact]
    public void Two_side_tracks_take_the_lowest_free_numbers()
    {
        var t = new TrackManager();
        t.OnForeground("chess.exe", T0);
        t.On(Video(true), T0);
        t.On(new MediaState("music.exe", "Track", null, MediaStatus.Playing, 0, true), T0);
        var opened = t.Tick(T0.AddSeconds(60));
        Assert.Equal([2, 3], opened.Select(c => c.Track).Order());
        t.On(Video(false), T0.AddSeconds(100));
        t.Tick(T0.AddSeconds(130)); // chrome's track 2 closes
        t.On(new MediaState("radio.exe", "Live", null, MediaStatus.Playing, 0, true), T0.AddSeconds(130));
        var again = Assert.Single(t.Tick(T0.AddSeconds(190)));
        Assert.Equal((2, "radio.exe"), (again.Track, again.App));
    }

    private static KeyRun Run(string? text, Suppression s = Suppression.None, ActorKind actor = ActorKind.Human) => new()
    {
        Id = 1,
        View = "v",
        ElementRef = "e",
        StartMs = 0,
        EndMs = 10,
        Text = text,
        Keys = 12,
        Suppressed = s,
        Actor = new Actor(actor, actor == ActorKind.Agent ? "agent.exe" : null, ActorEvidence.None),
    };

    private static ViewDiff Diff(ViewKey view, DateTimeOffset at, params string[] names) =>
        new(view, at, 1, 0, [.. names.Select((n, i) => new DiffLine((ulong)i, 1, 1, n, null))], [], DiffSource.Uia);

    [Fact]
    public void A_document_has_typing_first_seen_and_new_lines_only_clicks_and_media_changes()
    {
        var b = new ActivityDocumentBuilder();
        var view = new ViewKey(0xAB, 1);
        b.Add(1, Run("e4 e5 Nf3"), T0);
        b.Add(1, Run(null, Suppression.Password), T0.AddSeconds(1));
        b.Add(1, Run("git status", actor: ActorKind.Agent), T0.AddSeconds(2));
        b.Add(1, Diff(view, T0.AddSeconds(3), "Inbox", "Weekly report", "Lunch"));
        b.Add(1, Diff(view, T0.AddSeconds(4), "Inbox", "Weekly report", "Invoice")); // only "Invoice" is new
        b.Add(1, new ClickEvent(T0.AddSeconds(5), 1, 10, 10, "button Send", new Actor(ActorKind.Human, null, ActorEvidence.Hardware)));
        b.Add(2, Video(true), T0.AddSeconds(6));
        b.Add(2, Video(true), T0.AddSeconds(7)); // unchanged: not repeated
        b.Add(2, Video(false, MediaStatus.Paused), T0.AddSeconds(8));

        var doc = b.Close(1, T0.AddSeconds(10))!;
        Assert.Equal((1, T0, T0.AddSeconds(10)), (doc.Track, doc.Start, doc.End));
        string Section(string heading) => Assert.Single(doc.Sections, s => s.Heading.StartsWith(heading, StringComparison.Ordinal)).Text;
        Assert.Equal("e4 e5 Nf3\n[password, 12 keys]\ngit status (agent:agent.exe)", Section("typed"));
        Assert.Equal("Inbox\nWeekly report\nLunch", Section("first seen"));
        Assert.Equal("Invoice", Section("new in"));
        Assert.Equal("button Send", Section("clicked"));
        Assert.False(b.IsOpen(1));
        Assert.Null(b.Close(1, T0));

        var media = b.Close(2, T0.AddSeconds(9))!;
        Assert.Equal("▶ Endgame lecture · A channel (chrome.exe)\n⏸ Endgame lecture · A channel (chrome.exe)", Assert.Single(media.Sections).Text);
    }

    [Fact]
    public void Each_section_is_capped_at_4_KB_and_the_rest_is_counted()
    {
        var b = new ActivityDocumentBuilder();
        var line = new string('x', 999);
        for (var i = 0; i < 6; i++)
        {
            b.Add(1, Run(line + i), T0.AddSeconds(i));
        }

        var doc = b.Close(1, T0.AddSeconds(6))!;
        var typed = Assert.Single(doc.Sections).Text;
        Assert.True(Encoding.UTF8.GetByteCount(typed) <= ActivityDocumentBuilder.MaxSectionBytes);
        Assert.Equal(4, typed.Split('\n').Length);
        Assert.Equal(2 * 1001, doc.Truncated); // two lines of 1000 chars + their newline
    }
}
