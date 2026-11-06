using Tasktor.Core.Workflow;
using Tasktor.Tests.Shared;

namespace Tasktor.Core.Tests;

/// <summary>
/// Closing the tracking stores never disposes them under a running write: it drains, and on a slow machine the writer
/// thread closes them itself (a 1 s join followed by dispose crashed the test host under load).
/// </summary>
public sealed class StoreWriterTests : IDisposable
{
    private readonly TempStore _store = new();
    private readonly List<Exception> _unhandled = [];

    public StoreWriterTests() => AppDomain.CurrentDomain.UnhandledException += OnUnhandled;

    public void Dispose()
    {
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
        _store.Dispose();
    }

    private void OnUnhandled(object? sender, UnhandledExceptionEventArgs e) => _unhandled.Add((Exception)e.ExceptionObject);

    private StoreSet Stores() => new(new EventStore(_store.Layout.EventsDb), new EpisodeStore(_store.Layout.EpisodesDb));

    [Fact]
    public void Dispose_drains_queued_writes_before_closing()
    {
        var writer = StoreWriter.Threaded(Stores());
        var done = 0;
        for (var i = 0; i < 50; i++)
        {
            writer.Enqueue(_ => { Thread.Sleep(2); Interlocked.Increment(ref done); });
        }

        writer.Dispose();
        Assert.Equal(50, done);
        Assert.True(writer.Closed);
    }

    [Fact]
    public void A_slow_writer_closes_the_stores_itself_and_nothing_is_disposed_under_it()
    {
        using var gate = new ManualResetEventSlim();
        var writer = StoreWriter.Threaded(Stores(), TimeSpan.FromMilliseconds(50));
        var wrote = false;
        writer.Enqueue(s =>
        {
            gate.Wait();
            _ = s.Events.Db; // still usable: not disposed underneath
            wrote = true;
        });

        writer.Dispose(); // gives up waiting after 50 ms
        Assert.False(writer.Closed);
        writer.Enqueue(_ => throw new InvalidOperationException("must not run after dispose"));

        gate.Set();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!writer.Closed && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }

        Assert.True(wrote);
        Assert.True(writer.Closed);
        Assert.Empty(_unhandled);
        writer.Dispose(); // twice is harmless
    }
}
