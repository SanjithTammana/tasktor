using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Cards;
using Tasktor.Core.Actions;
using Tasktor.Core.Storage;

namespace Tasktor.Core.Features.Projects;

/// <summary><c>state\projects.json</c>: workflow / cluster id → project name (stable ids survive refits, D-C12).</summary>
public sealed record ProjectsFile
{
    public int SchemaVersion { get; init; } = 1;

    public Dictionary<string, string> Map { get; init; } = new(StringComparer.Ordinal);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ProjectsFile))]
internal sealed partial class ProjectsJsonContext : JsonSerializerContext;

/// <summary>I6 <c>ProjectMap</c>: user labels over C's workflows (many workflows → one project), persisted.</summary>
public sealed class ProjectMap
{
    private ProjectsFile _file = new();

    public ProjectMap(string path)
    {
        Path = path;
        try
        {
            if (File.Exists(path))
            {
                _file = JsonSerializer.Deserialize(File.ReadAllBytes(path), ProjectsJsonContext.Default.ProjectsFile) ?? new();
            }
        }
        catch (JsonException)
        {
            _file = new();
        }
    }

    public string Path { get; }

    public IReadOnlyDictionary<string, string> Map => _file.Map;

    public string? ProjectOf(string? id) => id is not null && _file.Map.TryGetValue(id, out var p) ? p : null;

    /// <summary>Map or (with an empty name) unmap an id; returns the bytes to persist.</summary>
    public byte[] Set(string id, string? project)
    {
        if (string.IsNullOrWhiteSpace(project))
        {
            _file.Map.Remove(id);
        }
        else
        {
            _file.Map[id] = project.Trim();
        }

        return JsonSerializer.SerializeToUtf8Bytes(_file, ProjectsJsonContext.Default.ProjectsFile);
    }
}

/// <summary>
/// I6 Time per project (Module <c>projects</c>, needs T0 tracking): C9's human time per workflow, labelled by the
/// <see cref="ProjectMap"/>; today, this week and last week; unmapped time is "Unassigned". Weeks start on Monday (local).
/// Fed by <see cref="Add"/> (C9's totals); a mapping applies to all totals at once, since ids are stable.
/// </summary>
public sealed class ProjectTime : Feature
{
    public const string Unassigned = "Unassigned";

    private readonly ProjectMap _map;
    private readonly TimeZoneInfo _local;
    private readonly List<(string? Id, long Ms, DateTimeOffset At)> _items = [];

    public ProjectTime(FeatureContext context, ProjectMap map, TimeZoneInfo? local = null)
        : base(context)
    {
        _map = map;
        _local = local ?? TimeZoneInfo.Local;
    }

    public override string Id => "projects";

    public override FeatureTier Tier => FeatureTier.Module;

    protected override void OnEnable()
    {
        Context.Router.Register("project.map", MapAction);
        Context.RegisterExport(new ExportTableSource("project_time_weekly", ["week", "project", "minutes"], since =>
            Weekly().Where(r => since is null || string.CompareOrdinal(r.Week, WeekOf(since.Value)) >= 0)
                .Select(r => new object?[] { r.Week, r.Project, Math.Round(r.Ms / 60_000.0, 1) })));
    }

    protected override void OnDisable() => _items.Clear();

    /// <summary>C9 adds the human time of one closed activity with its workflow (or activity cluster) id.</summary>
    public void Add(string? clusterOrWorkflowId, long activeMs, DateTimeOffset at)
    {
        if (activeMs > 0)
        {
            _items.Add((clusterOrWorkflowId, activeMs, at));
        }
    }

    public string ProjectOf(string? id) => _map.ProjectOf(id) ?? Unassigned;

