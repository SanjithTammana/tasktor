using System.Globalization;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Platform;
using Tasktor.Core.Rules;

namespace Tasktor.Core.Features.Folders;

/// <summary>
/// I12 Folder watch with an action (Module <c>folders</c>): the folders named by enabled <c>file.created</c> rules are
/// watched (Known Folders resolved, ≤ 8); each settled new file raises <c>file.created</c> with <c>{path}</c>,
/// <c>{file}</c> and <c>{folder}</c>, and the rule's toast offers its declared <c>run</c> command, which only a click
/// starts. The card lists the recent files. File contents are never read.
/// </summary>
public sealed class FolderAction : Feature
{
    private readonly Func<IReadOnlyList<string>, IEventSource<FileEvent>> _sourceFor;
    private readonly List<RuleEvent> _pending = [];
    private readonly List<(DateTimeOffset At, string File)> _recent = [];
    private IEventSource<FileEvent>? _source;
    private IReadOnlyList<string> _watched = [];

    public FolderAction(FeatureContext context, Func<IReadOnlyList<string>, IEventSource<FileEvent>> sourceFor)
        : base(context) => _sourceFor = sourceFor;

    public override string Id => "folders";

    public override FeatureTier Tier => FeatureTier.Module;

    /// <summary>The folders enabled <c>file.created</c> rules name, resolved (set by the composition from the rule set).</summary>
    public Func<IReadOnlyList<string>> Folders { get; set; } = () => [];

    public IReadOnlyList<string> Watched => _watched;

    /// <summary>Folders named by the rules' <c>file.created</c> conditions, Known Folders resolved, at most 8.</summary>
    public static List<string> FoldersOf(RuleSet set, Func<string, string?> knownFolder)
    {
        ArgumentNullException.ThrowIfNull(set);
        var list = new List<string>();
        void Walk(Contracts.Rules.Condition c)
        {
            if (c.Event == "file.created" && c.Folder is { } f)
            {
                var path = Placeholders.Fill(f, new Dictionary<string, string>(), knownFolder);
                if (!path.Contains('{', StringComparison.Ordinal))
                {
                    list.Add(path);
                }
            }

            foreach (var x in (c.All ?? []).Concat(c.Any ?? []))
            {
                Walk(x);
            }

            if (c.Not is { } n)
            {
                Walk(n);
            }
        }

        foreach (var r in set.Rules)
        {
            Walk(r.Rule.When);
        }

        return [.. list.Distinct(StringComparer.OrdinalIgnoreCase).Take(FolderWatchSource.MaxFolders)];
    }

    protected override void OnEnable() => Rewatch();

    protected override void OnDisable()
    {
        _source?.Stop();
        _source = null;
        _watched = [];
        _recent.Clear();
    }

    /// <summary>Rules changed: watch the folders they name now.</summary>
    public void Rewatch()
    {
        if (!Enabled)
        {
            return;
        }

        var folders = Folders();
        if (folders.SequenceEqual(_watched, StringComparer.OrdinalIgnoreCase) && _source is not null)
        {
            return;
        }

        _source?.Stop();
        _watched = folders;
        _source = folders.Count == 0 ? null : _sourceFor(folders);
        _source?.Start(e => Context.Loop.Post(() => OnFile(e)));
    }

    public void OnFile(FileEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var path = e.Path.Reveal();
        var file = Path.GetFileName(path);
        _recent.Insert(0, (e.At, file));
        if (_recent.Count > 10)
        {
            _recent.RemoveAt(_recent.Count - 1);
        }

        _pending.Add(new RuleEvent
        {
            Name = "file.created",
            Id = path + "@" + e.At.UtcTicks.ToString(CultureInfo.InvariantCulture),
            Fields = new Dictionary<string, string>(StringComparer.Ordinal) { ["path"] = path, ["file"] = file, ["folder"] = e.Folder },
        });
        Context.Log.Info("folders.file", Logging.LogField.Of("ext", Path.GetExtension(file).ToLowerInvariant())); // never the name
        Context.RulesDirty();
    }

    public override void Contribute(RuleInputs inputs, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        inputs.Events.AddRange(_pending);
        _pending.Clear();
    }

    public override IReadOnlyList<Card> Cards()
    {
        if (!Enabled)
        {
            return [];
        }

        var items = _recent.Select(r => TimeZoneInfo.ConvertTime(r.At, TimeZoneInfo.Local).ToString("HH:mm", CultureInfo.InvariantCulture) + "  " + r.File).ToList();
        return
        [
            new Card
            {
                Id = "module.folders",
                Source = CardKit.ModuleSource,
                Title = "Folder watch",
                Icon = IconKey.Folder,
                Blocks =
                [
                    new BadgeBlock { Text = Value.Literal(_watched.Count == 0 ? "Add a folder rule to watch a folder" : "Watching " + _watched.Count + (_watched.Count == 1 ? " folder" : " folders")), Priority = 1 },
                    new ListBlock { Label = "New files", Items = Value.FromRawJson(System.Text.Json.JsonSerializer.Serialize<IReadOnlyList<string>>(items, Contracts.ContractsJsonContext.Default.IReadOnlyListString)), Max = 5, Priority = 2 },
                ],
            },
        ];
    }
}
