using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Config;
using Tasktor.Contracts.Snapshot;
using Tasktor.Core.Actions;
using Tasktor.Core.Features.Calendar;
using Tasktor.Core.Rules;
using Tasktor.Core.Storage;

namespace Tasktor.Core.Features.Focus;

public sealed record FocusFile
{
    public int SchemaVersion { get; init; } = 1;

    public List<BlockResult> Blocks { get; set; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(FocusFile))]
internal sealed partial class FocusJsonContext : JsonSerializerContext;

/// <summary>
/// E11 Focus timer (Module <c>focus</c>): named presets (shipped 25/5 with a long break every 4, 50/10, 90/20, plus the
/// user's own from <c>config.json</c>). A focus block is a short event-mode window (I13), so refits and the content worker
/// wait; state <c>focus_block</c>. When a block ends, the timer waits and <c>focus.block_end</c> drives the default toast
/// (Next / +5 min / Stop). Each finished focus block goes to <see cref="FocusReview"/> with C's numbers when tracking is
/// on. Countdowns are computed by viewers; one wake per block end. Quick notes (E11.3) live here too.
/// </summary>
public sealed class FocusTimer : Feature
{
    private readonly EventMode _eventMode;
    private readonly NotesStore _notes;
    private readonly List<RuleEvent> _pending = [];
    private FocusFile _file = new();
    private FocusPreset? _preset;
    private int _index;
    private int _focusDone;
    private DateTimeOffset _blockStart;
    private DateTimeOffset _until;
    private TimeSpan? _pausedLeft;
    private bool _waiting;
    private string? _intended;
    private IDisposable? _wake;

    public FocusTimer(FeatureContext context, EventMode eventMode, NotesStore notes)
        : base(context)
    {
        _eventMode = eventMode;
        _notes = notes;
    }

    public override string Id => "focus";

    public override FeatureTier Tier => FeatureTier.Module;

    /// <summary>C's numbers for a block (from, to, intended workflow id) or null when tracking is off (set by M11).</summary>
    public Func<DateTimeOffset, DateTimeOffset, string?, BlockMeasures?> Measure { get; set; } = (_, _, _) => null;

    /// <summary>C's current workflow id at start, used when the user does not pick one.</summary>
    public Func<string?> CurrentWorkflow { get; set; } = () => null;

    public Func<bool> TrackingOn { get; set; } = () => false;

    public bool Running => _preset is not null && !_waiting && _pausedLeft is null;

    public bool Paused => _pausedLeft is not null;

    public bool Waiting => _waiting;

    public string? PresetName => _preset?.Name;

    public FocusBlockDef? Block => _preset is null ? null : BlockAt(_index);

    public IReadOnlyList<BlockResult> Results => _file.Blocks;

    protected override void OnEnable()
    {
        Load();
        Context.Router.Register("focus.start", Start);
        Context.Router.Register("focus.pause", _ => Do("focus.pause", Paused ? "Resumed" : "Paused", TogglePause));
        Context.Router.Register("focus.skip", _ => Do("focus.skip", "Next block", () => Advance(BlockEnd.Skipped)));
        Context.Router.Register("focus.next", _ => Do("focus.next", "Next block", () => Advance(BlockEnd.Completed)));
        Context.Router.Register("focus.extend", _ => Do("focus.extend", "+5 min", Extend));
        Context.Router.Register("focus.stop", _ => Do("focus.stop", "Stopped", Stop));
        Context.Router.Register("note.save", SaveNote);
        Context.Router.Register("note.pin", req => NoteAction(req, "note.pin", n => _notes.Pin(n, true)));
        Context.Router.Register("note.unpin", req => NoteAction(req, "note.unpin", n => _notes.Pin(n, false)));
        Context.Router.Register("note.delete", req => NoteAction(req, "note.delete", _notes.Delete));
        Context.RegisterExport(new ExportTableSource(
            "focus_blocks",
            ["at", "preset", "minutes", "end", "intended_workflow", "on_intent_share", "switches", "idle_share", "agent_share", "excluded"],
            since => _file.Blocks.Where(b => since is null || b.At >= since).Select(b => new object?[] { b.At, b.Preset, b.Minutes, Word(b.End), b.IntendedWorkflow, b.OnIntentShare, b.Switches, b.IdleShare, b.AgentShare, b.Excluded })));
        Context.RegisterExport(new ExportTableSource(
            "focus_presets",
            ["preset", "blocks", "mean_on_intent", "switches_per_block", "completion_rate", "excluded"],
            _ => FocusReview.Stats(_file.Blocks).Select(s => new object?[] { s.Preset, s.Blocks, s.MeanOnIntent, s.SwitchesPerBlock, s.CompletionRate, s.Excluded })));
        Context.RegisterPull("focus", _ => PullFocus());
        Publish();
    }

