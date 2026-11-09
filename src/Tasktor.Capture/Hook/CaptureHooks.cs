namespace Tasktor.Capture.Hook;

/// <summary>
/// Which low-level hooks the capture host attaches. Each device attaches only after its real-PC check passed: the mouse
/// after <c>--bench-hook</c> (C19 acceptance 3, passed 2026-11-09), the keyboard after <c>scripts/deadkey-check.ps1</c>
/// (layout decoding, C17), which has not run yet.
/// </summary>
public static class CaptureHooks
{
    public const HookDevices Attached = HookDevices.Mouse;
}
