using Tasktor.Windows.Sources;

namespace Tasktor.Windows.Tests;

public sealed class FormattingTests
{
    [Theory]
    [InlineData(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, "::")]
    [InlineData(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 }, "::1")]
    [InlineData(new byte[] { 0xfe, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 }, "fe80::1")]
    [InlineData(new byte[] { 0x20, 0x01, 0x0d, 0xb8, 0, 1, 0, 2, 0, 3, 0, 4, 0, 5, 0, 6 }, "2001:db8:1:2:3:4:5:6")]
    public void Ipv6_addresses_format_for_display(byte[] bytes, string text) => Assert.Equal(text, ListenerSource.Ipv6(bytes));

    [Fact]
    public void Off_windows_the_win32_sources_return_nothing_instead_of_throwing()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var rows = new List<KeyValuePair<Contracts.Platform.ProcKey, Contracts.Platform.ProcessRow>>();
        new Win32ProcessTable().Read(rows, Contracts.Platform.ColumnSet.All);
        Assert.Empty(rows);
        var frame = new Contracts.Platform.MetricFrame();
        new PdhMetricSource().Read(frame);
        Assert.Equal(0, frame.Count);
        new PowerSource().Read(frame);
        Assert.True(frame.TryGet("battery.flag", out var flag) && flag == 128);
    }
}
