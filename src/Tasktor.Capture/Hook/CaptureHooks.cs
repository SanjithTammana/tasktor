namespace Tasktor.Capture.Hook;

/// <summary>
/// Which low-level hooks the capture host attaches. Each device attached only after its real-PC check passed (both on
/// 2026-11-09): the mouse after <c>--bench-hook</c> (C19 acceptance 3), the keyboard after
/// <c>scripts/deadkey-check.ps1</c> (layout decoding, C17).
/// </summary>
public static class CaptureHooks
{
    public const HookDevices Attached = HookDevices.Both;
}
