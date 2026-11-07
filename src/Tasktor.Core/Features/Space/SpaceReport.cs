using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Cards;
using Tasktor.Core.Actions;
using Tasktor.Core.Features.SystemMonitor;

namespace Tasktor.Core.Features.Space;

/// <summary>One report line: a named category, its folder and size. The cleanup command is the tool's own.</summary>
public sealed record SpaceItem([property: JsonRequired] string Name, [property: JsonRequired] string Kind, string? Path, long Bytes, string? Cleanup);

public sealed record SpaceReportFile
{
    public DateTimeOffset At { get; init; }

    public List<SpaceItem> Items { get; set; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(SpaceReportFile))]
internal sealed partial class SpaceJsonContext : JsonSerializerContext;

/// <summary>
/// I7 Space report (Core, report only; a tab in A5's disk flyout): each named cache, Downloads, <c>%TEMP%</c>, OneDrive
/// files kept on this device (hydrated: without <c>RECALL_ON_DATA_ACCESS</c> / offline), the Recycle Bin and folders the
/// user adds. Scans run only on request, at BelowNormal, within A5's folder-scan budget; results are cached on disk for
/// 24 h. It never deletes: Open in Explorer, or copy the tool's own cleanup command for the user to run.
/// </summary>
public sealed class SpaceReport : Feature
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromHours(24);
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    private readonly CacheLocator _caches;
    private CancellationTokenSource? _scan;

    public SpaceReport(FeatureContext context, CacheLocator caches)
        : base(context) => _caches = caches;

    public override string Id => "space";

    public override FeatureTier Tier => FeatureTier.Core;

    /// <summary>Known Folders: Downloads, Temp, OneDrive.</summary>
    public Func<string, string?> Folder { get; set; } = _ => null;

    /// <summary>Recycle Bin bytes for all drives (Windows: <c>SHQueryRecycleBinW</c>), or null.</summary>
    public Func<long?> RecycleBinBytes { get; set; } = () => null;

    public string CachePath => System.IO.Path.Combine(Context.Store.CacheDir, "space-report.json");

    public SpaceReportFile? Last { get; private set; }

    public bool Scanning => _scan is not null;

    protected override void OnEnable()
    {
        Last = ReadCache();
        Context.Router.Register("space.report", Report);
        Context.Router.Register("space.copy_cleanup", CopyCleanup);
        Context.RegisterPull("space", _ => Last is null ? null : JsonSerializer.SerializeToNode(Last, SpaceJsonContext.Default.SpaceReportFile));
    }

    protected override void OnDisable() => _scan?.Cancel();

    public override IReadOnlyList<Card> Cards() => [];

    private SpaceReportFile? ReadCache()
    {
        try
        {
            return File.Exists(CachePath) ? JsonSerializer.Deserialize(File.ReadAllBytes(CachePath), SpaceJsonContext.Default.SpaceReportFile) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The categories to measure now (paths that exist only).</summary>
    public List<(string Name, string Kind, string Path, string? Cleanup, bool HydratedOnly)> Categories()
    {
        var list = _caches.Locate().Select(c => (c.Name, "cache", c.Path, c.Cleanup, false)).ToList();
        foreach (var (name, known) in new[] { ("Downloads", "Downloads"), ("Temporary files", "Temp") })
        {
            if (Folder(known) is { } p && Directory.Exists(p))
            {
                list.Add((name, "folder", p, null, false));
            }
        }

        if (Folder("OneDrive") is { } od && Directory.Exists(od))
        {
            list.Add(("OneDrive kept on this device", "onedrive", od, null, true));
        }

        foreach (var extra in Context.Settings.Current.Space?.Folders ?? [])
        {
            if (Directory.Exists(extra))
            {
                list.Add((System.IO.Path.GetFileName(extra.TrimEnd('\\', '/')), "user", extra, null, false));
            }
        }

        return list;
    }

    private PreparedAction Report(ActionRequest req)
    {
        var refresh = req.Args?.GetValueOrDefault("refresh") is "true" or "1";
        var now = Context.Clock.UtcNow;
        if (!refresh && Last is { } cached && now - cached.At < CacheFor)
        {
            return new PreparedAction { Summary = "Space report", Immediate = new ActionResult { Action = "space.report", Status = ActionStatus.Ok, Message = "From " + cached.At.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) } };
        }

        if (_scan is not null)
        {
            return new PreparedAction { Summary = "Space report", Immediate = new ActionResult { Action = "space.report", Status = ActionStatus.Ok, Message = "Already measuring" } };
        }

        var categories = Categories();
        _scan = new CancellationTokenSource();
        var token = _scan.Token;
        return new PreparedAction
        {
            Summary = "Space report",
            Immediate = new ActionResult { Action = "space.report", Status = ActionStatus.Ok, Message = "Measuring " + categories.Count + " places" },
            Done = _ => Context.OffLoop(() =>
            {
                var prio = Thread.CurrentThread.Priority;
                Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                try
                {
                    var items = new List<SpaceItem>();
                    foreach (var (name, kind, path, cleanup, hydrated) in categories)
                    {
                        if (token.IsCancellationRequested)
                        {
                            break;
                        }

                        var scanner = new FolderScanner { SizeOf = hydrated ? HydratedSize : f => f.Length };
                        items.Add(new SpaceItem(name, kind, path, scanner.Scan(path, token).Bytes, cleanup));
                    }

                    if (RecycleBinBytes() is { } bin)
                    {
                        items.Add(new SpaceItem("Recycle Bin", "recycle", null, bin, null));
                    }

                    var file = new SpaceReportFile { At = now, Items = [.. items.OrderByDescending(i => i.Bytes)] };
                    Context.Loop.Post(() =>
                    {
                        _scan = null;
                        if (!token.IsCancellationRequested)
                        {
                            Last = file;
                            Context.Persist(CachePath, JsonSerializer.SerializeToUtf8Bytes(file, SpaceJsonContext.Default.SpaceReportFile));
                        }
                    });
                }
                finally
                {
                    Thread.CurrentThread.Priority = prio;
                }
            }),
        };
    }

    /// <summary>OneDrive "kept on this device": count a file only when its content is local (not cloud-only).</summary>
    public static long HydratedSize(FileInfo f)
    {
        ArgumentNullException.ThrowIfNull(f);
        return (f.Attributes & (RecallOnDataAccess | FileAttributes.Offline)) != 0 ? 0 : f.Length;
    }

    /// <summary>Returns the tool's own cleanup command for the surface to put on the clipboard. Never runs it.</summary>
    private PreparedAction CopyCleanup(ActionRequest req)
    {
        var name = req.Target?.GetValueOrDefault("cache");
        var item = Last?.Items.FirstOrDefault(i => i.Name == name && i.Cleanup is not null);
        return item is null
            ? new PreparedAction { Summary = "Copy cleanup command", Refusal = "That cache has no cleanup command" }
            : new PreparedAction { Summary = "Copy cleanup command", Immediate = new ActionResult { Action = "space.copy_cleanup", Status = ActionStatus.Ok, Message = item.Cleanup } };
    }
}
