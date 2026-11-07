using Tasktor.Capture.Deep;
using Tasktor.Contracts.Platform;

namespace Tasktor.Capture.Media;

/// <summary>
/// C20 state and audible window. Call on the capture logic thread after the OS source posts events; it performs no
/// polling, UIA, network, logging or background work. The post delegate is the existing event-channel boundary.
/// </summary>
public sealed class MediaSessionLogic(Action<Sensitive<MediaState>> post)
{
    public const float AudibleThreshold = 0.01f;
    public const int WindowSeconds = 5;
    public const int AudibleSeconds = 3;

    private readonly Dictionary<string, Entry> _apps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Applies SMTC metadata after AUMID resolution. Unknown status and invalid positions are ignored, never converted to
    /// stopped. The existing MediaState contract requires strings, so the complete record remains wrapped and may be revealed only at the protected recording boundary.
    /// </summary>
    public bool Metadata(string app, Sensitive<string>? title, Sensitive<string>? artist, MediaStatus status, long positionMs)
    {
        if (string.IsNullOrWhiteSpace(app) || positionMs < 0 || status is not (MediaStatus.Playing or MediaStatus.Paused or MediaStatus.Stopped))
            return false;

        var entry = For(app);
        var changed = !entry.HasMetadata || !Nullable.Equals(entry.Title, title) || !Nullable.Equals(entry.Artist, artist)
            || entry.Status != status || entry.PositionMs != positionMs;
        entry.HasMetadata = true;
        entry.Title = title;
        entry.Artist = artist;
        entry.Status = status;
        entry.PositionMs = positionMs;
        if (changed) Emit(entry);
        return true;
    }

    /// <summary>
    /// One aggregate peak per app and second (the largest peak across its active audio sessions). Same-second updates
    /// replace the sample. Invalid peaks and samples older than the latest observed clock time are ignored.
    /// </summary>
    public bool Peak(string app, DateTimeOffset at, float peak)
    {
        if (string.IsNullOrWhiteSpace(app) || !float.IsFinite(peak) || peak < 0 || peak > 1) return false;
        var entry = For(app);
        var second = at.ToUnixTimeSeconds();
        if (second < entry.LatestSecond) return false;
        entry.LatestSecond = second;
        var index = (int)((second % WindowSeconds + WindowSeconds) % WindowSeconds);
        entry.Seconds[index] = second;
        entry.High[index] = peak > AudibleThreshold;
        UpdateAudible(entry, second);
        return true;
    }

    /// <summary>Age the windows at the host's existing 1 Hz tick; emits only transitions.</summary>
    public void Tick(DateTimeOffset at)
    {
        var second = at.ToUnixTimeSeconds();
        foreach (var entry in _apps.Values)
        {
            if (second < entry.LatestSecond) continue;
            entry.LatestSecond = second;
            UpdateAudible(entry, second);
        }
    }

    /// <summary>All audio sessions for an app ended: clear stale levels without inventing a playback status.</summary>
    public bool AudioStopped(string app)
    {
        if (string.IsNullOrWhiteSpace(app) || !_apps.TryGetValue(app, out var entry)) return false;
        Array.Fill(entry.Seconds, long.MinValue);
        Array.Clear(entry.High);
        if (entry.Audible)
        {
            entry.Audible = false;
            if (entry.HasMetadata) Emit(entry);
        }
        return true;
    }

    /// <summary>Remove a departed source and clear its audible qualification if previously emitted.</summary>
    public bool Remove(string app)
    {
        if (string.IsNullOrWhiteSpace(app) || !_apps.TryGetValue(app, out var entry)) return false;
        AudioStopped(app);
        return _apps.Remove(app);
    }

    private Entry For(string app)
    {
        if (!_apps.TryGetValue(app, out var entry))
        {
            entry = new Entry(app);
            _apps.Add(app, entry);
        }
        return entry;
    }

    private void UpdateAudible(Entry entry, long second)
    {
        var count = 0;
        for (var i = 0; i < WindowSeconds; i++)
        {
            if (entry.High[i] && entry.Seconds[i] >= second - (WindowSeconds - 1) && entry.Seconds[i] <= second) count++;
        }
        var audible = count >= AudibleSeconds;
        if (audible == entry.Audible) return;
        entry.Audible = audible;
        if (entry.HasMetadata) Emit(entry);
    }

    private void Emit(Entry entry) => post(Sensitive.Of(new MediaState(entry.App, entry.Title?.Reveal(), entry.Artist?.Reveal(), entry.Status, entry.PositionMs, entry.Audible)));

    private sealed class Entry(string app)
    {
        public string App { get; } = app;
        public bool HasMetadata { get; set; }
        public Sensitive<string>? Title { get; set; }
        public Sensitive<string>? Artist { get; set; }
        public MediaStatus Status { get; set; }
        public long PositionMs { get; set; }
        public bool Audible { get; set; }
        public long LatestSecond { get; set; } = long.MinValue;
        public long[] Seconds { get; } = [long.MinValue, long.MinValue, long.MinValue, long.MinValue, long.MinValue];
        public bool[] High { get; } = new bool[WindowSeconds];
    }
}

