using Tasktor.Capture.Deep;
using Tasktor.Capture.Index;
using Tasktor.Core.Storage;

namespace Tasktor.Capture.Tests;

/// <summary>C19.3: an activity's pointer features stay in the index after the raw path's 7 days, and forget removes them.</summary>
public sealed class PointerIndexTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 7, 9, 0, 0, TimeSpan.Zero);
    private static readonly PointerFeatures F = new(900, 100, 300, 2, 1, 0.75);
    private readonly ContentIndex _index = ContentIndex.Open(SqliteDb.Open(":memory:"));

    public void Dispose() => _index.Dispose();

    private static ActivityDocument Doc(int minutes, string app, PointerFeatures? pointer, params DocSection[] sections) =>
        new(1, T0.AddMinutes(minutes), T0.AddMinutes(minutes + 10), sections) { Apps = [app], Pointer = pointer };

    [Fact]
    public void Features_are_kept_even_for_an_activity_without_text()
    {
        _index.Add(Doc(0, "synthetic.exe", F));
        _index.Add(Doc(30, "synthetic.exe", null, new DocSection("typed", "notes")));
        Assert.Equal(F, _index.Pointer(T0, 1));
        Assert.Null(_index.Pointer(T0.AddMinutes(30), 1));
        Assert.Equal(1, _index.PointerCount);
    }

    [Fact]
    public void Forget_by_time_and_by_app_removes_features()
    {
        _index.Add(Doc(0, "synthetic.exe", F));
        _index.Add(Doc(30, "other.exe", F));
        _index.Add(Doc(60, "kept.exe", F));
        _index.Forget(T0.AddMinutes(20), T0.AddMinutes(40));
        Assert.Equal(1, _index.Forget("synthetic.exe", null));
        Assert.Null(_index.Pointer(T0, 1));
        Assert.Null(_index.Pointer(T0.AddMinutes(30), 1));
        Assert.Equal(F, _index.Pointer(T0.AddMinutes(60), 1));
    }
}
