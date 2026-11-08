using Tasktor.Contracts.Platform;
using Tasktor.Core.Logging;
using Tasktor.Core.Time;
using Tasktor.Tests.Shared;

namespace Tasktor.Core.Tests;

public sealed class LoggingTests
{
    [Fact]
    public void Sensitive_values_never_reach_the_log()
    {
        var w = new MemoryLogWriter();
        var log = new LogSink(new FakeClock(), w);
        var title = Sensitive.Of("Secret project plan.docx - Word");
        log.Info("window.focus", LogField.Of("exe", "winword.exe"), LogField.Of("title", title));
        var line = Assert.Single(w.Lines);
        Assert.Contains("exe=winword.exe", line, StringComparison.Ordinal);
        Assert.Contains("title=<redacted>", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret", line, StringComparison.Ordinal);
        Assert.Equal("<redacted>", $"{title}");
    }

    [Fact]
    public void Verbose_reverts_by_itself_after_24_hours()
    {
        var clock = new FakeClock();
        var w = new MemoryLogWriter();
        var log = new LogSink(clock, w);
        log.Verbose("hidden");
        log.SetVerbose(true);
        log.Verbose("shown");
        clock.Advance(TimeSpan.FromHours(24));
        log.Verbose("hidden-again");
        Assert.Single(w.Lines);
        Assert.False(log.VerboseOn);
    }

    [Fact]
    public void Values_with_spaces_are_quoted()
    {
        var w = new MemoryLogWriter();
        new LogSink(new FakeClock(), w).Warn("x", LogField.Of("msg", "a b\"c"), LogField.Of("n", 3), LogField.Of("ok", true));
        Assert.EndsWith("WARN x msg=\"a b\\\"c\" n=3 ok=true", w.Lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void File_writer_keeps_seven_days()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tasktor-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            var w = new FileLogWriter(dir);
            var day = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
            for (var i = 0; i < 10; i++)
            {
                w.Write(day.AddDays(i), "line " + i);
            }

            Assert.Equal(7, Directory.GetFiles(dir, "tasktor-*.log").Length);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("30s", 30)]
    [InlineData("5m", 300)]
    [InlineData("2h", 7200)]
    [InlineData("1d", 86400)]
    [InlineData("1h30m", 5400)]
    [InlineData("500ms", 0.5)]
    public void Durations_parse(string text, double seconds)
    {
        Assert.True(DurationText.TryParse(text, out var v));
        Assert.Equal(seconds, v.TotalSeconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("5")]
    [InlineData("5x")]
    [InlineData("m")]
    public void Bad_durations_fail(string text) => Assert.False(DurationText.TryParse(text, out _));

    [Fact]
    public void Humanize_is_short() =>
        Assert.Equal(["45 s", "12 min", "1 h 10 m", "3 d"], new[] { 45, 720, 4200, 3 * 86400 }.Select(s => DurationText.Humanize(TimeSpan.FromSeconds(s))));
}
