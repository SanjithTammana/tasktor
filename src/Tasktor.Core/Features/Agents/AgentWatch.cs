using System.Globalization;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Agents;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Snapshot;
using Tasktor.Core.Actions;
using Tasktor.Core.Apps;
using Tasktor.Core.Features.Jobs;
using Tasktor.Core.Rules;
using Tasktor.Core.Shapes;
using Tasktor.Core.Storage;
using AppsFeature = Tasktor.Core.Features.SystemMonitor.Apps;

namespace Tasktor.Core.Features.Agents;

/// <summary>One agent session row (estimated from its process, or reported by hooks).</summary>
public sealed class AgentSession
{
    public required string Id { get; init; }

    public required string Agent { get; set; }

    public ProcKey? Process { get; set; }

    public string? Folder { get; set; }

    public AgentSessionState State { get; set; } = AgentSessionState.Idle;

    public bool Reported { get; set; }

    public DateTimeOffset Since { get; set; }

    public double CoresNow { get; set; }

    public DateTimeOffset? LastBusyAt { get; set; }

    public double? LastCpuS { get; set; }

    public DateTimeOffset? LastCpuAt { get; set; }

    public int Children { get; set; }

    public bool Announced { get; set; }
}

/// <summary>
/// I1 AI agent watch (Module <c>agents</c>): one row per running agent session from the <c>agents</c> group (D-S3), busy /
/// idle estimated from its process (CPU used or a child spawned in the last 30 s), and working / needs-permission /
/// waiting / ended reported by hook files, which win for the same process. The names come from A's 60 s scan; while
/// agents run, one cheap read per agent every 15 s estimates busy. Q2: a session waiting for permission for 2 min whose
/// window is not in front raises <c>agent.state</c> for the default toast rule. Q1: hooks are written only on confirm.
/// </summary>
public sealed class AgentWatch : Feature
{
    public static readonly TimeSpan BusyWindow = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Sample = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan PermissionWait = TimeSpan.FromMinutes(2);
    public const double BusyCpuS = 0.2;

    private readonly AppsFeature _apps;
    private readonly AgentHookSource _hooks;
    private readonly Func<ProcKey, ProcessRow?> _readProcess;
    private readonly Func<ProcKey, string?> _commandLine;
    private readonly Dictionary<ProcKey, string?> _match = [];
    private readonly Dictionary<string, AgentSession> _sessions = new(StringComparer.Ordinal);
    private readonly List<RuleEvent> _pending = [];
    private Dictionary<uint, uint> _parents = [];
    private IDisposable? _scan;
    private IDisposable? _sampler;
    private IDisposable? _hookDebounce;
    private FileWatch? _watch;

    public AgentWatch(FeatureContext context, AppsFeature apps, AgentHookSource hooks, Func<ProcKey, ProcessRow?> readProcess, Func<ProcKey, string?> commandLine)
        : base(context)
    {
        _apps = apps;
        _hooks = hooks;
        _readProcess = readProcess;
        _commandLine = commandLine;
    }

    public override string Id => "agents";

    public override FeatureTier Tier => FeatureTier.Module;

    /// <summary>The <c>agents</c> group from the rules file (D-S3: one list, owned by the rules file).</summary>
    public Func<IReadOnlyList<string>> AgentGroup { get; set; } = () => [];

    /// <summary>B2.5's check: is any of these processes the foreground window's?</summary>
    public Func<IReadOnlyList<uint>, bool>? WindowInFront { get; set; }

    /// <summary>Bring a process tree's window to the front (Windows adapter). Returns false when it could not.</summary>
    public Func<IReadOnlyList<uint>, bool>? ShowWindow { get; set; }

    /// <summary>Claude Code's settings file (from the user profile Known Folder); null when unknown.</summary>
    public Func<string?> ClaudeSettingsPath { get; set; } = () => null;

    public IReadOnlyCollection<AgentSession> Sessions => _sessions.Values;

    public long Samples { get; private set; }

    public bool Sampling => _sampler is not null;

