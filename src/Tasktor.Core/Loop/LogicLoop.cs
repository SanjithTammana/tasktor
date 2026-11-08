using System.Collections.Concurrent;
using Tasktor.Contracts.Platform;

namespace Tasktor.Core.Loop;

/// <summary>
/// The one logic thread (A D-A5 §7, B D-B11 §6). All Core state is touched only from here, so Core has no locks.
/// Sources and off-loop workers call <see cref="Post"/> (thread-safe); the loop runs the posted work in order,
/// and fires timers when they are due.
/// <para>Two ways to drive it: <see cref="Start"/> runs a dedicated thread with real waits; tests call
/// <see cref="RunPending"/> and <see cref="RunDue"/> with a fake clock.</para>
/// </summary>
public sealed class LogicLoop : IDisposable
{
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly List<Timer> _timers = [];
    private readonly IClock _clock;
    private readonly Action<Exception>? _onError;
    private Thread? _thread;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _stopping;
    private int _loopThreadId = -1;

    public LogicLoop(IClock clock, Action<Exception>? onError = null)
    {
        _clock = clock;
        _onError = onError;
    }

    public IClock Clock => _clock;

    /// <summary>True when the caller is on the logic thread (or the loop is driven manually).</summary>
    public bool IsLoopThread => _loopThreadId == -1 || Environment.CurrentManagedThreadId == _loopThreadId;

    /// <summary>Enqueue work for the logic thread. Thread-safe; never runs the work inline.</summary>
    public void Post(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        _queue.Enqueue(work);
        Wake();
    }

    /// <summary>A typed channel: sources post events, the loop calls <paramref name="handler"/>.</summary>
    public EventChannel<T> CreateChannel<T>(Action<T> handler) => new(this, handler);

    /// <summary>Run <paramref name="action"/> on the loop every <paramref name="interval"/>, first at now + interval.</summary>
    public IDisposable Every(TimeSpan interval, Action action) => AddTimer(_clock.UtcNow + interval, interval, action);

    /// <summary>Run <paramref name="action"/> once at <paramref name="due"/>.</summary>
    public IDisposable At(DateTimeOffset due, Action action) => AddTimer(due, null, action);

    /// <summary>Run everything queued so far (manual mode). Returns how many items ran.</summary>
    public int RunPending()
    {
        var n = 0;
        while (_queue.TryDequeue(out var work))
        {
            Execute(work);
            n++;
        }

        return n;
    }

    /// <summary>Fire every timer due at the clock's now (manual mode), then drain the queue. Returns timers fired.</summary>
    public int RunDue()
    {
        lock (_timers)
        {
            return RunDueLocked();
        }
    }

    private int RunDueLocked()
    {
        var fired = 0;
        var now = _clock.UtcNow;
        while (true)
        {
            Timer? next = null;
            foreach (var t in _timers)
            {
                if (!t.Cancelled && t.Due <= now && (next is null || t.Due < next.Due))
                {
                    next = t;
                }
            }

            if (next is null)
            {
                break;
            }

            if (next.Interval is { } every)
            {
                next.Due += every;
                if (next.Due <= now)
                {
                    // Skip missed periods instead of firing a burst after a long stall (e.g. sleep).
                    next.Due = now + every;
                }
            }
            else
            {
                next.Cancelled = true;
            }

            Execute(next.Action);
            fired++;
            RunPending();
        }

        _timers.RemoveAll(t => t.Cancelled);
        RunPending();
        return fired;
    }

    /// <summary>The earliest timer due, if any.</summary>
    public DateTimeOffset? NextDue
    {
        get
        {
            lock (_timers)
            {
                DateTimeOffset? min = null;
                foreach (var t in _timers)
                {
                    if (!t.Cancelled && (min is null || t.Due < min))
                    {
                        min = t.Due;
                    }
                }

                return min;
            }
        }
    }

    /// <summary>Start the dedicated logic thread.</summary>
    public void Start(string name = "tasktor-logic")
    {
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.Start();
    }

