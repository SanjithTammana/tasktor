using Tasktor.Capture.Deep;

namespace Tasktor.Capture.Tests;

/// <summary>D-C1 in the capture host: track 1's activity is a same-app run, glances under 30 s absorbed, capped at an hour.</summary>
public sealed class ActivityBoundaryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_first_app_starts_the_activity_without_ending_one()
    {
        var b = new ActivityBoundary();
        Assert.False(b.Foreground("notepad.exe", T0));
        Assert.False(b.Tick(T0.AddMinutes(10)));
    }

    [Fact]
    public void A_glance_away_shorter_than_30_s_is_absorbed()
    {
        var b = new ActivityBoundary();
        b.Foreground("notepad.exe", T0);
        Assert.False(b.Foreground("mail.exe", T0.AddSeconds(10)));
        Assert.False(b.Tick(T0.AddSeconds(39)));
        Assert.False(b.Foreground("NOTEPAD.EXE", T0.AddSeconds(39))); // back in time, case-insensitive
        Assert.False(b.Tick(T0.AddMinutes(5)));
    }

    [Fact]
    public void Another_app_in_front_for_30_s_ends_the_activity_once()
    {
        var b = new ActivityBoundary();
        b.Foreground("notepad.exe", T0);
        b.Foreground("mail.exe", T0.AddSeconds(10));
        Assert.False(b.Tick(T0.AddSeconds(39)));
        Assert.True(b.Tick(T0.AddSeconds(40)));
        Assert.False(b.Tick(T0.AddSeconds(41)));
        Assert.False(b.Foreground("mail.exe", T0.AddSeconds(42))); // mail is the activity now
    }

    [Fact]
    public void A_third_app_starts_its_own_count()
    {
        var b = new ActivityBoundary();
        b.Foreground("notepad.exe", T0);
        b.Foreground("mail.exe", T0.AddSeconds(10));
        b.Foreground("chat.exe", T0.AddSeconds(30));
        Assert.False(b.Tick(T0.AddSeconds(59)));
        Assert.True(b.Tick(T0.AddSeconds(60)));
    }

    [Fact]
    public void An_unknown_foreground_changes_nothing()
    {
        var b = new ActivityBoundary();
        b.Foreground("notepad.exe", T0);
        Assert.False(b.Foreground(null, T0.AddMinutes(2)));
        Assert.False(b.Tick(T0.AddMinutes(3)));
    }

    [Fact]
    public void An_activity_longer_than_the_cap_ends_and_the_same_app_starts_a_new_one()
    {
        var b = new ActivityBoundary();
        b.Foreground("notepad.exe", T0);
        Assert.False(b.Tick(T0 + ActivityBoundary.Cap - TimeSpan.FromSeconds(1)));
        Assert.True(b.Tick(T0 + ActivityBoundary.Cap));
        Assert.False(b.Tick(T0 + ActivityBoundary.Cap + TimeSpan.FromMinutes(59)));
        Assert.True(b.Tick(T0 + ActivityBoundary.Cap + ActivityBoundary.Cap));
    }

    [Fact]
    public void The_thresholds_are_the_preregistered_ones()
    {
        var episodes = new Contracts.Workflow.EpisodeThresholds();
        Assert.Equal(TimeSpan.FromMilliseconds(episodes.GlanceMaxMs), ActivityBoundary.GlanceMax);
        Assert.Equal(TimeSpan.FromMilliseconds(episodes.ActivityCapMs), ActivityBoundary.Cap);
    }
}
