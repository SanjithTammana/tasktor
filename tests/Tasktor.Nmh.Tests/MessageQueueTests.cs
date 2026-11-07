namespace Tasktor.Nmh.Tests;

public sealed class MessageQueueTests
{
    [Fact]
    public void QueueDropsOldestAtLineLimit()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, "queue");
            var queue = new MessageQueue(path);
            for (var i = 0; i < 1002; i++) queue.Append(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var lines = File.ReadAllLines(path);
            Assert.Equal(1000, lines.Length);
            Assert.Equal("2", lines[0]);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public void QueueDropsOldestAtByteLimit()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, "queue");
            var queue = new MessageQueue(path);
            queue.Append(new string('a', 600_000));
            queue.Append(new string('b', 600_000));
            var lines = File.ReadAllLines(path);
            Assert.Single(lines);
            Assert.Equal('b', lines[0][0]);
            Assert.True(new FileInfo(path).Length <= MessageQueue.MaxBytes);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task ConcurrentWritersDoNotLoseLines()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, "queue");
            var writes = Enumerable.Range(0, 50).Select(i => Task.Run(() => new MessageQueue(path).Append(i.ToString(System.Globalization.CultureInfo.InvariantCulture))));
            await Task.WhenAll(writes);
            Assert.Equal(50, File.ReadAllLines(path).Distinct().Count());
        }
        finally { Directory.Delete(directory, true); }
    }
}
