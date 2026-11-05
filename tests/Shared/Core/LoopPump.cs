namespace Tasktor.Tests.Shared;

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
