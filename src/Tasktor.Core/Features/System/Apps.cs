using System.Globalization;
using System.Text.Json.Nodes;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Pipe;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Snapshot;
using Tasktor.Core.Actions;
using Tasktor.Core.Apps;
using Tasktor.Core.Loop;
using Tasktor.Core.Shapes;

namespace Tasktor.Core.Features.SystemMonitor;

/// <summary>
/// A2 processes grouped by app + A3 per-process actions and guards. Per-process data is read only while someone holds a
/// lease on the <c>procs</c> sampler (a viewer at 1–2 s, a pressure escalation at 5 s, B's names-only 60 s scan) and its
/// buffers are freed when the last lease goes (D-A3). CPU % comes from a <see cref="RateCounter"/> per (PID, creation time).
/// </summary>
public sealed class Apps : Feature
{
    public const string SamplerName = "procs";

    private readonly ITableSource<ProcKey, ProcessRow> _source;
    private readonly AppIdentity _identity;
    private readonly AppGrouper _grouper;
    private readonly KeyedTable<ProcKey, ProcessRow> _table = new();
    private readonly Dictionary<ProcKey, RateCounter> _cpu = [];
    private readonly Dictionary<ProcKey, double> _cpuPct = [];
    private readonly Dictionary<ProcKey, ProcessPriority> _originalPriority = [];
    private readonly HashSet<ProcKey> _eco = [];
    private List<AppGroup> _groups = [];

    public Apps(FeatureContext context, ITableSource<ProcKey, ProcessRow> source, AppIdentity identity, Func<string, IReadOnlyList<string>?> groups)
        : base(context)
    {
        _source = source;
        _identity = identity;
        _grouper = new AppGrouper(identity);
        Targets = new ProcessTargets(() => _table.Rows, id => _groups.FirstOrDefault(g => g.AppId == id), groups);
    }

    public override string Id => "apps";

    public override FeatureTier Tier => FeatureTier.Core;

    public ProcessTargets Targets { get; }

    /// <summary>The rules file's process groups (D-H6 <c>group</c> conditions count their running processes).</summary>
    public Func<IReadOnlyDictionary<string, IReadOnlyList<string>>> RuleGroups { get; set; } = () => new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>Running processes per rule group, from the latest names scan (busy is B's / I1's, added by them).</summary>
    public override void Contribute(Rules.RuleInputs inputs, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        foreach (var (name, globs) in RuleGroups())
        {
            if (globs.Count == 0)
            {
                inputs.Groups[name] = new Rules.GroupStatus(0, 0);
                continue;
            }

            var count = _table.Rows.Values.Count(r => globs.Any(g => Glob.IsMatch(g, r.Exe)));
            inputs.Groups[name] = new Rules.GroupStatus(count, 0);
        }
    }

    public IReadOnlyDictionary<ProcKey, ProcessRow> Table => _table.Rows;

    public IReadOnlyList<AppGroup> Groups => _groups;

    /// <summary>CPU % of the whole machine per process (last usage sample).</summary>
    public IReadOnlyDictionary<ProcKey, double> CpuPct => _cpuPct;

    /// <summary>Raised on the loop after each table sample with the columns read and the diff (B's job watch, C's start/stop).</summary>
    public event Action<KeyedTable<ProcKey, ProcessRow>, ColumnSet>? Sampled;

    public ProcessRow? Find(ProcKey key) => _table.TryGet(key, out var r) ? r : null;

    protected override void OnEnable()
    {
        if (!Context.Scheduler.IsRegistered(SamplerName))
        {
            Context.Scheduler.Register(SamplerName, Read, ReleaseBuffers);
        }

        Context.RegisterPull("processes", PullProcesses);
        RegisterHandlers();
    }

    protected override void OnDisable() => ReleaseBuffers();

    public void Read(ColumnSet columns)
    {
        Context.OffLoop(() =>
        {
            var rows = new List<KeyValuePair<ProcKey, ProcessRow>>(640);
            _source.Read(rows, columns);
            var at = Context.Clock.UtcNow;
            Context.Loop.Post(() => Ingest(rows, columns, at));
        });
    }

