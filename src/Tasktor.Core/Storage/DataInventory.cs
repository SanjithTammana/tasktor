using Tasktor.Contracts.Export;

namespace Tasktor.Core.Storage;

/// <summary>
/// U1 D-H8 / U4 D-G3 "Your data": sizes and ages of what Tasktor keeps, grouped as the <c>forget</c> scopes delete it
/// (<see cref="Forgetter.PathsFor"/>). Links are never followed (a junction under the root is not counted). Notes and
/// presets are the user's own content and are not listed: no forget scope deletes them.
/// </summary>
public static class DataInventory
{
    public static DataReport Scan(StoreLayout layout, Forgetter forgetter)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(forgetter);
        var learned = forgetter.PathsFor(ForgetScope.Learned);
        var tracking = forgetter.PathsFor(ForgetScope.Tracking);
        var capture = forgetter.PathsFor(ForgetScope.Capture);
        var rest = forgetter.PathsFor(ForgetScope.All).Except(learned).Except(tracking).Except(capture).ToList();
        return new DataReport
        {
            Groups =
            [
                Group("learned", "What Tasktor learned", "until you forget it", learned),
                Group("tracking", "Activity tracking", "events 30 days, summaries until you forget them", tracking),
                Group("capture", "Captured typing and screen text", "7 days", capture),
                Group("all", "Logs, jobs and the snapshot", "logs " + Logging.FileLogWriter.KeepDays + " days, the rest while in use", rest),
            ],
        };
    }

    private static DataGroup Group(string scope, string name, string kept, IEnumerable<string> paths)
    {
        long bytes = 0;
        var files = 0;
        DateTimeOffset? oldest = null, newest = null;
        foreach (var file in paths.SelectMany(Files))
        {
            bytes += file.Length;
            files++;
            var at = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
            oldest = oldest is null || at < oldest ? at : oldest;
            newest = newest is null || at > newest ? at : newest;
        }

        return new DataGroup { Scope = scope, Name = name, Kept = kept, Bytes = bytes, Files = files, Oldest = oldest, Newest = newest };
    }

    private static IEnumerable<FileInfo> Files(string path)
    {
        if (File.Exists(path))
        {
            var f = new FileInfo(path);
            if ((f.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                yield return f;
            }

            yield break;
        }

        if (!Directory.Exists(path))
        {
            yield break;
        }

        var pending = new Stack<DirectoryInfo>([new DirectoryInfo(path)]);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = dir.EnumerateFileSystemInfos().ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var e in entries)
            {
                if ((e.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue; // never follow links
                }

                if (e is DirectoryInfo d)
                {
                    pending.Push(d);
                }
                else if (e is FileInfo f)
                {
                    yield return f;
                }
            }
        }
    }
}