    protected override void OnEnable()
    {
        _apps.Sampled += OnTable;
        _scan = Context.Scheduler.Lease(AppsFeature.SamplerName, SystemMonitor.SystemTotals.Tick, ColumnSet.Names, holder: "agents");
        _watch = new FileWatch([_hooks.Directory], () => Context.Loop.Post(HooksChanged), folders: true);
        Context.Router.Register("agent.show", Show);
        Context.Router.Register("agent.lower_newest", LowerNewest);
        Context.Router.Register("agent.hooks.install", req => Hooks(req, install: true));
        Context.Router.Register("agent.hooks.remove", req => Hooks(req, install: false));
        ReadHooks();
    }

    protected override void OnDisable()
    {
        _apps.Sampled -= OnTable;
        _scan?.Dispose();
        _sampler?.Dispose();
        _sampler = null;
        _watch?.Dispose();
        _hookDebounce?.Dispose();
        _sessions.Clear();
        _match.Clear();
        Context.UpdateState(s => s with { Agents = null });
    }

    // ---------------- processes ----------------

    private void OnTable(KeyedTable<ProcKey, ProcessRow> table, ColumnSet columns)
    {
        if ((columns & ColumnSet.Names) != 0)
        {
            Scan(table.Rows.Values.ToList(), Context.Clock.UtcNow);
        }
    }

    /// <summary>Which agent (group entry) a process is, cached per process: exe glob, or a script host + script basename.</summary>
    public string? AgentOf(ProcessRow r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (_match.TryGetValue(r.Key, out var hit))
        {
            return hit;
        }

        string? agent = null;
        foreach (var entry in AgentGroup())
        {
            if (entry.Contains('.', StringComparison.Ordinal) || entry.Contains('*', StringComparison.Ordinal))
            {
                if (Glob.IsMatch(entry, r.Exe))
                {
                    agent = Path.GetFileNameWithoutExtension(entry).TrimEnd('*');
                    break;
                }
            }
            else if (IsScriptHost(r.Exe) && string.Equals(Path.GetFileNameWithoutExtension(JobGate.Script(_commandLine(r.Key))), entry, StringComparison.OrdinalIgnoreCase))
            {
                agent = entry;
                break;
            }
        }

        _match[r.Key] = agent;
        return agent;
    }