    /// <summary>Apply one table read (loop-only; tests call it directly).</summary>
    public void Ingest(IReadOnlyList<KeyValuePair<ProcKey, ProcessRow>> rows, ColumnSet columns, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (Context.Scheduler.IsRegistered(SamplerName) && Context.Scheduler.LeaseCount(SamplerName) == 0)
        {
            return; // a read that finished after its last lease went: its buffers were already freed (D-A3)
        }

        _table.Apply(rows);
        foreach (var gone in _table.Removed)
        {
            _cpu.Remove(gone.Key);
            _cpuPct.Remove(gone.Key);
            _originalPriority.Remove(gone.Key);
            _eco.Remove(gone.Key);
        }

        if ((columns & ColumnSet.Usage) != 0)
        {
            var cores = Math.Max(1, Environment.ProcessorCount);
            foreach (var kv in _table.Rows)
            {
                if (!_cpu.TryGetValue(kv.Key, out var rc))
                {
                    _cpu[kv.Key] = rc = new RateCounter();
                }

                var rate = rc.Update(kv.Value.CpuTimeS, at);
                _cpuPct[kv.Key] = rate is { } r ? Math.Clamp(100 * r / cores, 0, 100) : 0;
            }

            var list = _table.Rows.Values.ToList();
            var cpu = list.Select(r => _cpuPct.GetValueOrDefault(r.Key)).ToArray();
            _groups = _grouper.Group(list, cpu);
            PublishApps();
        }

        Sampled?.Invoke(_table, columns);
    }

    /// <summary>The resource under most pressure decides the order (A2: else CPU).</summary>
    public string PressureResource()
    {
        var s = Context.State().System;
        var t = Context.Settings;
        var best = ("cpu", 0.0);
        foreach (var (res, v) in new[] { ("ram", s?.RamUsedPct), ("gpu", s?.Gpu is { Count: > 0 } g ? g.Max(x => x.Pct) : null), ("cpu", s?.CpuPct) })
        {
            if (v is { } value && value >= t.ThresholdFor(res).Warn && value / t.ThresholdFor(res).Warn > best.Item2)
            {
                best = (res, value / t.ThresholdFor(res).Warn);
            }
        }

        return best.Item1;
    }

    public IReadOnlyList<AppGroup> TopGroups(int n, string resource) =>
        TopN<AppGroup>.Of(_groups.Where(g => !g.IsSystem), n, Comparer<AppGroup>.Create((a, b) => Key(a, resource).CompareTo(Key(b, resource))));

    /// <summary>A2.7: one line naming the saturated resource and the top app group, e.g. "RAM 92%: Chrome 3.1 GB".</summary>
    public (string Text, Severity Severity)? Verdict(SystemState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var t = Context.Settings;
        (string Res, string Label, double Value, Severity Sev)? worst = null;
        foreach (var (res, label, v) in new[] { ("ram", "RAM", s.RamUsedPct), ("cpu", "CPU", s.CpuPct), ("gpu", "GPU", s.Gpu is { Count: > 0 } g ? g.Max(x => x.Pct) : null), ("disk_active", "Disk", s.Disks is { Count: > 0 } d ? d.Max(x => x.ActivePct) : null) })
        {
            if (v is not { } value)
            {
                continue;
            }

            var sev = SeverityHold.Classify(value, t.ThresholdFor(res));
            if (sev != Severity.Normal && (worst is null || sev > worst.Value.Sev || (sev == worst.Value.Sev && value / t.ThresholdFor(res).Warn > worst.Value.Value / t.ThresholdFor(worst.Value.Res).Warn)))
            {
                worst = (res, label, value, sev);
            }
        }

        if (worst is { } w)
        {
            var text = w.Label + " " + Math.Round(w.Value).ToString(CultureInfo.InvariantCulture) + "%";
            if (w.Res != "disk_active" && TopGroups(1, w.Res).FirstOrDefault() is { } top)
            {
                text += ": " + top.Name + " " + Amount(top, w.Res);
            }

            return (text, w.Sev);
        }

        if (s.Volumes?.FirstOrDefault(v => v.Low == true) is { } low)
        {
            return ((low.Letter ?? low.Id) + " " + Math.Round(low.FreeGb).ToString(CultureInfo.InvariantCulture) + " GB free (" + Math.Round(low.FreePct).ToString(CultureInfo.InvariantCulture) + "%)", Severity.Warn);
        }

        return null;
    }

