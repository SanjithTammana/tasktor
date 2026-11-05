using Tasktor.Core.Loop;
using Tasktor.Core.Shapes;
using Tasktor.Tests.Shared;

namespace Tasktor.Core.Tests;

public sealed class RingBufferTests
{
    [Fact]
    public void Keeps_the_newest_items_oldest_first()
    {
        var r = new RingBuffer<int>(3);
        for (var i = 1; i <= 5; i++)
        {
            r.Add(i);
        }

        Assert.Equal([3, 4, 5], r.ToArray());
        Assert.Equal(5, r.Latest);
        Assert.True(r.IsFull);
    }

    [Fact]
    public void Hour_ring_has_sixty_points_after_an_hour_of_ticks()
    {
        // A1 acceptance 3: after 60 min with no viewer, the flyout shows 60 tick points at once.
        var clock = new FakeClock();
        var loop = new LogicLoop(clock);
        var ring = new RingBuffer<double>(60);
        var sample = 0;
        loop.Every(TimeSpan.FromSeconds(60), () => ring.Add(sample++));
        for (var m = 0; m < 75; m++)
        {
            clock.Advance(TimeSpan.FromSeconds(60));
            loop.RunDue();
        }

        Assert.Equal(60, ring.Count);
        Assert.Equal(15, ring[0]);
        Assert.Equal(74, ring.Latest);
    }

    [Fact]
    public void Window_quantiles_are_exact()
    {
        var r = new RingBuffer<float>(20);
        for (var i = 1; i <= 10; i++)
        {
            r.Add(i);
        }

        Assert.Equal(10f, WindowStats.Max(r));
        Assert.Equal(5.5f, WindowStats.Median(r));
        Assert.Equal(9.1f, WindowStats.Quantile(r, 0.9), 3);
        Assert.True(float.IsNaN(WindowStats.Max(new RingBuffer<float>(2))));
    }
}

public sealed class KeyedTableTests
{
    private static KeyValuePair<int, string> R(int k, string v) => new(k, v);

    [Fact]
    public void Diff_gives_added_removed_changed()
    {
        var t = new KeyedTable<int, string>();
        t.Apply([R(1, "a"), R(2, "b"), R(3, "c")]);
        Assert.Equal(3, t.Added.Count);
        t.Apply([R(1, "a"), R(2, "B"), R(4, "d")]);
        Assert.Equal([4], t.Added);
        Assert.Equal([2], t.Changed);
        Assert.Equal(3, Assert.Single(t.Removed).Key);
        Assert.Equal("c", t.Removed[0].Value);
        Assert.Equal(3, t.Count);
    }

    [Fact]
    public void A_row_that_vanishes_mid_sample_leaves_no_ghost()
    {
        // A2 acceptance 3: a process that exits between list and open is simply absent next time.
        var t = new KeyedTable<int, string>();
        t.Apply([R(1, "a"), R(2, "b")]);
        t.Apply([R(1, "a")]);
        Assert.False(t.TryGet(2, out _));
        t.Apply([R(1, "a")]);
        Assert.Empty(t.Removed);
    }

    [Fact]
    public void Clear_drops_rows()
    {
        var t = new KeyedTable<int, string>();
        t.Apply([R(1, "a")]);
        t.Clear();
        Assert.Equal(0, t.Count);
    }
}

public sealed class TopNTests
{
    [Fact]
    public void Matches_a_full_sort_on_random_input()
    {
        var rng = new Random(7);
        for (var round = 0; round < 50; round++)
        {
            var items = Enumerable.Range(0, rng.Next(0, 200)).Select(_ => rng.Next(1000)).ToList();
            var n = rng.Next(1, 8);
            var expected = items.OrderByDescending(x => x).Take(n).ToList();
            Assert.Equal(expected, TopN<int>.Of(items, n, Comparer<int>.Default));
        }
    }
}

public sealed class RateCounterTests
{
    [Fact]
    public void First_read_has_no_rate_then_deltas_per_second()
    {
        var clock = new FakeClock();
        var r = new RateCounter();
        Assert.Null(r.Update(100, clock.UtcNow));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(50, r.Update(200, clock.UtcNow));
    }

    [Fact]
    public void A_counter_that_goes_backwards_restarts()
    {
        var clock = new FakeClock();
        var r = new RateCounter();
        r.Update(100, clock.UtcNow);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(r.Update(10, clock.UtcNow));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(5, r.Update(15, clock.UtcNow));
    }
}

public sealed class TreeRollupTests
{
    [Fact]
    public void Anchors_follow_the_fold_rule_and_sums_roll_up()
    {
        // 0 ← 1 ← 2, 0 ← 3; 4 alone. Node 3 does not fold.
        var t = new TreeRollup();
        t.Reset(5);
        t.SetParent(1, 0);
        t.SetParent(2, 1);
        t.SetParent(3, 0);
        t.ResolveAnchors(i => i != 3);
        Assert.Equal([0, 0, 0, 3, 4], Enumerable.Range(0, 5).Select(t.Anchor));
        Span<double> sums = stackalloc double[5];
        t.SumToAnchors([1, 2, 3, 4, 5], sums);
        Assert.Equal(6, sums[0]);
        Assert.Equal(4, sums[3]);
        Assert.Equal(5, sums[4]);
        Assert.Equal([3, 1], t.Children(0).ToArray());
    }

    [Fact]
    public void Subtree_totals_for_folder_trees()
    {
        var t = new TreeRollup();
        t.Reset(4);
        t.SetParent(1, 0);
        t.SetParent(2, 0);
        t.SetParent(3, 2);
        Span<double> totals = stackalloc double[4];
        t.SubtreeTotals([1, 10, 100, 1000], totals);
        Assert.Equal(1111, totals[0]);
        Assert.Equal(1100, totals[2]);
    }

    [Fact]
    public void Buffers_are_reused_across_resets()
    {
        var t = new TreeRollup();
        t.Reset(3);
        t.SetParent(1, 0);
        t.ResolveAnchors(_ => true);
        t.Reset(2);
        t.ResolveAnchors(_ => true);
        Assert.Equal(-1, t.Parent(1));
        Assert.Equal(1, t.Anchor(1));
    }
}