    protected override void OnDisable()
    {
        if (_preset is not null)
        {
            Stop();
        }
    }

    /// <summary>
    /// The deep window's Focus page (U4 D-G3): presets to start from, the last blocks, per-preset statistics and the
    /// comparison line. Names, minutes and numbers only: no note text, no titles, no workflow ids.
    /// </summary>
    private JsonNode PullFocus()
    {
        var presets = new JsonArray();
        foreach (var p in Context.Settings.FocusPresets)
        {
            presets.Add((JsonNode)new JsonObject { ["name"] = p.Name, ["summary"] = Summary(p) });
        }

        var blocks = new JsonArray();
        foreach (var b in _file.Blocks.OrderByDescending(r => r.At).Take(10))
        {
            blocks.Add((JsonNode)new JsonObject
            {
                ["at"] = b.At.ToString("O", CultureInfo.InvariantCulture),
                ["preset"] = b.Preset,
                ["minutes"] = b.Minutes,
                ["end"] = Word(b.End),
                ["on_intent_share"] = b.OnIntentShare,
                ["switches"] = b.Switches,
                ["excluded"] = b.Excluded,
            });
        }

        var stats = new JsonArray();
        foreach (var s in FocusReview.Stats(_file.Blocks))
        {
            stats.Add((JsonNode)new JsonObject
            {
                ["preset"] = s.Preset,
                ["blocks"] = s.Blocks,
                ["mean_on_intent"] = Math.Round(s.MeanOnIntent, 3),
                ["switches_per_block"] = Math.Round(s.SwitchesPerBlock, 1),
                ["completion_rate"] = Math.Round(s.CompletionRate, 3),
                ["excluded"] = s.Excluded,
            });
        }

        return new JsonObject { ["tracking"] = TrackingOn(), ["presets"] = presets, ["blocks"] = blocks, ["stats"] = stats, ["review"] = ReviewLine() };
    }

    private static string Summary(FocusPreset p)
    {
        var parts = p.Blocks.Select(b => b.Minutes.ToString(CultureInfo.InvariantCulture) + " min " + (b.Kind == "focus" ? "focus" : "break"));
        var text = string.Join(", ", parts);
        return p.LongBreakEvery is > 0 and var every ? text + ", a long break every " + every.ToString(CultureInfo.InvariantCulture) : text;
    }

    private static string Word(BlockEnd e) => e switch { BlockEnd.Completed => "completed", BlockEnd.Skipped => "skipped", _ => "stopped" };

    private void Load()
    {
        try
        {
            _file = File.Exists(Context.Store.Focus) ? JsonSerializer.Deserialize(File.ReadAllBytes(Context.Store.Focus), FocusJsonContext.Default.FocusFile) ?? new() : new();
        }
        catch (JsonException)
        {
            _file = new();
        }
    }

    /// <summary>Block <paramref name="i"/> of the running sequence, with the long break in place of every n-th break.</summary>
    private FocusBlockDef? BlockAt(int i)
    {
        var p = _preset!;
        if (!p.Repeat && i >= p.Blocks.Count)
        {
            return null;
        }

        var b = p.Blocks[i % p.Blocks.Count];
        var focusBefore = Enumerable.Range(0, i + 1).Count(k => p.Blocks[k % p.Blocks.Count].Kind == "focus");
        if (b.Kind == "break" && p.LongBreakEvery is > 0 and var every && focusBefore % every == 0)
        {
            return new FocusBlockDef { Kind = "long_break", Minutes = p.LongBreakMinutes ?? (b.Minutes * 3) };
        }

        return b;
    }

    private PreparedAction Start(ActionRequest req)
    {
        var name = req.Args?.GetValueOrDefault("preset");
        var preset = Context.Settings.FocusPresets.FirstOrDefault(p => p.Name == name) ?? Context.Settings.FocusPresets[0];
        var intended = req.Args?.GetValueOrDefault("workflow");
        return Do("focus.start", preset.Name + " started", () =>
        {
            if (_preset is not null)
            {
                Stop();
            }

            _preset = preset;
            _index = 0;
            _focusDone = 0;
            _intended = intended ?? CurrentWorkflow();
            Begin(Context.Clock.UtcNow);
        });
    }