    public override IReadOnlyList<Card> Cards() =>
    [
        new Card
        {
            Id = "core.apps",
            Source = CardKit.Core,
            Title = "Apps",
            Icon = IconKey.Apps,
            Blocks =
            [
                new TableBlock
                {
                    Columns = [new TableColumn { Key = "name", Label = "App" }, new TableColumn { Key = "cpu_pct", Label = "CPU", Unit = "%" }, new TableColumn { Key = "private_mb", Label = "RAM", Unit = "MB" }, new TableColumn { Key = "gpu_pct", Label = "GPU", Unit = "%" }],
                    Rows = Value.Binding("/state/apps"),
                    Max = 5,
                    RowKey = "app_id",
                    Priority = 1,
                },
                new TreeBlock { Label = "Processes", Table = "processes", Priority = 9 },
            ],
            Actions =
            [
                new CardAction { Id = "close", Label = "Close", Action = "app.close", Target = CardKit.Target("app", "{row}"), Priority = 1, Destructive = true },
                new CardAction { Id = "lower", Label = "Lower", Action = "priority.set", Target = CardKit.Target("app", "{row}"), Args = CardKit.Target("priority", "below_normal"), Priority = 2 },
                new CardAction { Id = "eco", Label = "Efficiency", Action = "efficiency.set", Target = CardKit.Target("app", "{row}"), Priority = 3 },
                new CardAction { Id = "end", Label = "End…", Action = "app.end", Target = CardKit.Target("app", "{row}"), Priority = 4, Destructive = true },
            ],
            Link = "tasktor://open?card=core.apps",
            StaleAfterS = 30,
        },
    ];

    private void PublishApps()
    {
        var resource = PressureResource();
        var rows = TopGroups(5, resource).Select(g => new AppRow
        {
            AppId = g.AppId,
            Name = g.Name,
            CpuPct = Math.Round(g.CpuPct, 1),
            PrivateMb = Math.Round(g.PrivateBytes / (1024 * 1024)),
            GpuPct = g.GpuPct is { } gp ? Math.Round(gp, 1) : null,
            Count = g.Count,
            Admin = g.AnyElevated ? true : null,
        }).ToList();
        Context.UpdateState(s => s with { Apps = rows });
    }

    private void ReleaseBuffers()
    {
        _table.Clear();
        _cpu.Clear();
        _cpuPct.Clear();
        _groups = [];
        Context.UpdateState(s => s.Apps is null ? s : s with { Apps = null });
    }

    private static double Key(AppGroup g, string resource) => resource switch
    {
        "ram" => g.PrivateBytes,
        "gpu" => g.GpuPct ?? 0,
        _ => g.CpuPct,
    };