    private static bool IsScriptHost(string exe) =>
        exe.StartsWith("python", StringComparison.OrdinalIgnoreCase) || exe.StartsWith("node", StringComparison.OrdinalIgnoreCase)
        || exe.Equals("bun.exe", StringComparison.OrdinalIgnoreCase) || exe.Equals("deno.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>One pass over the process names (tests call it directly).</summary>
    public void Scan(IReadOnlyList<ProcessRow> rows, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var byPid = rows.GroupBy(r => r.Key.Pid).ToDictionary(g => g.Key, g => g.First());
        _parents = byPid.ToDictionary(kv => kv.Key, kv => kv.Value.ParentPid);
        var live = new HashSet<ProcKey>();
        foreach (var r in rows)
        {
            if (AgentOf(r) is not { } agent)
            {
                continue;
            }

            // Only the topmost agent process of a tree is a session (an agent's own helpers fold in).
            if (byPid.TryGetValue(r.ParentPid, out var parent) && parent.Key.Pid != r.Key.Pid && AgentOf(parent) is not null)
            {
                continue;
            }

            live.Add(r.Key);
            var id = "p" + r.Key.Pid.ToString(CultureInfo.InvariantCulture) + "-" + r.Key.CreatedTicks.ToString(CultureInfo.InvariantCulture);
            if (!_sessions.TryGetValue(id, out var s))
            {
                _sessions[id] = s = new AgentSession { Id = id, Agent = agent, Process = r.Key, Since = now };
            }

            var children = rows.Count(c => c.ParentPid == r.Key.Pid && c.Key.Pid != r.Key.Pid);
            if (children > s.Children)
            {
                s.LastBusyAt = now; // spawned a child since the last scan
            }

            s.Children = children;
        }

        foreach (var gone in _sessions.Values.Where(s => !s.Reported && s.Process is { } k && !live.Contains(k)).Select(s => s.Id).ToList())
        {
            _sessions.Remove(gone);
        }

        foreach (var k in _match.Keys.Where(k => !byPid.ContainsKey(k.Pid)).ToList())
        {
            _match.Remove(k);
        }

        ExpireReported(byPid, now);
        UpdateSampler();
        Recompute(now);
    }

    private void UpdateSampler()
    {
        var any = _sessions.Values.Any(s => s.Process is not null);
        if (any && _sampler is null)
        {
            _sampler = Context.Loop.Every(Sample, SampleCpu);
            SampleCpu();
        }
        else if (!any && _sampler is not null)
        {
            _sampler.Dispose();
            _sampler = null;
        }
    }

    /// <summary>Every 15 s while agents run: one CPU-time read per agent process, off the loop.</summary>
    public void SampleCpu()
    {
        var keys = _sessions.Values.Where(s => s.Process is not null).Select(s => s.Process!.Value).Distinct().ToList();
        var at = Context.Clock.UtcNow; // the reading's time, not the time it is applied
        Samples++;
        Context.OffLoop(() =>
        {
            var reads = keys.Select(k => (k, _readProcess(k))).ToList();
            Context.Loop.Post(() => ApplyCpu(reads, at));
        });
    }

    private void ApplyCpu(List<(ProcKey Key, ProcessRow? Row)> reads, DateTimeOffset now)
    {
        foreach (var (key, row) in reads)
        {
            foreach (var s in _sessions.Values.Where(s => s.Process == key))
            {
                if (row is null)
                {
                    continue;
                }

                if (s.LastCpuS is { } prev && s.LastCpuAt is { } at && now > at)
                {
                    var used = row.CpuTimeS - prev;
                    s.CoresNow = Math.Max(0, used / (now - at).TotalSeconds);
                    if (used >= BusyCpuS)
                    {
                        s.LastBusyAt = now;
                    }
                }

                s.LastCpuS = row.CpuTimeS;
                s.LastCpuAt = now;
            }
        }

        Recompute(now);
    }

    // ---------------- hooks ----------------

    private void HooksChanged()
    {
        _hookDebounce ??= Context.Loop.At(Context.Clock.UtcNow + TimeSpan.FromMilliseconds(250), () =>
        {
            _hookDebounce = null;
            ReadHooks();
        });
    }

    public void ReadHooks() => Context.OffLoop(() =>
    {
        var rows = new List<KeyValuePair<string, AgentHookFile>>();
        _hooks.Read(rows, ColumnSet.None);
        Context.Loop.Post(() => ApplyHooks(rows.Select(r => r.Value).ToList(), Context.Clock.UtcNow));
    });

    public void ApplyHooks(IReadOnlyList<AgentHookFile> files, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(files);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            var id = "h" + f.SessionId;
            seen.Add(id);
            if (!_sessions.TryGetValue(id, out var s))
            {
                _sessions[id] = s = new AgentSession { Id = id, Agent = f.Agent, Reported = true, Since = f.Since };
            }

            if (s.State != f.State)
            {
                s.Announced = false;
            }

            s.State = f.State;
            s.Since = f.Since;
            s.Folder = f.Folder;
            s.Agent = f.Agent;

            // Reported state wins for the same process: by pid, else the single running process of that agent.
            var proc = f.Pid is { } pid
                ? _sessions.Values.FirstOrDefault(p => !p.Reported && p.Process?.Pid == pid)
                : _sessions.Values.Where(p => !p.Reported && string.Equals(p.Agent, f.Agent, StringComparison.OrdinalIgnoreCase)).ToList() is { Count: 1 } one ? one[0] : null;
            if (proc is not null)
            {
                s.Process = proc.Process;
                s.CoresNow = proc.CoresNow;
                _sessions.Remove(proc.Id);
            }
        }

        foreach (var gone in _sessions.Values.Where(s => s.Reported && !seen.Contains(s.Id)).Select(s => s.Id).ToList())
        {
            _sessions.Remove(gone);
        }

        Recompute(now);
    }

    /// <summary>Ended sessions go after a minute; a reported session whose process is gone (or never named) expires.</summary>
    private void ExpireReported(Dictionary<uint, ProcessRow> byPid, DateTimeOffset now)
    {
        foreach (var s in _sessions.Values.Where(s => s.Reported).ToList())
        {
            var processGone = s.Process is { } k && (!byPid.TryGetValue(k.Pid, out var r) || (k.CreatedTicks != 0 && r.Key.CreatedTicks != k.CreatedTicks));
            var ended = s.State == AgentSessionState.Ended && now - s.Since > TimeSpan.FromMinutes(1);
            var stale = s.Process is null && now - s.Since > TimeSpan.FromHours(12);
            if (processGone || ended || stale)
            {
                _sessions.Remove(s.Id);
                var session = s.Id[1..];
                Context.OffLoop(() => _hooks.Delete(session));
            }
        }
    }