    private void Begin(DateTimeOffset now)
    {
        _waiting = false;
        _pausedLeft = null;
        _blockStart = now;
        _until = now.AddMinutes(Block!.Minutes);
        Arm();
    }

    private void Arm()
    {
        _wake?.Dispose();
        _wake = Context.Loop.At(_until, BlockTimeUp);
        if (Block!.Kind == "focus")
        {
            _eventMode.Begin(new EventWindow("focus", _preset!.Name, _blockStart, _until, "focus"));
        }
        else
        {
            _eventMode.End("focus");
        }

        Publish();
    }

    private void BlockTimeUp()
    {
        if (_preset is null || _waiting || Paused)
        {
            return;
        }

        _waiting = true;
        _eventMode.End("focus");
        var done = Block!;
        var next = BlockAt(_index + 1);
        _pending.Add(new RuleEvent
        {
            Name = "focus.block_end",
            Id = "block-" + _blockStart.UtcTicks.ToString(CultureInfo.InvariantCulture),
            Fields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["block"] = Label(done),
                ["next"] = next is null ? "nothing, the preset is done" : Label(next) + " · " + next.Minutes + " min",
            },
        });
        Context.RulesDirty();
        Publish();
    }

    private static string Label(FocusBlockDef b) => b.Kind switch { "focus" => "Focus", "long_break" => "Long break", _ => "Break" };

    private void Advance(BlockEnd how)
    {
        if (_preset is null)
        {
            return;
        }

        Finish(how);
        _index++;
        if (Block is null)
        {
            Stop();
            return;
        }

        Begin(Context.Clock.UtcNow);
    }

    private void Extend()
    {
        if (_preset is null)
        {
            return;
        }

        if (_waiting)
        {
            _waiting = false;
            _until = Context.Clock.UtcNow.AddMinutes(5);
        }
        else if (_pausedLeft is { } left)
        {
            _pausedLeft = left + TimeSpan.FromMinutes(5);
            Publish();
            return;
        }
        else
        {
            _until = _until.AddMinutes(5);
        }

        Arm();
    }

    private void TogglePause()
    {
        if (_preset is null || _waiting)
        {
            return;
        }

        var now = Context.Clock.UtcNow;
        if (_pausedLeft is { } left)
        {
            _pausedLeft = null;
            _until = now + left;
            Arm();
            return;
        }

        _pausedLeft = _until - now;
        _wake?.Dispose();
        _eventMode.End("focus"); // a paused block is not focus time
        Publish();
    }

    private void Stop()
    {
        if (_preset is null)
        {
            return;
        }

        Finish(_waiting ? BlockEnd.Completed : BlockEnd.Stopped);
        _wake?.Dispose();
        _eventMode.End("focus");
        _preset = null;
        _waiting = false;
        _pausedLeft = null;
        Publish();
    }

    /// <summary>Record the current block if it was a focus block (E11.2), with C's numbers when tracking is on.</summary>
    private void Finish(BlockEnd how)
    {
        if (Block is not { Kind: "focus" } b)
        {
            return;
        }

        var end = _waiting ? _until : Context.Clock.UtcNow;
        var measures = TrackingOn() ? Measure(_blockStart, end, _intended) : null;
        var minutes = (int)Math.Round((end - _blockStart).TotalMinutes);
        _file.Blocks.Add(FocusReview.Result(_blockStart, _preset!.Name, minutes > 0 ? minutes : b.Minutes, how, _intended, measures));
        if (_file.Blocks.Count > FocusReview.Keep)
        {
            _file.Blocks.RemoveRange(0, _file.Blocks.Count - FocusReview.Keep);
        }

        _focusDone++;
        Context.Persist(Context.Store.Focus, JsonSerializer.SerializeToUtf8Bytes(_file, FocusJsonContext.Default.FocusFile));
    }

    private void Publish()
    {
        FocusState? state = _preset is null ? null : new FocusState
        {
            Preset = _preset.Name,
            Block = _waiting ? "done" : Block!.Kind,
            Until = _pausedLeft is { } left ? Context.Clock.UtcNow + left : _until,
            Paused = _pausedLeft is not null,
        };
        Context.UpdateState(s => s with { Focus = state });
        Context.RulesDirty();
    }

    public override void Contribute(RuleInputs inputs, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        inputs.State("focus_block", Running && Block?.Kind == "focus");
        inputs.Events.AddRange(_pending);
        _pending.Clear();
    }

    public string ReviewLine() => FocusReview.Compare(FocusReview.Stats(_file.Blocks), TrackingOn());

    public override IReadOnlyList<Card> Cards()
    {
        if (!Enabled)
        {
            return [];
        }

        var blocks = new List<Block>();
        if (_preset is null)
        {
            blocks.Add(new BadgeBlock { Text = Value.Literal("Ready: " + Context.Settings.FocusPresets[0].Name), Priority = 1 });
        }
        else if (_waiting)
        {
            blocks.Add(new BadgeBlock { Text = Value.Literal(Label(Block!) + " done"), Priority = 1 });
        }
        else if (_pausedLeft is { } left)
        {
            blocks.Add(new BadgeBlock { Text = Value.Literal("Paused · " + (int)Math.Ceiling(left.TotalMinutes) + " min left"), Priority = 1 });
        }
        else
        {
            blocks.Add(new CountdownBlock { Label = Label(Block!), Until = Value.Binding("/state/focus/until"), Priority = 1 });
        }

        if (_preset is not null)
        {
            blocks.Add(new BadgeBlock { Text = Value.Binding("/state/focus/preset"), Priority = 2 });
        }

        blocks.Add(new BadgeBlock { Text = Value.Literal(ReviewLine()), Priority = 4 });
        var excluded = FocusReview.Stats(_file.Blocks).Sum(s => s.Excluded);
        if (excluded > 0)
        {
            blocks.Add(new BadgeBlock { Text = Value.Literal(excluded + (excluded == 1 ? " block" : " blocks") + " left out: tracking was unsure"), Priority = 5 });
        }

        var pinned = _notes.PinnedLines();
        if (pinned.Count > 0)
        {
            blocks.Add(new ListBlock { Label = "Notes", Items = Value.FromRawJson(JsonSerializer.Serialize<IReadOnlyList<string>>(pinned, Contracts.ContractsJsonContext.Default.IReadOnlyListString)), Max = 4, Priority = 3 });
        }

        var actions = _preset is null
            ? new List<CardAction> { new() { Id = "start", Label = "Start", Action = "focus.start", Priority = 1 } }
            : _waiting
                ? [new() { Id = "next", Label = "Next", Action = "focus.next", Priority = 1 }, new() { Id = "extend", Label = "+5 min", Action = "focus.extend", Priority = 2 }, new() { Id = "stop", Label = "Stop", Action = "focus.stop", Priority = 3 }]
                : [new() { Id = "pause", Label = Paused ? "Resume" : "Pause", Action = "focus.pause", Priority = 1 }, new() { Id = "skip", Label = "Skip", Action = "focus.skip", Priority = 2 }, new() { Id = "stop", Label = "Stop", Action = "focus.stop", Priority = 3 }];
        return
        [
            new Card
            {
                Id = "module.focus",
                Source = CardKit.ModuleSource,
                Title = "Focus",
                Icon = IconKey.Focus,
                Blocks = blocks,
                Actions = actions,
            },
        ];
    }

    private static PreparedAction Do(string id, string message, Action act) => new()
    {
        Summary = id,
        Immediate = new ActionResult { Action = id, Status = ActionStatus.Ok, Message = message },
        Done = _ => act(),
    };

    private PreparedAction SaveNote(ActionRequest req)
    {
        var name = req.Args?.GetValueOrDefault("name");
        var text = req.Args?.GetValueOrDefault("text") ?? "";
        if (string.IsNullOrWhiteSpace(name))
        {
            return new PreparedAction { Summary = "Save note", Refusal = "A note needs a name" };
        }

        return new PreparedAction
        {
            Summary = "Save note",
            Run = _ =>
            {
                _notes.Save(name, text);
                return new ActionResult { Action = "note.save", Status = ActionStatus.Ok, Message = "Saved" };
            },
            Done = _ => Publish(),
        };
    }

    private PreparedAction NoteAction(ActionRequest req, string id, Action<string> act)
    {
        var name = req.Target?.GetValueOrDefault("note");
        if (string.IsNullOrWhiteSpace(name) || _notes.Read(name) is null)
        {
            return new PreparedAction { Summary = id, Refusal = "No such note" };
        }

        return new PreparedAction
        {
            Summary = id,
            Run = _ =>
            {
                act(name);
                return new ActionResult { Action = id, Status = ActionStatus.Ok };
            },
            Done = _ => Publish(),
        };
    }
}
