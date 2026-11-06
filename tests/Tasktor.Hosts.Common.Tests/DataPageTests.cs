using Tasktor.Contracts.Export;
using Tasktor.Hosts.Common.Deep;
using Tasktor.Hosts.Common.Render;

namespace Tasktor.Hosts.Common.Tests;

/// <summary>U4 D-G3 "Your data": the data pull as rows with a two-step forget per group.</summary>
public sealed class DataPageTests
{
    private static readonly DataGroup Tracking = new()
    {
        Scope = "tracking",
        Name = "Activity tracking",
        Kept = "events 30 days",
        Bytes = 5 * 1024 * 1024 + 300 * 1024,
        Files = 2,
        Oldest = new DateTimeOffset(2026, 10, 8, 23, 30, 0, TimeSpan.Zero),
        Newest = new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public void A_group_lists_size_files_dates_and_retention_and_offers_its_forget()
    {
        var s = DataPage.Section(Tracking, TimeZoneInfo.Utc);
        Assert.Equal("Activity tracking", s.Title);
        Assert.Equal(
            [("Size", "5.3 MB"), ("Files", "2"), ("Oldest", "8 Oct 2026"), ("Newest", "6 Nov 2026"), ("Kept", "events 30 days")],
            s.Rows.Select(r => (r.Label, r.Value!)));
        var forget = Assert.Single(s.Actions);
        Assert.Equal(("forget", "tracking", true), (forget.Action, forget.Target!["what"], forget.Destructive == true));
        Assert.EndsWith("…", forget.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_group_says_so_and_offers_nothing()
    {
        var s = DataPage.Section(new DataGroup { Scope = "capture", Name = "Captured", Kept = "7 days" }, TimeZoneInfo.Utc);
        Assert.Equal("Nothing kept.", s.Rows[0].Label);
        Assert.Empty(s.Actions);
    }

    [Fact]
    public void The_rest_is_forget_everything()
    {
        var s = DataPage.Section(Tracking with { Scope = "all" }, TimeZoneInfo.Utc);
        Assert.Equal("Forget everything…", s.Actions[0].Label);
    }

    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(1023, "1023 bytes")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(250L * 1024 * 1024, "250 MB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3 GB")]
    public void Bytes_read_like_Explorer(long bytes, string expected) => Assert.Equal(expected, DataPage.Bytes(bytes));

    [Fact]
    public void Days_are_in_the_viewers_zone()
    {
        var plus = TimeZoneInfo.CreateCustomTimeZone("plus2", TimeSpan.FromHours(2), "plus2", "plus2");
        Assert.Equal("9 Oct 2026", DataPage.Section(Tracking, plus).Rows.Single(r => r.Label == "Oldest").Value);
    }
}