    private static string Amount(AppGroup g, string resource) => resource switch
    {
        "ram" => (g.PrivateBytes / (1024.0 * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " GB",
        "gpu" => Math.Round(g.GpuPct ?? 0).ToString(CultureInfo.InvariantCulture) + "%",
        _ => Math.Round(g.CpuPct).ToString(CultureInfo.InvariantCulture) + "%",
    };

    private JsonNode? PullProcesses(PullMessage pull)
    {
        var arr = new JsonArray();
        foreach (var g in _groups.OrderByDescending(g => g.CpuPct))
        {
            var procs = new JsonArray();
            foreach (var k in g.Members)
            {
                if (_table.TryGet(k, out var r))
                {
                    procs.Add((JsonNode)new JsonObject
                    {
                        ["pid"] = r.Key.Pid,
                        ["created"] = r.Key.CreatedTicks.ToString(CultureInfo.InvariantCulture),
                        ["exe"] = r.Exe,
                        ["cpu_pct"] = Math.Round(_cpuPct.GetValueOrDefault(k), 1),
                        ["private_mb"] = Math.Round(r.PrivateBytes / (1024 * 1024)),
                        ["priority"] = r.Priority.ToString(),
                        ["admin"] = r.Elevated,
                        ["locked"] = Locked(r),
                    });
                }
            }

            arr.Add((JsonNode)new JsonObject
            {
                ["app_id"] = g.AppId,
                ["name"] = g.Name,
                ["cpu_pct"] = Math.Round(g.CpuPct, 1),
                ["private_mb"] = Math.Round(g.PrivateBytes / (1024 * 1024)),
                ["count"] = g.Count,
                ["system"] = g.IsSystem,
                ["processes"] = procs,
            });
        }

        return arr;
    }

    /// <summary>Why a row is greyed (A3: never fail on click; say why first).</summary>
    public static string? Locked(ProcessRow r) =>
        r.Protected ? "Protected by Windows"
        : r.Elevated ? "Runs as admin — use Task Manager"
        : !r.OwnUser ? "Another user's process"
        : AppIdentity.IsCritical(r.Exe) ? "Critical Windows process"
        : null;

    private void RegisterHandlers()
    {
        var router = Context.Router;
        PreparedAction ProcessAction(ActionRequest req, string verb, Func<IReadOnlyList<ProcKey>, IReadOnlyList<ProcessOutcome>> run)
        {
            var t = Targets.Resolve(req.Target);
            if (t is null || t.Keys.Count == 0)
            {
                return new PreparedAction { Summary = verb, Refusal = "No such process or it already exited" };
            }

            var count = t.Keys.Count;
            return new PreparedAction
            {
                Summary = verb + " " + t.Name + (count > 1 ? " (" + count + ")" : string.Empty) + "?",
                TargetName = t.Exe ?? t.Name,
                Processes = t.Keys,
                Run = keys => Summarize(req.Action, verb, t.Name, count, run(keys)),
            };
        }

        IProcessControl Pc() => Context.Processes ?? throw new InvalidOperationException("no process control");

        router.Register("proc.end", req => ProcessAction(req, "End", k => Pc().End(k)));
        router.Register("proc.end_tree", req => ProcessAction(req with { Target = TreeOf(req.Target) }, "End", k => Pc().End(k)));
        router.Register("app.end", req => ProcessAction(req, "End", k => Pc().End(k)));
        router.Register("proc.close", req => ProcessAction(req, "Close", k => Pc().Close(k)));
        router.Register("app.close", req => ProcessAction(req, "Close", k => Pc().Close(k)));
        router.Register("proc.freeze", req => ProcessAction(req, "Freeze", k => Pc().Freeze("app:" + (req.Target?.GetValueOrDefault("app") ?? req.Target?.GetValueOrDefault("pid")), k)));
        router.Register("proc.resume", req => ProcessAction(req, "Resume", k => Pc().Thaw("app:" + (req.Target?.GetValueOrDefault("app") ?? req.Target?.GetValueOrDefault("pid")), k)));
        router.Register("efficiency.set", req => ProcessAction(req, "Efficiency mode for", k =>
        {
            var r = Pc().SetEfficiency(k, true);
            Context.Loop.Post(() => _eco.UnionWith(r.Where(o => o.Status == ActionStatus.Ok).Select(o => o.Key)));
            return r;
        }));
        router.Register("efficiency.restore", req => ProcessAction(req, "Efficiency mode off for", k => Pc().SetEfficiency(k, false)));
        router.Register("priority.set", req =>
        {
            var prio = req.Args?.GetValueOrDefault("priority") switch { "idle" => ProcessPriority.Idle, "normal" => ProcessPriority.Normal, _ => ProcessPriority.BelowNormal };
            return ProcessAction(req, "Lower", k =>
            {
                var results = Pc().SetPriority(k, prio);
                Context.Loop.Post(() =>
                {
                    foreach (var (o, prev) in results)
                    {
                        if (o.Status == ActionStatus.Ok)
                        {
                            _originalPriority.TryAdd(o.Key, prev); // remembered at the first change (B3.3)
                        }
                    }
                });
                return results.Select(r => r.Outcome).ToList();
            });
        });
        router.Register("priority.restore", req =>
        {
            var t = Targets.Resolve(req.Target);
            if (t is null || t.Keys.Count == 0)
            {
                return new PreparedAction { Summary = "Restore", Refusal = "No such process or it already exited" };
            }

            var restore = t.Keys.GroupBy(k => _originalPriority.GetValueOrDefault(k, ProcessPriority.Normal)).ToList();
            return new PreparedAction
            {
                Summary = "Restore " + t.Name,
                TargetName = t.Exe ?? t.Name,
                Processes = t.Keys,
                Run = keys => Summarize(req.Action, "Restore", t.Name, keys.Count, restore.SelectMany(g => Pc().SetPriority(g.Where(keys.Contains).ToList(), g.Key).Select(x => x.Outcome)).ToList()),
                Done = r =>
                {
                    foreach (var k in t.Keys)
                    {
                        _originalPriority.Remove(k);
                    }
                },
            };
        });
        router.Register("proc.open_location", req =>
        {
            var t = Targets.Resolve(req.Target);
            var path = t?.Keys.Select(k => _table.TryGet(k, out var r) ? r.Path : null).FirstOrDefault(p => p is not null);
            return path is null
                ? new PreparedAction { Summary = "Open file location", Refusal = "The path is not readable" }
                : new PreparedAction { Summary = "Open file location", TargetName = t!.Exe, Run = _ => Launched(req.Action, Context.Launch("select:" + path)) };
        });
        router.Register("proc.open_taskmgr", req => new PreparedAction { Summary = "Open Task Manager", Run = _ => Launched(req.Action, Context.Launch("taskmgr")) });
    }

    private Dictionary<string, string>? TreeOf(IReadOnlyDictionary<string, string>? target)
    {
        if (target is null || !target.TryGetValue("pid", out var pidText) || !uint.TryParse(pidText, out var pid))
        {
            return target is null ? null : new Dictionary<string, string>(target, StringComparer.Ordinal);
        }

        // The row the button came from must still be that process: a pid reused since then is another program, and its
        // tree is not the one the user meant (F-AR2). Left as pid + created, the identity guard refuses it.
        var root = _table.Rows.Values.FirstOrDefault(r => r.Key.Pid == pid);
        if (root is null || (target.TryGetValue("created", out var c) && long.TryParse(c, out var created) && created != root.Key.CreatedTicks))
        {
            return new Dictionary<string, string>(target, StringComparer.Ordinal);
        }

        // End tree (A3.2): the process and every descendant, from the snapshot, each with its creation time so every key is
        // re-checked before it is ended; ended leaf-first.
        var children = _table.Rows.Values.GroupBy(r => r.ParentPid).ToDictionary(g => g.Key, g => g.ToList());
        var all = new List<ProcKey> { root.Key };
        for (var i = 0; i < all.Count; i++)
        {
            foreach (var ch in children.GetValueOrDefault(all[i].Pid) ?? [])
            {
                if (ch.Key.CreatedTicks >= all[i].CreatedTicks && !all.Contains(ch.Key)) // a "child" older than its parent is a reused parent pid
                {
                    all.Add(ch.Key);
                }
            }
        }

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["pids"] = string.Join(",", all.Select(k => k.Pid + ":" + k.CreatedTicks)),
            ["name"] = root.Exe,
        };
    }

    private static ActionResult Launched(string action, bool ok) =>
        new() { Action = action, Status = ok ? ActionStatus.Ok : ActionStatus.Failed, Message = ok ? "Opened" : "Could not open" };

    /// <summary>A3.8 result line: "Ended Chrome: 29 processes", "Ended 27 of 29, 2 run as admin", "Already exited".</summary>
    public static ActionResult Summarize(string action, string verb, string name, int requested, IReadOnlyList<ProcessOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        var ok = outcomes.Count(o => o.Status == ActionStatus.Ok);
        var items = outcomes.Select(o => new ActionItemResult { Item = o.Key.ToString(), Status = o.Status, Reason = o.Reason }).ToList();
        var past = verb switch { "End" => "Ended", "Close" => "Asked to close", "Lower" => "Lowered", "Freeze" => "Froze", "Resume" => "Resumed", "Restore" => "Restored", _ => verb };
        if (ok == 0)
        {
            var reason = outcomes.FirstOrDefault()?.Reason ?? "Nothing to do";
            return new ActionResult { Action = action, Status = outcomes.All(o => o.Status == ActionStatus.Refused) ? ActionStatus.Refused : ActionStatus.Failed, Message = reason, Items = items };
        }

        if (ok == requested && ok == outcomes.Count)
        {
            return new ActionResult { Action = action, Status = ActionStatus.Ok, Message = past + " " + name + (ok > 1 ? ": " + ok + " processes" : string.Empty), Items = items };
        }

        var reasons = outcomes.Where(o => o.Status != ActionStatus.Ok).GroupBy(o => o.Reason ?? "failed").Select(g => g.Count() + " " + ActionRouter.ShortReason(g.Key)).ToList();
        return new ActionResult { Action = action, Status = ActionStatus.Partial, Message = past + " " + ok + " of " + requested + (reasons.Count == 0 ? string.Empty : ", " + string.Join(", ", reasons)), Items = items };
    }
}
