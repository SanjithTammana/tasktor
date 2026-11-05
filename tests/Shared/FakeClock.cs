using Tasktor.Contracts.Platform;

namespace Tasktor.Tests.Shared;

/// <summary>A clock tests move by hand.</summary>
internal sealed class FakeClock : IClock
{
    public FakeClock(DateTimeOffset? start = null) => UtcNow = start ?? new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);

    public DateTimeOffset UtcNow { get; set; }

    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>Drives a manual logic loop until a task it owes completes.</summary>
internal static class LoopPump
{
    public static T Run<T>(Tasktor.Core.Loop.LogicLoop loop, Task<T> task, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (loop.RunPending() == 0)
            {
                Thread.Sleep(1);
            }

            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException("task did not complete");
            }
        }

        loop.RunPending();
        return task.GetAwaiter().GetResult();
    }
}