    /// <summary>
    /// Completes only after the logic thread has exited (its last callback returned), or at the first stop when it never
    /// started. Callers that must know no callback can still run (a capture host before it removes its indicator) wait on it.
    /// </summary>
    public Task Completion => _completion.Task;

    /// <summary>Stop the thread after the current item; queued work is dropped. Waits up to 5 s for it to exit.</summary>
    public void Stop() => TryStop(TimeSpan.FromSeconds(5));

    /// <summary>
    /// Stop after the current item and wait up to <paramref name="timeout"/> for the loop to finish. True only once no
    /// callback can run again (the loop returned from its last one, or never started); false while a callback still runs,
    /// and always false when called from the loop itself (it cannot prove its own exit).
    /// </summary>
    public bool TryStop(TimeSpan timeout)
    {
        _stopping = true;
        Wake();
        if (_thread is null)
        {
            _completion.TrySetResult();
            return true;
        }

        if (Environment.CurrentManagedThreadId == _thread.ManagedThreadId)
        {
            return false;
        }

        // Completion, not Join: it is set once the last callback has returned, which is the guarantee callers need; the
        // thread may still be a few instructions from exiting then, and a short Join could say "running" for that.
        return _completion.Task.Wait(timeout);
    }

    /// <summary>Post <paramref name="work"/> and wait for its result (for callers off the loop, e.g. tests and the pipe).</summary>
    public Task<T> InvokeAsync<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                tcs.SetResult(work());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    public void Dispose()
    {
        Stop();
        if (_thread is null || !_thread.IsAlive)
        {
            _signal.Dispose();
        }
    }

    private void Run()
    {
        _loopThreadId = Environment.CurrentManagedThreadId;
        try
        {
            while (!_stopping)
            {
                RunDue();
                var next = NextDue;
                var wait = next is null ? Timeout.InfiniteTimeSpan : next.Value - _clock.UtcNow;
                if (wait != Timeout.InfiniteTimeSpan && wait < TimeSpan.Zero)
                {
                    wait = TimeSpan.Zero;
                }

                if (wait > TimeSpan.FromMinutes(5))
                {
                    wait = TimeSpan.FromMinutes(5);
                }

                _signal.Wait(wait);
                RunPending();
            }
        }
        finally
        {
            _completion.TrySetResult(); // no callback runs after this: the thread only returns from here
        }
    }

    private IDisposable AddTimer(DateTimeOffset due, TimeSpan? interval, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (interval is { } i && i <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        var timer = new Timer(due, interval, action);
        lock (_timers)
        {
            _timers.Add(timer);
        }

        Wake();
        return timer;
    }

    private void Wake()
    {
        try
        {
            _signal.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Execute(Action work)
    {
        try
        {
            work();
        }
        catch (Exception ex) when (_onError is not null)
        {
            _onError(ex);
        }
    }

    private sealed class Timer(DateTimeOffset due, TimeSpan? interval, Action action) : IDisposable
    {
        public DateTimeOffset Due { get; set; } = due;

        public TimeSpan? Interval { get; } = interval;

        public Action Action { get; } = action;

        public bool Cancelled { get; set; }

        public void Dispose() => Cancelled = true;
    }
}

/// <summary>A typed queue onto the logic loop (A D-A5: <c>EventChannel&lt;T&gt;</c>). Sources only enqueue.</summary>
public sealed class EventChannel<T>
{
    private readonly LogicLoop _loop;
    private readonly Action<T> _handler;
    private long _posted;

    internal EventChannel(LogicLoop loop, Action<T> handler)
    {
        _loop = loop;
        _handler = handler;
    }

    public long Posted => Interlocked.Read(ref _posted);

    /// <summary>Thread-safe: enqueue <paramref name="item"/>; the handler runs on the logic thread.</summary>
    public void Post(T item)
    {
        Interlocked.Increment(ref _posted);
        _loop.Post(() => _handler(item));
    }
}
