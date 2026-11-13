using Tasktor.Hosts.Common.Command;

namespace Tasktor.Hosts.Common.Tests;

public class TargetMonitorTests
{
    [Theory]
    [InlineData("Chrome_WidgetWin_1")] // an ordinary app window
    [InlineData("Shell_TrayWnd")] // the main taskbar: a tray click opens on its monitor
    [InlineData("Shell_SecondaryTrayWnd")] // a second monitor's taskbar
    [InlineData("WinUIDesktopWin32WindowClass")] // the deep window, when Find opens the command line
    public void A_visible_working_window_picks_its_monitor(string className) =>
        Assert.Equal(MonitorSource.Foreground, TargetMonitor.Choose(true, className, visible: true, minimized: false));

    [Theory]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    public void The_desktop_spans_every_monitor_so_the_pointer_decides(string className) =>
        Assert.Equal(MonitorSource.Cursor, TargetMonitor.Choose(true, className, visible: true, minimized: false));

    [Fact]
    public void No_foreground_window_uses_the_pointer() =>
        Assert.Equal(MonitorSource.Cursor, TargetMonitor.Choose(false, null, visible: false, minimized: false));

    [Fact]
    public void A_hidden_foreground_window_uses_the_pointer() =>
        Assert.Equal(MonitorSource.Cursor, TargetMonitor.Choose(true, "HiddenMessageWindow", visible: false, minimized: false));

    [Fact]
    public void A_minimized_foreground_window_uses_the_pointer() =>
        Assert.Equal(MonitorSource.Cursor, TargetMonitor.Choose(true, "Notepad", visible: true, minimized: true));
}
