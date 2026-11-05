using System.Security.Cryptography;
using System.Text;

namespace Tasktor.Nmh;

/// <summary>Bounded local fallback for context messages when capture is unavailable.</summary>
public sealed class MessageQueue(string path)
{
    public const int MaxLines = 1000;
    public const int MaxBytes = 1024 * 1024;

    public void Append(string line)
    {
        if (line.Contains('\n') || Encoding.UTF8.GetByteCount(line) + 1 > MaxBytes)
        {
            return;
        }

        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path))));
        using var mutex = new Mutex(false, "tasktor-nmh-queue-" + key);
        try { mutex.WaitOne(); } catch (AbandonedMutexException) { }
        try
        {
            AppendLocked(line);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private void AppendLocked(string line)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        lines.Add(line);
        var size = lines.Sum(x => Encoding.UTF8.GetByteCount(x) + 1L);
        while (lines.Count > MaxLines || size > MaxBytes)
        {
            size -= Encoding.UTF8.GetByteCount(lines[0]) + 1;
            lines.RemoveAt(0);
        }

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }
}
