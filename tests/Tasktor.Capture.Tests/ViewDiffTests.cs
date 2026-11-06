using System.Text;
using Tasktor.Capture.Deep;

namespace Tasktor.Capture.Tests;

/// <summary>C §4 / §5: the hook ring, stable refs, snapshot diffs with re-baseline and churn, and the field value diff.</summary>
public sealed class ViewDiffTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);
    private static readonly ViewKey Mail = new(0x10, 1);

    [Theory]
    [InlineData("", 0xEF46DB3751D8E999UL)]
    [InlineData("a", 0xD24EC4F1A98C6E5BUL)]
    [InlineData("abc", 0x44BC2CF5AD770999UL)]
    [InlineData("Nobody inspects the spammish repetition", 0xFBCEA83C8A378BF1UL)]
    public void XxHash64_matches_the_reference_vectors(string input, ulong expected) =>
        Assert.Equal(expected, XxHash64.Hash(Encoding.ASCII.GetBytes(input)));

    [Fact]
    public void Ring_keeps_order_counts_overwrites_and_never_returns_a_torn_entry()
    {
        var ring = new InputRing(8);
        for (var i = 0; i < 5; i++)
        {
            ring.Write(new RawInput((ushort)i, 0, 0, i, false));
        }

        var buf = new RawInput[16];
        Assert.Equal(5, ring.Drain(buf));
        Assert.Equal([0, 1, 2, 3, 4], buf[..5].Select(r => (int)r.Vk));
        Assert.Equal(0, ring.Dropped);

        for (var i = 0; i < 20; i++)
        {
            ring.Write(new RawInput((ushort)(100 + i), 0, 0, i, false));
        }

        var n = ring.Drain(buf);
        Assert.Equal(8, n); // the newest ring-full
        Assert.Equal(Enumerable.Range(112, 8), buf[..n].Select(r => (int)r.Vk));
        Assert.Equal(12, ring.Dropped);
        Assert.Throws<ArgumentOutOfRangeException>(() => new InputRing(6));
    }

    [Fact]
    public void Ring_under_a_concurrent_writer_delivers_an_increasing_sequence_and_accounts_for_every_entry()
    {
        const int Total = 2_000_000;
        var ring = new InputRing(1024);
        var writer = new Thread(() =>
        {
            for (var i = 1; i <= Total; i++)
            {
                ring.Write(new RawInput(0, 0, 0, i, false));
            }
        });
        writer.Start();
        var buf = new RawInput[256];
        long last = 0, got = 0;
        while (writer.IsAlive || got + ring.Dropped < Total)
        {
            var n = ring.Drain(buf);
            for (var i = 0; i < n; i++)
            {
                Assert.True(buf[i].TimeMs > last, "entries arrive in order and are never torn");
                last = buf[i].TimeMs;
            }

            got += n;
            if (!writer.IsAlive && n == 0 && got + ring.Dropped >= Total)
            {
                break;
            }
        }

        Assert.Equal(Total, got + ring.Dropped);
        Assert.Equal(Total, last); // the newest entry always survives
    }

    [Fact]
    public void Refs_are_stable_by_identity_and_survive_an_unrelated_sibling_insert()
    {
        var root = RefMinter.Ref(0, null, 1, "Inbox", 0);
        var first = RefMinter.Ref(root, null, 2, "Subject", 0);
        Assert.Equal(first, RefMinter.Ref(root, null, 2, "Subject", 0));
        Assert.NotEqual(first, RefMinter.Ref(root, null, 2, "Subject", 1)); // the second "Subject" sibling
        Assert.NotEqual(first, RefMinter.Ref(root, null, 3, "Subject", 0)); // another role
        Assert.Equal(RefMinter.Ref(root, "send", 4, "Send", 0), RefMinter.Ref(root, "send", 4, "Send now", 0)); // AutomationId wins
        Assert.NotEqual(RefMinter.Ref(root, "send", 4, "x", 0), RefMinter.Ref(first, "send", 4, "x", 0)); // parent matters
    }

    /// <summary>A walk from (ref, name) pairs; refs are just the index-independent ids the test picks.</summary>
    private static UiaWalk Walk(ViewKey view, params (ulong Ref, string Name)[] lines)
    {
        var strings = lines.Select(l => l.Name).ToList();
        return new UiaWalk(view, [.. lines.Select((l, i) => new SnapLine(l.Ref, 1, 2, i, -1))], strings);
    }

    private static (ulong, string)[] Lines(int count, string prefix = "item") =>
        [.. Enumerable.Range(1, count).Select(i => ((ulong)i, prefix + " " + i))];

    [Fact]
    public void First_sight_is_a_baseline_then_only_changes_and_nothing_when_unchanged()
    {
        var differ = new SnapshotDiffer();
        var first = differ.Diff(Walk(Mail, Lines(10)), T0)!;
        Assert.True(first.Baseline);
        Assert.Equal(10, first.Added.Count);
        Assert.Equal(1, first.Seq);

        Assert.Null(differ.Diff(Walk(Mail, Lines(10)), T0.AddSeconds(1)));

        var moved = Lines(10).ToList();
        moved.Insert(3, (99UL, "new message"));
        moved.RemoveAt(10); // item 10 scrolled out
        var d = differ.Diff(Walk(Mail, [.. moved]), T0.AddSeconds(2))!;
        Assert.False(d.Baseline);
        Assert.Equal("new message", Assert.Single(d.Added).Name);
        Assert.Equal([10UL], d.Removed);
        Assert.Equal((2L, 1L), (d.Seq, d.Base));
    }

    [Fact]
    public void A_change_over_60_percent_or_the_50th_diff_re_baselines()
    {
        var differ = new SnapshotDiffer();
        differ.Diff(Walk(Mail, Lines(10)), T0);
        var swapped = differ.Diff(Walk(Mail, Lines(10, "other")), T0.AddSeconds(1))!; // a different message opened
        Assert.True(swapped.Baseline);
        Assert.Empty(swapped.Removed);

        var view = new ViewKey(0x20, 1);
        differ.Diff(Walk(view, Lines(20)), T0);
        // At most 50 diffs chain off one baseline, so a vault reader never replays more than 50: the 51st record is a baseline.
        for (var i = 1; i <= SnapshotDiffer.RebaselineEvery + 1; i++)
        {
            var lines = Lines(20).ToList();
            lines[0] = (1000UL + (ulong)i, "unread " + i); // one small change each time
            var d = differ.Diff(Walk(view, [.. lines]), T0.AddMinutes(i))!;
            Assert.Equal(i == SnapshotDiffer.RebaselineEvery + 1, d.Baseline);
        }
    }

    [Fact]
    public void A_ticking_clock_turns_volatile_and_only_its_last_value_is_emitted_at_activity_close()
    {
        var differ = new SnapshotDiffer();
        (ulong, string)[] With(string clock) => [.. Lines(9), (50UL, clock)];
        differ.Diff(Walk(Mail, With("09:00:00")), T0);
        var emitted = 0;
        for (var s = 1; s <= 30; s++)
        {
            if (differ.Diff(Walk(Mail, With($"09:00:{s:00}")), T0.AddSeconds(s)) is not null)
            {
                emitted++;
            }
        }

        Assert.Equal(SnapshotDiffer.VolatileChanges - 1, emitted); // the 10th change in 60 s marks it volatile

        var close = Assert.Single(differ.CloseActivity(T0.AddSeconds(31)));
        Assert.Equal("09:00:30", Assert.Single(close.Added).Name);
        Assert.Contains(50UL, close.Removed); // replaces the last value the vault saw

        Assert.NotNull(differ.Diff(Walk(Mail, With("09:00:31")), T0.AddSeconds(32))); // a new activity sees it again
        Assert.Empty(differ.CloseActivity(T0.AddSeconds(33)));
    }

    [Fact]
    public void At_most_64_views_live_and_an_evicted_view_re_baselines()
    {
        var differ = new SnapshotDiffer();
        for (var w = 0; w <= SnapshotDiffer.MaxViews; w++)
        {
            differ.Diff(Walk(new ViewKey(w, 1), Lines(3)), T0);
        }

        Assert.Equal(SnapshotDiffer.MaxViews, differ.LiveViews);
        Assert.True(differ.Diff(Walk(new ViewKey(0, 1), Lines(3)), T0)!.Baseline); // view 0 was the least recent
        Assert.Null(differ.Diff(Walk(new ViewKey(SnapshotDiffer.MaxViews, 1), Lines(3)), T0));

        differ.Forget(new ViewKey(0, 1));
        Assert.Equal(SnapshotDiffer.MaxViews - 1, differ.LiveViews);
    }

    [Fact]
    public void Myers_finds_the_minimal_edit_and_gives_up_past_the_cap()
    {
        int[] a = [1, 2, 3, 4, 5, 6], b = [1, 3, 4, 7, 5, 6];
        Assert.True(Myers.Diff(a, b, 10, out var ka, out var kb));
        Assert.Equal([true, false, true, true, true, true], ka); // 2 removed
        Assert.Equal([true, true, true, false, true, true], kb); // 7 added
        Assert.False(Myers.Diff(a, b, 1, out _, out _));
        Assert.True(Myers.Diff(Array.Empty<int>(), Array.Empty<int>(), 0, out _, out _));
    }

    [Fact]
    public void Field_truth_stores_the_value_diff_not_the_keys()
    {
        var f = new FieldTruth();
        Assert.True(f.Hold("Hello world"));
        Assert.Equal(new FieldEdit(", dear", 0), f.Diff("Hello, dear world")); // autocomplete / paste / IME all look the same
        Assert.Equal(new FieldEdit("", 5), f.Diff("Hello, world")); // "dear " deleted
        Assert.Null(f.Diff("Hello, world"));
        Assert.Equal(new FieldEdit("Bye", 5), f.Diff("Bye, world"));

        Assert.False(f.Hold(new string('x', FieldTruth.MaxChars + 1))); // too long: keys are the fallback
        Assert.False(f.Holding);
        Assert.Null(f.Diff("anything"));
    }
}
