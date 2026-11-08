using Tasktor.Capture.Deep;
using Tasktor.Capture.Visibility;
using Tasktor.Contracts.Presets;
using Tasktor.Core.Windows;

namespace Tasktor.Capture.Tests;

/// <summary>C19.2: window changes are debounced, filtered to top-level windows, and only a changed set is posted.</summary>
public sealed class VisibilityProducerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 9, 0, 0, TimeSpan.Zero);

    private sealed class FakeGeometry : IWindowGeometry
    {
        public List<WindowInfo> List { get; } = [Window(1, "synthetic.exe", 0, 0, 100, 100)];

        public int Reads { get; private set; }

        public IReadOnlyList<RectPx> Monitors() => [new RectPx(0, 0, 200, 100)];

        public IReadOnlyList<WindowInfo> Windows()
        {
            Reads++;
            return [.. List];
        }

        public long Foreground() => 1;
    }

    private static WindowInfo Window(long hwnd, string app, int x, int y, int w, int h) =>
        new() { Hwnd = hwnd, Pid = 7, AppId = app, FramePx = new RectPx(x, y, w, h), Show = ShowState.Normal };

    [Fact]
    public void Changes_are_debounced_and_a_burst_is_capped()
    {
        var geometry = new FakeGeometry();
        var posted = new List<VisibleSet>();
        var p = new VisibilityProducer(geometry, posted.Add, () => T0);
        Assert.Null(p.DueAtMs);
        p.Changed(1000);
        p.Changed(1100);
        Assert.Equal(1100 + VisibilityProducer.DebounceMs, p.DueAtMs);
        Assert.False(p.Tick(1300));
        Assert.True(p.Tick(1350));
        Assert.Single(posted);
        Assert.Null(p.DueAtMs);

        // a drag that never pauses still posts within the cap
        for (var ms = 2000L; ms < 2000 + VisibilityProducer.MaxDelayMs; ms += 50) p.Changed(ms);
        Assert.Equal(2000 + VisibilityProducer.MaxDelayMs, p.DueAtMs);
        geometry.List.Add(Window(2, "other.exe", 100, 0, 100, 100));
        Assert.True(p.Tick(2000 + VisibilityProducer.MaxDelayMs));
        Assert.Equal(2, posted.Count);
        Assert.Equal(["synthetic.exe", "other.exe"], posted[^1].Windows.Select(w => w.App));
    }

    [Fact]
    public void An_unchanged_set_is_not_posted_again()
    {
        var geometry = new FakeGeometry();
        var posted = new List<VisibleSet>();
        var p = new VisibilityProducer(geometry, posted.Add, () => T0);
        p.Changed(0);
        p.Tick(1000);
        p.Changed(2000);
        Assert.True(p.Tick(3000)); // computed
        Assert.Equal(2, geometry.Reads);
        Assert.Single(posted); // same windows, same areas: nothing new
    }

    [Theory]
    [InlineData(0x0003u, 0, 0, false, true)] // foreground: any window
    [InlineData(0x000Bu, 0, 0, false, true)] // move-size end
    [InlineData(0x0016u, 0, 0, false, true)] // minimize start
    [InlineData(0x0017u, 0, 0, false, true)] // minimize end
    [InlineData(0x8002u, 0, 0, true, true)] // show, top-level window
    [InlineData(0x8002u, 0, 0, false, false)] // show, child window
    [InlineData(0x8003u, 0, 0, true, true)] // hide, top-level window
    [InlineData(0x800Bu, 0, 0, true, true)] // location change, top-level window
    [InlineData(0x800Bu, -9, 0, true, false)] // location change of the cursor (OBJID_CURSOR)
    [InlineData(0x800Bu, -8, 0, true, false)] // the caret
    [InlineData(0x800Bu, 0, 3, true, false)] // a child element
    [InlineData(0x800Cu, 0, 0, true, false)] // a title change is not geometry
    public void Only_top_level_geometry_events_count(uint evt, int idObject, int idChild, bool topLevel, bool counts) =>
        Assert.Equal(counts, VisibilityProducer.Counts(evt, idObject, idChild, 42, h => h == 42 && topLevel));

    [Fact]
    public void A_visible_set_carries_numbers_and_the_app_only()
    {
        // the producer has nowhere to put a title: a visible window is (handle, app, pixels, foreground)
        Assert.Equal(["App", "IsForeground", "VisiblePx", "Window"], typeof(VisibleWindow).GetProperties().Select(p => p.Name).Order());
    }

    [Fact]
    public void The_benchmark_times_enumeration_and_geometry_apart()
    {
        var result = VisibilityBench.Run(new FakeGeometry(), 50);
        Assert.Equal(50, result.Runs);
        Assert.Equal(1, result.WindowsMax);
        Assert.True(result.Enumerate.P50Us <= result.Enumerate.P95Us && result.Geometry.P95Us <= result.Geometry.MaxUs);
        Assert.DoesNotContain("synthetic", result.ToString(), StringComparison.Ordinal);
    }
}
