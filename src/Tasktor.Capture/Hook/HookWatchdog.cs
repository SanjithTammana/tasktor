namespace Tasktor.Capture.Hook;

/// <summary>Detects 30 seconds without hook events while the system reports new input, using wrap-safe ticks.</summary>
public sealed class HookWatchdog
{
    public const uint SilenceMs = 30_000;
    private uint _lastHookTick;
    private uint _lastInputTick;
    private long _published;
    private bool _inputSinceHook;

    public HookWatchdog(uint now, uint lastInputTick, long published)
    {
        _lastHookTick = now;
        _lastInputTick = lastInputTick;
        _published = published;
    }

    /// <summary>Run on the hook message-loop thread; true requests one immediate reinstall.</summary>
    public bool ShouldReinstall(uint now, uint lastInputTick, long published)
    {
        if (published != _published)
        {
            _published = published;
            _lastHookTick = now;
            _lastInputTick = lastInputTick;
            _inputSinceHook = false;
            return false;
        }

        if (lastInputTick != _lastInputTick)
        {
            _lastInputTick = lastInputTick;
            _inputSinceHook = true;
        }

        if (!_inputSinceHook || unchecked(now - _lastHookTick) < SilenceMs) return false;
        _lastHookTick = now;
        _inputSinceHook = false;
        return true;
    }
}
