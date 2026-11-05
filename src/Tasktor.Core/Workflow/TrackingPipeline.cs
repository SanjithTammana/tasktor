using System.Collections.Concurrent;
using Tasktor.Contracts.Workflow;

namespace Tasktor.Core.Workflow;

/// <summary>Both stores and this run's activity id base: what the store writer owns.</summary>
public sealed class StoreSet(EventStore events, EpisodeStore episodes) : IDisposable
{
    public EventStore Events { get; } = events;

    public EpisodeStore Episodes { get; } = episodes;

    public long Base { get; } = episodes.NextBase();

    public void Dispose()
    {
        Events.Dispose();
        Episodes.Dispose();
    }
}

/// <summary>
/// The store writer thread (C threading): owns both write connections, receives batches, posts nothing back to the loop
/// itself. Tests use <see cref="Inline"/>.
/// </summary>
public sealed class StoreWriter : IDisposable
{
    private readonly BlockingCollection<Action<StoreSet>> _queue = new(boundedCapacity: 4096);
    private readonly Thread? _thread;
    private readonly StoreSet _stores;

    private StoreWriter(StoreSet stores, bool thread)
    {
        _stores = stores;
        if (thread)
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "tasktor-store-writer", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }
    }

    public static StoreWriter Threaded(StoreSet stores) => new(stores, true);

    public static StoreWriter Inline(StoreSet stores) => new(stores, false);

    public long Failures { get; private set; }

    public void Enqueue(Action<StoreSet> work)
    {
        if (_thread is null)
        {
            work(_stores);
            return;
        }

        if (!_queue.IsAddingCompleted)
        {
            _queue.TryAdd(work, 1000);
        }
    }

    private void Run()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            try
            {
                work(_stores);
            }
            catch (Storage.SqliteException)
            {
                Failures++; // a full disk or a locked file: the batch is lost, never the loop
            }
        }
    }

    /// <summary>Drain the queue and close both stores (turning tracking off closes them within 1 s, C1 acceptance 6).</summary>
    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread?.Join(TimeSpan.FromSeconds(1));
        _queue.Dispose();
        _stores.Dispose();
    }
}

/// <summary>
/// The loop side of C1 + C4: every raw event goes through <see cref="PrivacyGate"/> first, then into the batch for
/// <c>events.db</c> and into <see cref="EpisodeBuilder"/>; closed activities (tokens hashed into buckets), windows and
/// sessions go to <c>episodes.db</c>, and frequent terms are promoted. The batch is written every 64 events or 10 s, so
/// a crash loses at most 10 s. Loop-only.
/// </summary>
public sealed class TrackingPipeline
{
    public const int BatchSize = 64;
    public static readonly long BatchMs = 10_000;

    private readonly PrivacyGate _gate;
    private readonly TokenBuckets _buckets;
    private readonly StoreWriter _writer;
    private readonly PreregFile _prereg;
    private readonly List<CaptureEvent> _batch = [];
    private long _offset;
    private long _lastWrite;
    private int _seenActivities;
    private int _seenWindows;
    private int _seenSessions;

    public TrackingPipeline(PrivacyGate gate, TokenBuckets buckets, StoreWriter writer, PreregFile prereg)
    {
        _gate = gate;
        _buckets = buckets;
        _writer = writer;
        _prereg = prereg;
        Builder = new EpisodeBuilder(prereg.Episodes, prereg.Features.MaxTokens);
    }

    public EpisodeBuilder Builder { get; }

    public PrivacyGate Gate => _gate;

    /// <summary>Raised on the loop for each closed activity (totals, assignment of closed activities).</summary>
    public event Action<ActivityRecord>? ActivityClosed;

    public event Action<WindowRecord>? WindowClosed;

    public long Events { get; private set; }

    public void On(CaptureEvent raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        _offset = raw.TsUtcMs - raw.MonoMs;
        foreach (var e in _gate.Apply(raw))
        {
            _batch.Add(e);
            Events++;
            Builder.On(e);
        }

        Drain(raw.MonoMs);
    }

    public void Tick(long monoMs)
    {
        Builder.Tick(monoMs);
        Drain(monoMs, force: monoMs - _lastWrite >= BatchMs);
    }

    public void Flush(long monoMs)
    {
        Builder.Flush(monoMs);
        Drain(monoMs, force: true);
    }

    private void Drain(long monoMs, bool force = false)
    {
        if (_batch.Count >= BatchSize || (force && _batch.Count > 0))
        {
            var batch = _batch.ToList();
            _batch.Clear();
            _lastWrite = monoMs;
            _writer.Enqueue(s => s.Events.Append(batch));
        }

        var f = _prereg.Features;
        for (; _seenActivities < Builder.Activities.Count; _seenActivities++)
        {
            var a = Builder.Activities[_seenActivities];
            var tokens = a.Tokens.Select(t => (t.Key, Bucket: _buckets.Of(t.Key), t.Value)).ToList();
            var merged = tokens.GroupBy(t => t.Bucket).Select(g => (g.Key, g.Sum(x => x.Value))).OrderBy(x => x.Key).ToList();
            var endUtc = a.StartUtcMs + (a.EndMs - a.StartMs);
            var day = Days.Of(a.StartUtcMs, a.TzMin);
            _writer.Enqueue(s =>
            {
                s.Episodes.AddActivity(s.Base + a.Index, a, endUtc, merged);
                s.Episodes.Promote(s.Events.ObserveTerms(tokens.Select(t => (t.Key, t.Bucket)), day, f.TermMinActivities, f.TermMinDays));
            });
            ActivityClosed?.Invoke(a);
        }

        for (; _seenWindows < Builder.Windows.Count; _seenWindows++)
        {
            var w = Builder.Windows[_seenWindows];
            var offset = _offset;
            _writer.Enqueue(s => s.Episodes.AddWindow(s.Base + w.Index, w.StartMs + offset, w.EndMs + offset, w.ActiveMs, Days.Of(w.StartMs + offset, 0), [.. w.Dwell.Select(d => (s.Base + d.Key, d.Value))]));
            WindowClosed?.Invoke(w);
        }

        for (; _seenSessions < Builder.Sessions.Count; _seenSessions++)
        {
            var se = Builder.Sessions[_seenSessions];
            var offset = _offset;
            _writer.Enqueue(s => s.Episodes.AddSession(s.Base + se.Index, se.StartMs + offset, se.EndMs + offset, [.. se.Activities.Select(i => s.Base + i)]));
        }
    }
}