    // ---------------- state, rules, card ----------------

    private void Recompute(DateTimeOffset now)
    {
        foreach (var s in _sessions.Values.Where(s => !s.Reported))
        {
            s.State = s.LastBusyAt is { } b && now - b <= BusyWindow ? AgentSessionState.Busy : AgentSessionState.Idle;
        }

        if (Context.Settings.Current.Agents?.PermissionToast != false)
        {
            foreach (var s in _sessions.Values.Where(s => s.Reported && s.State == AgentSessionState.NeedsPermission && !s.Announced && now - s.Since >= PermissionWait))
            {
                if (s.Process is { } k && WindowInFront?.Invoke(Chain(k.Pid)) == true)
                {
                    continue; // the user is looking at it
                }

                s.Announced = true;
                _pending.Add(new RuleEvent
                {
                    Name = "agent.state",
                    Id = s.Id + "@" + s.Since.UtcTicks.ToString(CultureInfo.InvariantCulture),
                    Fields = new Dictionary<string, string>(StringComparer.Ordinal) { ["agent"] = Display(s.Agent), ["folder"] = s.Folder ?? "a session", ["state"] = "needs-permission" },
                    Target = CardKit.Target("session", s.Id),
                });
            }
        }

        var rows = Ordered().Select(s => new AgentSessionRow
        {
            Agent = Display(s.Agent),
            Folder = s.Folder,
            State = s.State,
            Reported = s.Reported,
            Since = s.Since,
            CoresNow = s.Process is null ? null : Math.Round(s.CoresNow, 2),
        }).ToList();
        Context.UpdateState(st => st with { Agents = rows.Count == 0 ? null : rows });
        Context.RulesDirty();

        // Re-check the 2 min wait without ticking: one wake at the earliest due time.
        var due = _sessions.Values.Where(s => s.Reported && s.State == AgentSessionState.NeedsPermission && !s.Announced).Select(s => s.Since + PermissionWait).Where(t => t > now).Order().FirstOrDefault();
        if (due != default)
        {
            Context.Loop.At(due, () => Recompute(Context.Clock.UtcNow));
        }
    }

    public IEnumerable<AgentSession> Ordered() => _sessions.Values.OrderBy(s => s.State switch
    {
        AgentSessionState.NeedsPermission => 0,
        AgentSessionState.Waiting => 1,
        AgentSessionState.Working => 2,
        AgentSessionState.Busy => 3,
        AgentSessionState.Idle => 4,
        _ => 5,
    }).ThenBy(s => s.Since);

    private static string Display(string agent) => agent.Length == 0 ? agent : char.ToUpperInvariant(agent[0]) + agent[1..];

    private List<uint> Chain(uint pid)
    {
        var chain = new List<uint> { pid };
        var cur = pid;
        for (var i = 0; i < 8 && _parents.TryGetValue(cur, out var p) && p != 0 && !chain.Contains(p); i++)
        {
            chain.Add(p);
            cur = p;
        }

        return chain;
    }

    public override void Contribute(RuleInputs inputs, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        inputs.State("agent_needs_permission", _sessions.Values.Any(s => s.State == AgentSessionState.NeedsPermission));
        var busy = _sessions.Values.Count(s => s.State is AgentSessionState.Busy or AgentSessionState.Working);
        inputs.Groups["agents"] = new GroupStatus(_sessions.Values.Count(s => s.Process is not null || s.State != AgentSessionState.Ended), busy);
        inputs.Events.AddRange(_pending);
        _pending.Clear();
    }

