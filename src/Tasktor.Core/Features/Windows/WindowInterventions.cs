using Tasktor.Contracts.Workflow;

namespace Tasktor.Core.Features.Windows;

/// <summary>
/// U3 D-W5 "don't fight the user": which windows the user moved, sized or closed, from <c>WinEventSource</c>'s
/// lifecycle events (numbers only: hwnd and a monotonic time). Thread-safe: the source thread writes, the restore's
/// off-loop step reads.
/// </summary>
public sealed class WindowInterventions
{
    private const int PruneAbove = 512;
    private static readonly long KeepMs = (long)TimeSpan.FromMinutes(1).TotalMilliseconds;

    private readonly Lock _gate = new();
    private readonly Dictionary<long, long> _changed = [];
    private readonly HashSet<long> _moving = [];

    /// <summary>Feed one event; kinds other than move / size / destroy are ignored.</summary>
    public void On(WindowEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var hwnd = (long)e.Hwnd;
        lock (_gate)
        {
            switch (e.Kind)
            {
                case WindowEventKind.MoveSizeStart:
                    _moving.Add(hwnd);
                    break;
                case WindowEventKind.MoveSizeEnd:
                case WindowEventKind.Destroyed:
                    _moving.Remove(hwnd);
                    break;
                default:
                    return;
            }

            _changed[hwnd] = e.AtMono;
            if (_changed.Count > PruneAbove)
            {
                var cutoff = e.AtMono - KeepMs;
                foreach (var key in _changed.Where(p => p.Value < cutoff).Select(p => p.Key).ToArray())
                {
                    _changed.Remove(key);
                }
            }
        }
    }

    /// <summary>True when the user is moving <paramref name="hwnd"/> now, or moved / sized / closed it at or after <paramref name="since"/>.</summary>
    public bool ChangedSince(long hwnd, long since)
    {
        lock (_gate)
        {
            return _moving.Contains(hwnd) || (_changed.TryGetValue(hwnd, out var at) && at >= since);
        }
    }
}
