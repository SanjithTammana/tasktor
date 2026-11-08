using Tasktor.Capture.Deep;
using Tasktor.Capture.Media;
using Tasktor.Contracts.Platform;

namespace Tasktor.Capture.Tests;

public sealed class MediaSessionLogicTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Audible_requires_three_peaks_strictly_above_threshold_in_the_last_five_seconds()
    {
        var sink = new FakeSink();
        var logic = new MediaSessionLogic(sink.Post);
        Assert.True(logic.Metadata("app.exe", Sensitive.Of("Sample"), Sensitive.Of("Artist"), MediaStatus.Playing, 100));
        Assert.False(Assert.Single(sink.Events).Reveal().Audible);
        Assert.True(logic.Peak("app.exe", T0, 0.01f));
        Assert.True(logic.Peak("app.exe", T0.AddSeconds(1), 0.02f));
        Assert.True(logic.Peak("app.exe", T0.AddSeconds(2), 0.03f));
        Assert.Single(sink.Events);
        Assert.True(logic.Peak("app.exe", T0.AddSeconds(3), 0.04f));
        Assert.True(Assert.Single(sink.Events.Skip(1)).Reveal().Audible);
        logic.Tick(T0.AddSeconds(7));
        Assert.False(sink.Events[^1].Reveal().Audible);
    }

    [Fact]
    public void Peaks_in_the_same_second_replace_the_sample_instead_of_counting_twice()
    {
        var sink = new FakeSink();
        var logic = new MediaSessionLogic(sink.Post);
        logic.Metadata("app.exe", null, null, MediaStatus.Paused, 0);
        logic.Peak("app.exe", T0, 0.8f);
        logic.Peak("app.exe", T0.AddMilliseconds(300), 0.9f);
        logic.Peak("app.exe", T0.AddMilliseconds(700), 0.7f);
        Assert.Single(sink.Events);
        logic.Peak("app.exe", T0.AddSeconds(1), 0.8f);
        logic.Peak("app.exe", T0.AddSeconds(2), 0.8f);
        Assert.True(sink.Events[^1].Reveal().Audible);
    }

    [Fact]
    public void Unknown_playback_or_invalid_meter_values_are_ignored_without_inventing_a_meaning()
    {
        var sink = new FakeSink();
        var logic = new MediaSessionLogic(sink.Post);
        Assert.False(logic.Metadata("app.exe", null, null, (MediaStatus)255, 0));
        Assert.Empty(sink.Events);
        Assert.True(logic.Metadata("app.exe", null, null, MediaStatus.Playing, 0));
        Assert.False(logic.Peak("app.exe", T0, float.NaN));
        Assert.False(logic.Peak("app.exe", T0, -0.1f));
        Assert.False(logic.Peak("app.exe", T0, 1.1f));
        Assert.Single(sink.Events);
    }

    [Fact]
    public void Metadata_is_sensitive_until_the_existing_media_state_is_emitted_and_duplicates_are_silent()
    {
        var sink = new FakeSink();
        var logic = new MediaSessionLogic(sink.Post);
        var title = Sensitive.Of("Sample title");
        var artist = Sensitive.Of("Sample artist");
        Assert.True(logic.Metadata("player.exe", title, artist, MediaStatus.Playing, 123));
        Assert.Equal("<redacted>", Assert.Single(sink.Events).ToString());
        Assert.Equal(new MediaState("player.exe", "Sample title", "Sample artist", MediaStatus.Playing, 123, false), Assert.Single(sink.Events).Reveal());
        Assert.True(logic.Metadata("player.exe", title, artist, MediaStatus.Playing, 123));
        Assert.Single(sink.Events);
        Assert.True(logic.Metadata("player.exe", title, artist, MediaStatus.Paused, 123));
        Assert.Equal(2, sink.Events.Count);
    }

    [Fact]
    public void Meter_samples_before_metadata_are_retained_but_do_not_invent_a_playback_state()
    {
        var sink = new FakeSink();
        var logic = new MediaSessionLogic(sink.Post);
        logic.Peak("app.exe", T0, 0.3f);
        logic.Peak("app.exe", T0.AddSeconds(1), 0.3f);
        logic.Peak("app.exe", T0.AddSeconds(2), 0.3f);
        Assert.Empty(sink.Events);
        logic.Metadata("app.exe", null, null, MediaStatus.Playing, 0);
        Assert.True(Assert.Single(sink.Events).Reveal().Audible);
    }

    [Fact]
    public void Audio_stop_clears_audibility_without_changing_playback_status()
    {
        var sink = new FakeSink();
        var logic = new MediaSessionLogic(sink.Post);
        logic.Metadata("app.exe", null, null, MediaStatus.Playing, 5);
        logic.Peak("app.exe", T0, 0.5f);
        logic.Peak("app.exe", T0.AddSeconds(1), 0.5f);
        logic.Peak("app.exe", T0.AddSeconds(2), 0.5f);
        Assert.True(sink.Events[^1].Reveal().Audible);
        Assert.True(logic.AudioStopped("app.exe"));
        Assert.False(sink.Events[^1].Reveal().Audible);
        Assert.Equal(MediaStatus.Playing, sink.Events[^1].Reveal().Status);
        Assert.False(logic.AudioStopped("unknown.exe"));
    }

    [Fact]
    public void Older_samples_cannot_rewrite_a_newer_window_and_apps_are_independent()
    {
        var sink = new FakeSink();
        var logic = new MediaSessionLogic(sink.Post);
        logic.Metadata("a.exe", null, null, MediaStatus.Playing, 0);
        logic.Metadata("b.exe", null, null, MediaStatus.Playing, 0);
        logic.Peak("a.exe", T0, 0.5f);
        logic.Peak("a.exe", T0.AddSeconds(1), 0.5f);
        logic.Peak("a.exe", T0.AddSeconds(2), 0.5f);
        Assert.False(logic.Peak("a.exe", T0, 0));
        Assert.True(sink.Events[^1].Reveal().Audible);
        Assert.DoesNotContain(sink.Events, e => e.Reveal().App == "b.exe" && e.Reveal().Audible);
    }
    private sealed class FakeSink
    {
        public List<Sensitive<MediaState>> Events { get; } = [];
        public void Post(Sensitive<MediaState> state) => Events.Add(state);
    }
}



