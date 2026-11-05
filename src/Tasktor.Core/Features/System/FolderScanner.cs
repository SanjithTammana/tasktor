using System.Text.Json.Serialization;

namespace Tasktor.Core.Features.System;

/// <summary>One folder in a scan result: its total, its kept children (top 20), and how many entries could not be read.</summary>
public sealed class FolderNode
{
    public required string Name { get; init; }

    public long Bytes { get; set; }

    public long Files { get; set; }

    public int Unreadable { get; set; }

    public List<FolderNode> Children { get; init; } = [];
}

/// <summary>
/// A5.3 biggest folders: a streaming depth-first walk that sums sizes post-order as it returns, keeps the top 20 children per
/// level for 4 levels (deeper drill-downs rescan that subtree), never follows junctions or symlinks (no loops, no double
/// counting), counts unreadable folders instead of failing, and stops within one directory when cancelled. Memory is
/// O(depth + kept rows). <see cref="SizeOf"/> is the allocated size on Windows (OneDrive placeholders count ~0, never hydrated).
/// </summary>
public sealed class FolderScanner
{
    public const int KeepPerLevel = 20;
    public const int KeepDepth = 4;

    /// <summary>Size of one file; default the length. Windows supplies the allocated size on disk.</summary>
    public Func<FileInfo, long> SizeOf { get; init; } = f => f.Length;

    public long FoldersSeen { get; private set; }

    public FolderNode Scan(string root, CancellationToken cancel, Action<long>? progress = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        FoldersSeen = 0;
        return Walk(new DirectoryInfo(root), 0, cancel, progress);
    }

    private FolderNode Walk(DirectoryInfo dir, int depth, CancellationToken cancel, Action<long>? progress)
    {
        cancel.ThrowIfCancellationRequested();
        var node = new FolderNode { Name = depth == 0 ? dir.FullName : dir.Name };
        FoldersSeen++;
        if (FoldersSeen % 500 == 0)
        {
            progress?.Invoke(FoldersSeen);
        }

        IEnumerable<FileSystemInfo> entries;
        try
        {
            entries = dir.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = false, RecurseSubdirectories = false, AttributesToSkip = 0, ReturnSpecialDirectories = false }).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or global::System.Security.SecurityException)
        {
            node.Unreadable++;
            return node;
        }

        foreach (var e in entries)
        {
            if ((e.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue; // junctions and symlinks are not followed
            }

            if (e is DirectoryInfo sub)
            {
                var child = Walk(sub, depth + 1, cancel, progress);
                node.Bytes += child.Bytes;
                node.Files += child.Files;
                node.Unreadable += child.Unreadable;
                if (depth + 1 < KeepDepth)
                {
                    Keep(node.Children, child);
                }
            }
            else if (e is FileInfo f)
            {
                try
                {
                    node.Bytes += SizeOf(f);
                    node.Files++;
                }
                catch (IOException)
                {
                    node.Unreadable++;
                }
            }
        }

        node.Children.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
        return node;
    }

    private static void Keep(List<FolderNode> kept, FolderNode child)
    {
        child.Children.TrimExcess();
        if (kept.Count < KeepPerLevel)
        {
            kept.Add(child);
            return;
        }

        var min = 0;
        for (var i = 1; i < kept.Count; i++)
        {
            if (kept[i].Bytes < kept[min].Bytes)
            {
                min = i;
            }
        }

        if (child.Bytes > kept[min].Bytes)
        {
            kept[min] = child;
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(FolderNode))]
internal sealed partial class FolderJsonContext : JsonSerializerContext;
