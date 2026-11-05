using Tasktor.Contracts.Platform;

namespace Tasktor.Core.Loop;

/// <summary>
/// Subscriptions "sampler X at interval Y" with reference counts (A: <c>Scheduler</c> + <c>Demand</c>). Each sampler runs
/// at the <b>shortest requested interval</b> with the <b>union of requested columns</b>; at zero leases it stops and its
/// release callback frees buffers. Loop-only; no locks.
/// </summary>
public sealed class Scheduler
{
    private readonly Dictionary<string, Sampler> _samplers = new(StringComparer.Ordinal);
    private readonly IClock _clock;

    public Scheduler(IClock clock) => _clock = clock;

    /// <summary>Register a sampler. <paramref name="run"/> gets the column union; <paramref name="release"/> runs when the last lease goes.</summary>
    public void Register(string name, Action<ColumnSet> run, Action? release = null)
    {
        if (!_samplers.TryAdd(name, new Sampler(name, run, release)))
        {
            throw new InvalidOperationException("Sampler already registered: " + name);
        }
    }

    public bool IsRegistered(string name) => _samplers.ContainsKey(name);

    /// <summary>Take a lease. Dispose it to release. The first lease runs the sampler at once.</summary>
    public DemandLease Lease(string name, TimeSpan interval, ColumnSet columns = ColumnSet.All, string? holder = null)
    {
        if (!_samplers.TryGetValue(name, out var s))
        {
            throw new KeyNotFoundException("No sampler named " + name);
        }

        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        var lease = new DemandLease(this, s, interval, columns, holder);
        var wasIdle = s.Leases.Count == 0;
        var faster = !wasIdle && interval < s.CurrentInterval;
        var widens = !wasIdle && (columns & ~s.CurrentColumns) != 0;
        s.Leases.Add(lease);
        if (wasIdle || faster || widens)
        {
            s.NextDue = _clock.UtcNow;
        }

        return lease;
    }

    /// <summary>Run every sampler that is due. Returns how many ran.</summary>
    public int Tick()
    {
        var now = _clock.UtcNow;
        var ran = 0;
        foreach (var s in _samplers.Values)
        {
            if (s.Leases.Count == 0 || s.NextDue is not { } due || due > now)
            {
                continue;
            }

            s.Run(s.CurrentColumns);
            s.Runs++;
            ran++;
            s.NextDue = now + s.CurrentInterval;
        }

        return ran;
    }

    /// <summary>When the next sampler is due (for the loop's wait).</summary>
    public DateTimeOffset? NextDue
    {
        get
        {
            DateTimeOffset? min = null;
            foreach (var s in _samplers.Values)
            {
                if (s.Leases.Count > 0 && s.NextDue is { } d && (min is null || d < min))
                {
                    min = d;
                }
            }

            return min;
        }
    }

    public int LeaseCount(string name) => _samplers.TryGetValue(name, out var s) ? s.Leases.Count : 0;

    public long Runs(string name) => _samplers.TryGetValue(name, out var s) ? s.Runs : 0;

    public TimeSpan? Interval(string name) => _samplers.TryGetValue(name, out var s) && s.Leases.Count > 0 ? s.CurrentInterval : null;

    public ColumnSet Columns(string name) => _samplers.TryGetValue(name, out var s) ? s.CurrentColumns : ColumnSet.None;

    internal void Release(DemandLease lease)
    {
        var s = lease.Sampler;
        if (!s.Leases.Remove(lease))
        {
            return;
        }

        if (s.Leases.Count == 0)
        {
            s.NextDue = null;
            s.ReleaseBuffers?.Invoke();
        }
    }

    internal sealed class Sampler(string name, Action<ColumnSet> run, Action? release)
    {
        public string Name { get; } = name;

        public Action<ColumnSet> Run { get; } = run;

        public Action? ReleaseBuffers { get; } = release;

        public List<DemandLease> Leases { get; } = [];

        public DateTimeOffset? NextDue { get; set; }

        public long Runs { get; set; }

        public TimeSpan CurrentInterval
        {
            get
            {
                var min = TimeSpan.MaxValue;
                foreach (var l in Leases)
                {
                    if (l.Interval < min)
                    {
                        min = l.Interval;
                    }
                }

                return min;
            }
        }

        public ColumnSet CurrentColumns
        {
            get
            {
                var c = ColumnSet.None;
                foreach (var l in Leases)
                {
                    c |= l.Columns;
                }

                return c;
            }
        }
    }
}

/// <summary>A held demand for a sampler (D-A5 / U1 <c>lease</c>). Disposing it releases the demand.</summary>
public sealed class DemandLease : IDisposable
{
    private readonly Scheduler _owner;
    private bool _disposed;

    internal DemandLease(Scheduler owner, Scheduler.Sampler sampler, TimeSpan interval, ColumnSet columns, string? holder)
    {
        _owner = owner;
        Sampler = sampler;
        Interval = interval;
        Columns = columns;
        Holder = holder;
    }

    public TimeSpan Interval { get; }

    public ColumnSet Columns { get; }

    public string? Holder { get; }

    public string SamplerName => Sampler.Name;

    internal Scheduler.Sampler Sampler { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _owner.Release(this);
    }
}
