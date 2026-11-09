using Tasktor.Capture.Hook;

namespace Tasktor.Capture.Tests;

public sealed class CaptureHooksTests
{
    [Fact]
    public void Only_the_mouse_hook_is_attached_until_the_dead_key_check_passes()
    {
        // C19 acceptance 3 passed on a real PC (2026-11-09): the mouse hook attaches. The keyboard hook waits for
        // scripts/deadkey-check.ps1; flip CaptureHooks.Attached and this test together once it passes.
        Assert.True(CaptureHooks.Attached.HasFlag(HookDevices.Mouse));
        Assert.False(CaptureHooks.Attached.HasFlag(HookDevices.Keyboard));
    }
}
