using Tasktor.Capture.Deep;

namespace Tasktor.Capture.Tests;

public sealed class SecureDesktopDefaultTests
{
    [Fact]
    public void Missing_desktop_signal_suppresses_even_a_known_non_password_field()
    {
        var guard = new Suppressor(new CaptureBlocks(local: TimeZoneInfo.Utc));
        var field = new FocusTarget("view", "field", "example.exe") { IsPassword = false };
        Assert.Equal(Suppression.SecureDesktop, guard.Check(field, DateTimeOffset.UnixEpoch));
    }
}
