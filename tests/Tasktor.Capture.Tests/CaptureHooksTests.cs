using Tasktor.Capture.Hook;

namespace Tasktor.Capture.Tests;

public sealed class CaptureHooksTests
{
    [Fact]
    public void Both_hooks_are_attached_once_their_real_pc_checks_passed()
    {
        // 2026-11-09 on a real PC: --bench-hook passed (C19 acceptance 3), then scripts/deadkey-check.ps1 passed with
        // US-International (é and ü composed, dead keys decoded as uncertain and suppressed).
        Assert.Equal(HookDevices.Both, CaptureHooks.Attached);
    }
}
