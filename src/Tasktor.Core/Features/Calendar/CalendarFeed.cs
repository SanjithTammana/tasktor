using Tasktor.Contracts.Platform;
using Tasktor.Core.Calendar;
using Tasktor.Core.Storage;

namespace Tasktor.Core.Features.Calendar;

/// <summary>
/// The one in-memory calendar table shared by E1, E2, I3 and I13 (U2: each source designed once). Reads off the loop on
/// file change (debounced 1 s) and at local midnight; nothing ticks between. Held while any consumer is enabled. Loop-only.
/// </summary>
public sealed class CalendarFeed
{
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1);

    private readonly FeatureContext _ctx;
    private readonly CalendarSource _source;
    private int _holders;
    private FileWatch? _watch;
    private IDisposable? _midnight;
    private IDisposable? _pending;

    public CalendarFeed(FeatureContext context, CalendarSource source, TimeZoneInfo? local = null)
    {
        _ctx = context;
        _source = source;
        Local = local ?? TimeZoneInfo.Local;
    }

    public TimeZoneInfo Local { get; }

    public IReadOnlyList<CalEvent> Rows { get; private set; } = [];

    public IReadOnlyList<CalendarFeedStatus> Feeds { get; private set; } = [];

    public long Reads { get; private set; }

    /// <summary>Raised on the loop after each read.</summary>
    public event Action? Updated;

    public void Acquire()
    {
        if (_holders++ > 0)
        {
            return;
        }

        Rewatch();
        Refresh();
    }

    public void Release()
    {
        if (_holders == 0 || --_holders > 0)
        {
            return;
        }

        _watch?.Dispose();
        _watch = null;
        _midnight?.Dispose();
        _midnight = null;
        _pending?.Dispose();
        _pending = null;
        Rows = [];
    }

    /// <summary>The settings changed (files added or removed): watch the new set and read again.</summary>
    public void Rewatch()
    {
        _watch?.Dispose();
        _watch = new FileWatch([.. _source.Inputs, _ctx.Store.PluginData(CalendarSource.PluginId) + Path.DirectorySeparatorChar + "events.json"], () => _ctx.Loop.Post(Changed));
    }

    private void Changed()
    {
        if (_holders == 0 || _pending is not null)
        {
            return;
        }

        _pending = _ctx.Loop.At(_ctx.Clock.UtcNow + Debounce, () =>
        {
            _pending = null;
            Refresh();
        });
    }

    public void Refresh()
    {
        if (_holders == 0)
        {
            return;
        }

        _ctx.OffLoop(() =>
        {
            var rows = new List<KeyValuePair<EventKey, CalEvent>>();
            _source.Read(rows, ColumnSet.None);
            var feeds = _source.Feeds;
            _ctx.Loop.Post(() => Apply([.. rows.Select(r => r.Value)], feeds));
        });
    }

    /// <summary>Apply one read (tests call it directly).</summary>
    public void Apply(IReadOnlyList<CalEvent> rows, IReadOnlyList<CalendarFeedStatus> feeds)
    {
        if (_holders == 0)
        {
            return; // a late read after the last consumer left
        }

        Rows = rows;
        Feeds = feeds;
        Reads++;
        _midnight?.Dispose();
        var now = _ctx.Clock.UtcNow;
        var localNow = TimeZoneInfo.ConvertTime(now, Local);
        var nextMidnight = new DateTimeOffset(localNow.Date.AddDays(1), Local.GetUtcOffset(localNow.Date.AddDays(1)));
        _midnight = _ctx.Loop.At(nextMidnight, Refresh);
        Updated?.Invoke();
    }

    /// <summary>Local wall time "HH:mm" for display.</summary>
    public string Clock(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, Local).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    public bool SameLocalDay(DateTimeOffset a, DateTimeOffset b) => TimeZoneInfo.ConvertTime(a, Local).Date == TimeZoneInfo.ConvertTime(b, Local).Date;
}
