using Tasktor.Core.Storage;
using Tasktor.Tests.Shared;

namespace Tasktor.Core.Tests;

/// <summary>U1 D-H8 / U4 D-G3 "Your data": sizes and ages in the groups the forget scopes delete.</summary>
public sealed class DataInventoryTests : IDisposable
{
    private readonly TempStore _store = new();

    public void Dispose() => _store.Dispose();

    private void Write(string path, int bytes, DateTime? at = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        if (at is { } t)
        {
            File.SetLastWriteTimeUtc(path, t);
        }
    }

    [Fact]
    public void Groups_follow_the_forget_scopes_with_sizes_counts_and_ages()
    {
        var l = _store.Layout;
        Write(l.JobKinds, 100, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));
        Write(l.EventsDb, 4000, new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc));
        Write(l.EpisodesDb, 1000, new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc));
        Write(Path.Combine(l.CaptureDir, "raw", "20261105.bin"), 300);
        Write(Path.Combine(l.LogDir, "tasktor-20261106.log"), 50);
        Write(Path.Combine(l.NotesDir, "note.md"), 999); // the user's own content: not listed
        Write(l.Presets, 999);

        var report = DataInventory.Scan(l, new Forgetter(l));
        Assert.Equal(["learned", "tracking", "capture", "all"], report.Groups.Select(g => g.Scope));
        var tracking = report.Groups.Single(g => g.Scope == "tracking");
        Assert.Equal((5000L, 2), (tracking.Bytes, tracking.Files));
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero), tracking.Oldest);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero), tracking.Newest);
        Assert.Equal((100L, 1), (report.Groups[0].Bytes, report.Groups[0].Files));
        Assert.Equal(300L, report.Groups[2].Bytes);
        Assert.Equal(50L, report.Groups[3].Bytes);
        Assert.Equal(5450L, report.Groups.Sum(g => g.Bytes)); // notes and presets are not counted
        Assert.Contains("7 days", report.Groups[3].Kept, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_store_reports_zero_and_no_dates()
    {
        var report = DataInventory.Scan(_store.Layout, new Forgetter(_store.Layout));
        Assert.All(report.Groups, g => Assert.Equal((0L, 0, (DateTimeOffset?)null), (g.Bytes, g.Files, g.Oldest)));
    }

    [Fact]
    public void Links_inside_the_store_are_not_followed()
    {
        var l = _store.Layout;
        Write(l.EventsDb, 10);
        var outside = Path.Combine(_store.Root, "..", "tasktor-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllBytes(Path.Combine(outside, "big.bin"), new byte[100_000]);
            Links.Directory(Path.Combine(l.TrackDir, "elsewhere"), outside); // a symlink, or a junction without the privilege

            Assert.Equal(10L, DataInventory.Scan(l, new Forgetter(l)).Groups.Single(g => g.Scope == "tracking").Bytes);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }
}
