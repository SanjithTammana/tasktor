using Tasktor.Capture.Deep;

namespace Tasktor.Capture.Tests;

/// <summary>C19.3: the per-activity pointer features that outlive the 7-day raw path.</summary>
public sealed class PointerFeaturesTests
{
    [Fact]
    public void A_scripted_path_gives_length_speeds_rates_and_dwell_share()
    {
        var f = new PointerFeatureBuilder();
        // 10 samples 100 ms apart along x, 10 px each: 90 px in 0.9 s at 100 px/s.
        for (var i = 0; i < 10; i++) f.Add(Move(i * 100, i * 10, 0));

        // A rest of 5 s, then 5 samples 100 ms apart, 30 px each: 120 px at 300 px/s.
        for (var i = 0; i < 5; i++) f.Add(Move(5900 + (i * 100), 90 + (i * 30), 0));

        f.Add(new PointerEvent(PointerEventKind.Click, 6400, 210, 0, 1));
        f.Add(new PointerEvent(PointerEventKind.Click, 6500, 210, 0, 1));
        f.Add(new PointerEvent(PointerEventKind.Wheel, 6600, 210, 0, WheelDelta: -120));

        var r = f.Build(TimeSpan.FromMinutes(1))!;
        Assert.Equal(90 + 120, r.PathLengthPx, 3); // the pointer did not move during the rest
        Assert.Equal(100, r.SpeedP50, 3);
        Assert.Equal(300, r.SpeedP90, 3);
        Assert.Equal(2, r.ClicksPerMinute, 3);
        Assert.Equal(1, r.ScrollsPerMinute, 3);

        // moving time: 9 + 4 gaps of 100 ms = 1.3 s of 60 s (the 5 s gap is a rest)
        Assert.Equal(1 - (1.3 / 60), r.DwellShare, 3);
    }

    [Fact]
    public void No_pointer_input_gives_no_features_and_a_still_pointer_dwells_fully()
    {
        Assert.Null(new PointerFeatureBuilder().Build(TimeSpan.FromMinutes(1)));

        var f = new PointerFeatureBuilder();
        f.Add(new PointerEvent(PointerEventKind.Click, 0, 5, 5, 1));
        var r = f.Build(TimeSpan.FromSeconds(30))!;
        Assert.Equal(0, r.PathLengthPx);
        Assert.Equal(0, r.SpeedP50);
        Assert.Equal(1, r.DwellShare);
        Assert.Equal(2, r.ClicksPerMinute, 3);
    }

    [Fact]
    public void Speed_samples_are_bounded_for_long_activities()
    {
        var f = new PointerFeatureBuilder();
        for (var i = 0; i < PointerFeatureBuilder.MaxSpeedSamples * 3; i++) f.Add(Move(i * 100L, i % 2 == 0 ? 0 : 10, 0));
        Assert.True(f.SpeedSamples <= PointerFeatureBuilder.MaxSpeedSamples);
        var r = f.Build(TimeSpan.FromHours(2))!;
        Assert.Equal(100, r.SpeedP50, 3);
    }

    [Fact]
    public void Target_resolutions_are_not_counted_as_clicks()
    {
        var f = new PointerFeatureBuilder();
        f.Add(new PointerEvent(PointerEventKind.Click, 0, 5, 5, 1));
        f.Add(new PointerEvent(PointerEventKind.Target, 0, 5, 5, 1, Target: "button: synthetic"));
        Assert.Equal(1, f.Build(TimeSpan.FromMinutes(1))!.ClicksPerMinute, 3);
    }

    private static PointerEvent Move(long ms, int x, int y) => new(PointerEventKind.Move, ms, x, y);
}
