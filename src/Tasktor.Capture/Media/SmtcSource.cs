using System.Runtime.Versioning;
using Tasktor.Capture.Deep;
using Tasktor.Contracts.Platform;
using Windows.Media.Control;

namespace Tasktor.Capture.Media;

/// <summary>
/// C20.1 / §5.6 now-playing: SMTC (<c>GlobalSystemMediaTransportControlsSessionManager</c>, the CsWinRT projection)
/// sessions, metadata and playback status, on WinRT's event threads; each change is read and posted (titles and artists
/// <see cref="Sensitive{T}"/>). A status SMTC does not state as playing, paused or stopped is not posted. A departed
/// source is posted as removed. Never controls playback.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class SmtcSource(Action<string, Sensitive<string>?, Sensitive<string>?, MediaStatus, long> metadata, Action<string> removed) : IDisposable
{
    private readonly object _gate = new();
    private readonly SmtcSessionSet _set = new();
    private readonly Dictionary<string, GlobalSystemMediaTransportControlsSession> _sessions = new(StringComparer.Ordinal);
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private bool _disposed;

    public async Task<bool> StartAsync()
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            lock (_gate)
            {
                if (_disposed) return false;
                _manager = manager;
                manager.SessionsChanged += OnSessionsChanged;
            }

            Sync();
            return true;
        }
        catch (Exception)
        {
            return false; // no media service: no now-playing (audible still works)
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            if (_manager is { } m) m.SessionsChanged -= OnSessionsChanged;
            foreach (var s in _sessions.Values) Unsubscribe(s);
            _sessions.Clear();
            _manager = null;
        }
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args) => Sync();

    private void Sync()
    {
        List<GlobalSystemMediaTransportControlsSession> fresh = [];
        IReadOnlyList<string> gone;
        lock (_gate)
        {
            if (_disposed || _manager is not { } manager) return;
            var current = manager.GetSessions();
            var ids = new HashSet<string>(current.Select(s => s.SourceAppUserModelId ?? string.Empty), StringComparer.Ordinal);
            foreach (var (id, s) in _sessions.Where(p => !ids.Contains(p.Key)).ToList())
            {
                Unsubscribe(s);
                _sessions.Remove(id);
            }

            foreach (var s in current)
            {
                var id = s.SourceAppUserModelId ?? string.Empty;
                if (id.Length == 0 || _sessions.ContainsKey(id)) continue;
                _sessions[id] = s;
                s.MediaPropertiesChanged += OnProperties;
                s.PlaybackInfoChanged += OnPlayback;
                s.TimelinePropertiesChanged += OnTimeline;
                fresh.Add(s);
            }

            gone = _set.Sync(_sessions.Keys).Removed;
        }

        foreach (var app in gone) removed(app);
        foreach (var s in fresh) _ = ReadAsync(s);
    }

    private void Unsubscribe(GlobalSystemMediaTransportControlsSession s)
    {
        s.MediaPropertiesChanged -= OnProperties;
        s.PlaybackInfoChanged -= OnPlayback;
        s.TimelinePropertiesChanged -= OnTimeline;
    }

    private void OnProperties(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => _ = ReadAsync(s);

    private void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => _ = ReadAsync(s);

    private void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) => _ = ReadAsync(s);

    private async Task ReadAsync(GlobalSystemMediaTransportControlsSession s)
    {
        try
        {
            if (MediaAppMap.FromAumid(s.SourceAppUserModelId) is not { } app) return;
            if (MediaAppMap.Status((int)s.GetPlaybackInfo().PlaybackStatus) is not { } status) return; // never a guessed status
            var props = await s.TryGetMediaPropertiesAsync();
            var position = (long)Math.Max(0, s.GetTimelineProperties().Position.TotalMilliseconds);
            lock (_gate)
            {
                if (_disposed) return;
            }

            metadata(app, Of(props?.Title), Of(props?.Artist), status, position);
        }
        catch (Exception)
        {
            // the session ended mid-read; never logged (titles are personal)
        }
    }

    private static Sensitive<string>? Of(string? s) => string.IsNullOrEmpty(s) ? null : Sensitive.Of(s);
}
