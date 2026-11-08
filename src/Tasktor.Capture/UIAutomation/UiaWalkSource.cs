using System.Collections.Concurrent;
using Tasktor.Capture.Deep;
using Tasktor.Contracts.Platform;

namespace Tasktor.Capture.UIAutomation;

/// <summary>
/// C §4 <c>UiaWalkSource</c>: the UIA worker thread (MTA, its own apartment, never the capture logic thread). The client
/// is created and used on it; its events only queue here; walks and focus results are posted out (to the session's
/// gate). Also the click-target resolver (#6): a hit test on the thread pool (MTA), under the caller's cancellation.
/// </summary>
public sealed class UiaWalkSource : IClickTargetResolver, IDisposable
{
    private readonly Func<IUiaClient?> _create;
    private readonly Func<IUiaClient, UiaWorker> _worker;
    private readonly BlockingCollection<Action<UiaWorker>> _queue = new(new ConcurrentQueue<Action<UiaWorker>>(), 1024);
    private readonly ManualResetEventSlim _ready = new();
    private Thread? _thread;
    private volatile IUiaClient? _client;

    /// <param name="create">Makes the client on the worker thread (null: UIA unavailable).</param>
    public UiaWalkSource(Func<IUiaClient?> create, Func<long, string?> appOf, Func<string, bool> blocked, Action<Sensitive<UiaWalk>, string> postWalk,
        Action<FocusTarget, Sensitive<string>?, Sensitive<string>?> postFocus)
    {
        _create = create ?? throw new ArgumentNullException(nameof(create));
        _worker = c => new UiaWorker(c, appOf, blocked, postWalk, postFocus, () => Environment.TickCount64);
    }

    public UiaWorker? Worker { get; private set; }

    /// <summary>Start the worker thread; false when no client could be made.</summary>
    public bool Start()
    {
        if (_thread is not null) return _client is not null;
        _thread = new Thread(Run) { IsBackground = true, Name = "tasktor-uia" };
        if (OperatingSystem.IsWindows()) _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(10));
        return _client is not null;
    }

    public Task<Sensitive<string>?> ResolveAsync(int x, int y, CancellationToken cancel)
    {
        if (_client is not { } client) return Task.FromResult<Sensitive<string>?>(null);
        return Task.Run(() => client.HitTest(x, y) is { } t ? Sensitive.Of(t) : (Sensitive<string>?)null, cancel);
    }

    /// <summary>Stop the thread (it releases the client's handlers on its own thread). Before the session stops.</summary>
    public void Dispose()
    {
        if (_thread is not { } thread) return;
        _queue.CompleteAdding();
        thread.Join(TimeSpan.FromSeconds(5));
        _thread = null;
    }

    private void Run()
    {
        IUiaClient? client = null;
        try
        {
            client = _create();
            if (client is null) return;
            var worker = Worker = _worker(client);
            client.FocusChanged += f => _queue.TryAdd(w => w.OnFocus(f));
            client.WindowDirty += h => _queue.TryAdd(w => w.OnDirty(h)); // full: dropped (the next change marks it again)
            _client = client;
            _ready.Set();
            long? due = null;
            while (!_queue.IsCompleted)
            {
                var wait = due is { } d ? (int)Math.Clamp(d - Environment.TickCount64, 0, int.MaxValue) : Timeout.Infinite;
                if (_queue.TryTake(out var work, wait))
                {
                    Safe(() => work(worker));
                    while (_queue.TryTake(out work)) Safe(() => work(worker));
                }

                long? next = null;
                Safe(() => next = worker.Step(Environment.TickCount64));
                due = next;
            }
        }
        catch (InvalidOperationException)
        {
            // completed while taking
        }
        finally
        {
            _client = null;
            try
            {
                client?.Dispose();
            }
            catch (Exception)
            {
                // the connection is already gone
            }

            _ready.Set();
        }
    }

    private static void Safe(Action a)
    {
        try
        {
            a();
        }
        catch (Exception)
        {
            // a provider failure costs one walk; nothing is logged (it could carry screen text)
        }
    }
}