    /// <summary>The Monday of <paramref name="t"/>'s local week, <c>yyyy-MM-dd</c>.</summary>
    public string WeekOf(DateTimeOffset t)
    {
        var d = TimeZoneInfo.ConvertTime(t, _local).Date;
        return d.AddDays(-(((int)d.DayOfWeek + 6) % 7)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    public IEnumerable<(string Week, string Project, long Ms)> Weekly() =>
        _items.GroupBy(i => (Week: WeekOf(i.At), Project: ProjectOf(i.Id)))
            .OrderBy(g => g.Key.Week, StringComparer.Ordinal).ThenBy(g => g.Key.Project, StringComparer.Ordinal)
            .Select(g => (g.Key.Week, g.Key.Project, g.Sum(i => i.Ms)));

    public Dictionary<string, long> Totals(Func<DateTimeOffset, bool> when) =>
        _items.Where(i => when(i.At)).GroupBy(i => ProjectOf(i.Id)).ToDictionary(g => g.Key, g => g.Sum(i => i.Ms), StringComparer.Ordinal);

    public Dictionary<string, long> Today()
    {
        var today = TimeZoneInfo.ConvertTime(Context.Clock.UtcNow, _local).Date;
        return Totals(t => TimeZoneInfo.ConvertTime(t, _local).Date == today);
    }

    public Dictionary<string, long> ThisWeek()
    {
        var w = WeekOf(Context.Clock.UtcNow);
        return Totals(t => WeekOf(t) == w);
    }

    public Dictionary<string, long> LastWeek()
    {
        var w = WeekOf(Context.Clock.UtcNow.AddDays(-7));
        return Totals(t => WeekOf(t) == w);
    }

    private PreparedAction MapAction(ActionRequest req)
    {
        var id = req.Target?.GetValueOrDefault("workflow") ?? req.Target?.GetValueOrDefault("cluster");
        if (string.IsNullOrWhiteSpace(id))
        {
            return new PreparedAction { Summary = "Assign project", Refusal = "Which workflow?" };
        }

        var project = req.Args?.GetValueOrDefault("project");
        if (project is { Length: > 80 })
        {
            return new PreparedAction { Summary = "Assign project", Refusal = "A project name can be up to 80 characters" };
        }

        return new PreparedAction
        {
            Summary = "Assign project",
            Immediate = new ActionResult { Action = "project.map", Status = ActionStatus.Ok, Message = string.IsNullOrWhiteSpace(project) ? "Unassigned" : "Assigned to " + project.Trim() },
            Done = _ => Context.Persist(_map.Path, _map.Set(id, project)),
        };
    }

    public override IReadOnlyList<Card> Cards()
    {
        if (!Enabled)
        {
            return [];
        }

        var week = ThisWeek().OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        var last = LastWeek();
        var today = Today();
        var rows = new JsonArray([.. week.Select(kv => (JsonNode)new JsonObject
        {
            ["project"] = kv.Key,
            ["today_min"] = Math.Round(today.GetValueOrDefault(kv.Key) / 60_000.0),
            ["week_min"] = Math.Round(kv.Value / 60_000.0),
            ["last_week_min"] = Math.Round(last.GetValueOrDefault(kv.Key) / 60_000.0),
        })]);
        var blocks = new List<Block>
        {
            new TableBlock
            {
                Label = "This week",
                Columns =
                [
                    new TableColumn { Key = "project", Label = "Project" },
                    new TableColumn { Key = "today_min", Label = "Today", Unit = "min" },
                    new TableColumn { Key = "week_min", Label = "Week", Unit = "min" },
                    new TableColumn { Key = "last_week_min", Label = "Last week", Unit = "min" },
                ],
                Rows = Value.FromRawJson(rows.ToJsonString()),
                RowKey = "project",
                Max = 6,
                Priority = 1,
            },
        };
        if (week.Count > 0)
        {
            var total = week.Sum(kv => kv.Value);
            blocks.Add(new BarBlock { Label = week[0].Key, Value = Value.Literal(Math.Round(week[0].Value / 60_000.0)), Max = Value.Literal(Math.Round(total / 60_000.0)), Unit = "min", Priority = 2 });
        }
        else
        {
            blocks.Add(new BadgeBlock { Text = Value.Literal("No tracked time this week yet"), Priority = 2 });
        }

        return [new Card { Id = "module.projects", Source = CardKit.ModuleSource, Title = "Projects", Icon = IconKey.Workflow, Blocks = blocks }];
    }
}
