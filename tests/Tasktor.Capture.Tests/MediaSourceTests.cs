using Tasktor.Capture.Deep;
using Tasktor.Capture.Media;

namespace Tasktor.Capture.Tests;

/// <summary>#9: AUMIDs map to the same app ids as audio sessions; peaks per app and second; session ends; status never invented.</summary>
public sealed class MediaSourceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Chrome", "chrome.exe")]
    [InlineData("MSEdge", "msedge.exe")]
    [InlineData("C:\\Program Files\\VideoLAN\\VLC\\vlc.exe", "vlc.exe")]
    [InlineData("Spotify.exe", "spotify.exe")]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "spotifyab.spotifymusic_zpdnekdrzrea0")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void Aumids_map_to_app_ids(string aumid, string? app) => Assert.Equal(app, MediaAppMap.FromAumid(aumid));

    [Theory]
    [InlineData(4, MediaStatus.Playing)]
    [InlineData(5, MediaStatus.Paused)]
    [InlineData(3, MediaStatus.Stopped)]
    [InlineData(0, null)] // closed
    [InlineData(1, null)] // opened
    [InlineData(2, null)] // changing
    [InlineData(9, null)]
    public void Playback_status_is_never_invented(int smtc, MediaStatus? status) => Assert.Equal(status, MediaAppMap.Status(smtc));

    [Fact]
    public void Peaks_are_the_max_per_app_and_a_session_end_stops_the_app()
    {
        var peaks = new List<(string App, float Peak)>();
        var stopped = new List<string>();
        var logic = new AudioMeterLogic(pid => pid switch { 1 or 2 => "player.exe", 3 => "game.exe", _ => null }, (app, _, p) => peaks.Add((app, p)), stopped.Add);
        logic.Sample(T0, [new(1, 0.2f, true), new(2, 0.5f, true), new(3, 0.1f, true), new(4, 0.9f, true)]);
        Assert.Equal([("game.exe", 0.1f), ("player.exe", 0.5f)], peaks.OrderBy(p => p.App));
        Assert.Empty(stopped); // pid 4 has no app: ignored

        peaks.Clear();
        logic.Sample(T0.AddSeconds(1), [new(1, 0.3f, true), new(3, 0f, false)]);
        Assert.Equal([("player.exe", 0.3f)], peaks);
        Assert.Equal(["game.exe"], stopped);

        logic.Sample(T0.AddSeconds(2), []);
        Assert.Equal(["game.exe", "player.exe"], stopped);
        Assert.False(logic.AnyActive);
    }

    [Fact]
    public void An_inactive_session_posts_no_peak()
    {
        var peaks = new List<float>();
        var logic = new AudioMeterLogic(_ => "player.exe", (_, _, p) => peaks.Add(p), _ => { });
        logic.Sample(T0, [new(1, 0.7f, false)]);
        Assert.Empty(peaks);
    }

    [Fact]
    public void A_muted_tone_never_makes_the_app_audible()
    {
        var states = new List<MediaState>();
        var media = new MediaSessionLogic(s => states.Add(s.Reveal()));
        media.Metadata("tone.exe", null, null, MediaStatus.Playing, 0);
        var logic = new AudioMeterLogic(_ => "tone.exe", (app, at, p) => media.Peak(app, at, p), app => media.AudioStopped(app));
        for (var s = 0; s < 6; s++) logic.Sample(T0.AddSeconds(s), [new(1, 0f, true)]);
        Assert.DoesNotContain(states, x => x.Audible);
        for (var s = 6; s < 12; s++) logic.Sample(T0.AddSeconds(s), [new(1, 0.4f, true)]);
        Assert.Contains(states, x => x.Audible);
        logic.Sample(T0.AddSeconds(12), []);
        Assert.False(states[^1].Audible);
    }
}

public sealed class SmtcSessionSetTests
{
    [Fact]
    public void Apps_come_once_and_leave_with_their_last_session()
    {
        var set = new SmtcSessionSet();
        var (added, removed) = set.Sync(["Chrome", "Chrome", "vlc.exe", null, ""]);
        Assert.Equal(["chrome.exe", "vlc.exe"], added);
        Assert.Empty(removed);
        (added, removed) = set.Sync(["Chrome"]);
        Assert.Empty(added);
        Assert.Equal(["vlc.exe"], removed);
        (_, removed) = set.Sync([]);
        Assert.Equal(["chrome.exe"], removed);
    }
}