    public override IReadOnlyList<Card> Cards()
    {
        if (!Enabled)
        {
            return [];
        }

        var waiting = _sessions.Values.Count(s => s.State is AgentSessionState.NeedsPermission or AgentSessionState.Waiting);
        return
        [
            new Card
            {
                Id = "module.agents",
                Source = CardKit.ModuleSource,
                Title = "Agents",
                Icon = IconKey.Agent,
                Severity = Value.Literal(_sessions.Values.Any(s => s.State == AgentSessionState.NeedsPermission) ? "warn" : "normal"),
                Blocks =
                [
                    new MetricBlock { Label = "Waiting for you", Value = Value.Literal(waiting), Priority = 1 },
                    new TableBlock
                    {
                        Label = "Sessions",
                        Columns =
                        [
                            new TableColumn { Key = "agent", Label = "Agent" },
                            new TableColumn { Key = "folder", Label = "Folder" },
                            new TableColumn { Key = "state", Label = "State" },
                            new TableColumn { Key = "since", Label = "Since" },
                            new TableColumn { Key = "cores_now", Label = "CPU", Unit = "cores" },
                        ],
                        Rows = Value.Binding("/state/agents"),
                        Max = 8,
                        Priority = 2,
                    },
                ],
                Actions =
                [
                    new CardAction { Id = "show", Label = "Show window", Action = "agent.show", Target = CardKit.Target("session", "{row}"), Priority = 1 },
                    // I1 Q1: exact states come from hooks written into Claude Code's settings.json only on confirm; the confirm
                    // step shows the diff and keeps a .bak. Both are destructive, so both are two-step.
                    new CardAction { Id = "hooks", Label = "Set up hooks", Action = "agent.hooks.install", Destructive = true, Priority = 3 },
                    new CardAction { Id = "unhook", Label = "Remove hooks", Action = "agent.hooks.remove", Destructive = true, Priority = 4 },
                ],
            },
        ];
    }

    // ---------------- actions ----------------

    private AgentSession? Find(ActionRequest req)
    {
        var id = req.Target?.GetValueOrDefault("session");
        return id is null ? null : _sessions.GetValueOrDefault(id) ?? _sessions.Values.FirstOrDefault(s => s.Id.EndsWith(id, StringComparison.Ordinal));
    }

    private PreparedAction Show(ActionRequest req)
    {
        if (Find(req) is not { Process: { } k })
        {
            return new PreparedAction { Summary = "Show window", Refusal = "That session has no window Tasktor can find" };
        }

        var chain = Chain(k.Pid);
        return new PreparedAction
        {
            Summary = "Show window",
            Run = _ => new ActionResult { Action = "agent.show", Status = ShowWindow?.Invoke(chain) == true ? ActionStatus.Ok : ActionStatus.Failed },
        };
    }

    private PreparedAction LowerNewest(ActionRequest req)
    {
        var newest = _sessions.Values.Where(s => s.Process is not null && s.State is AgentSessionState.Busy or AgentSessionState.Working)
            .MaxBy(s => s.Process!.Value.CreatedTicks);
        if (newest is null)
        {
            return new PreparedAction { Summary = "Lower the newest agent", Refusal = "No agent is busy now" };
        }

        var target = new Dictionary<string, string>(StringComparer.Ordinal) { ["pid"] = newest.Process!.Value.Pid.ToString(CultureInfo.InvariantCulture) };
        return Context.Router.PrepareWith("priority.set", req with { Target = target, Args = CardKit.Target("priority", "below_normal") });
    }

    private PreparedAction Hooks(ActionRequest req, bool install)
    {
        var verb = install ? "Set up hooks" : "Remove hooks";
        if (ClaudeSettingsPath() is not { } path)
        {
            return new PreparedAction { Summary = verb, Refusal = "Claude Code's settings folder was not found" };
        }

        string? text;
        HookChange change;
        try
        {
            text = File.Exists(path) ? File.ReadAllText(path) : null;
            change = install ? HookInstaller.Install(text) : HookInstaller.Remove(text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            return new PreparedAction { Summary = verb, Refusal = "Claude Code's settings file could not be read as JSON; nothing was changed" };
        }

        if (change.AlreadyInstalled)
        {
            return new PreparedAction { Summary = verb, Immediate = new ActionResult { Action = req.Action, Status = ActionStatus.Ok, Message = install ? "Hooks are already set up" : "No Tasktor hooks to remove" } };
        }

        return new PreparedAction
        {
            // The confirm step shows exactly this change (Q1).
            Summary = verb + " in Claude Code's settings.json (a .bak is kept):\n" + HookInstaller.Summary(change.Diff),
            Run = _ =>
            {
                if (text is not null)
                {
                    File.WriteAllText(path + ".bak", text);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                AtomicFile.Write(path, System.Text.Encoding.UTF8.GetBytes(change.NewText));
                return new ActionResult { Action = req.Action, Status = ActionStatus.Ok, Message = install ? "Hooks set up; Remove takes them out again" : "Tasktor's hooks removed" };
            },
        };
    }
}
