using System.Runtime.InteropServices;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Workflow;

namespace Tasktor.Windows.Sources;

/// <summary>
/// C1.2 <c>IdleSource</c>: <c>GetLastInputInfo</c> (a session-wide time, never which key or where), polled every 60 s
/// while active and every 5 s while idle. Idle starts once no input for <see cref="EpisodeThresholds.IdleAfterMs"/>,
/// backdated to the last input; it ends when input returns, also at the last input's time. Tick arithmetic is unsigned
/// 32-bit, so the 49.7-day wrap is harmless. Media idle (watching) needs the audio-session probe and is reported as input
/// idle until then.
/// </summary>
public sealed partial class IdleSource : IEventSource<PresenceEvent>
{
    public static readonly TimeSpan Active = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan WhileIdle = TimeSpan.FromSeconds(5);

    private readonly long _idleAfterMs;
    private readonly Lock _gate = new();
    private Timer? _timer;
    private Action<PresenceEvent>? _post;
    private bool _idle;

    public IdleSource(long? idleAfterMs = null) => _idleAfterMs = idleAfterMs ?? new EpisodeThresholds().IdleAfterMs;

    public string Name => "idle";

    public void Start(Action<PresenceEvent> post)
    {
        ArgumentNullException.ThrowIfNull(post);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _post = post;
        _timer = new Timer(_ => Sample(), null, TimeSpan.Zero, Active);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        _post = null;
    }

    /// <summary>Milliseconds since the last input, with unsigned 32-bit tick arithmetic (wrap-safe).</summary>
    public static uint IdleMs(uint nowTick, uint lastInputTick) => unchecked(nowTick - lastInputTick);

    private void Sample()
    {
        var info = new LastInputInfo { CbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (GetLastInputInfo(ref info) == 0)
        {
            return;
        }

        var idleMs = IdleMs(GetTickCount(), info.DwTime);
        var at = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(idleMs);
        var mono = Environment.TickCount64 - idleMs;
        lock (_gate)
        {
            if (!_idle && idleMs >= _idleAfterMs)
            {
                _idle = true;
                _post?.Invoke(new PresenceEvent(PresenceKind.IdleStart, at, mono, IdleKind.Input));
                _timer?.Change(WhileIdle, WhileIdle);
            }
            else if (_idle && idleMs < _idleAfterMs)
            {
                _idle = false;
                _post?.Invoke(new PresenceEvent(PresenceKind.IdleEnd, at, mono));
                _timer?.Change(Active, Active);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint CbSize;
        public uint DwTime;
    }

    [LibraryImport("user32.dll")]
    private static partial int GetLastInputInfo(ref LastInputInfo info);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetTickCount();
}
