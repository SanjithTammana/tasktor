using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tasktor.Contracts;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Agents;
using Tasktor.Contracts.Jobs;
using Tasktor.Contracts.Snapshot;
using Tasktor.Core.Storage;
using Tasktor.Hosts.Common.Pipe;
using Tasktor.Hosts.Common.Render;
using SnapshotRecord = Tasktor.Contracts.Snapshot.Snapshot;

namespace Tasktor.Cli;

/// <summary>
/// <c>tasktor</c>: a thin pipe client (U1 D-H4). Every verb that acts becomes an <see cref="ActionRequest"/> with origin
/// <c>cli</c>; the agent's router decides. Destructive verbs need <c>--yes</c> and <c>cli.allow_destructive</c>.
/// <c>job set</c> and <c>agent-hook</c> write their files directly and work without the agent.
/// </summary>
public static class CliApp
{
    public const string Usage = """
        usage: tasktor <command> [options]

          status [--json] [--cards]                 state (and cards); exit 3 when the agent is down
          job lower|restore|pause|resume|end <id>   act on a job (pause: --freeze | --polite)
          job set <id> [--pct N] [--done N --total N] [--unit U] [--stage S] [--msg M] [--label L]
                       [--state running|indeterminate|warning|error|done] [--heartbeat S] [--log PATH] [--pid N]
          job done <id> | job rm <id>               finish or remove a progress file
          proc lower|restore|close|end <pid>        act on a process
          pause|resume tracking|capture|refits [--for 1h]
          summary [--since 7d] [--json]             numbers-only workflow summary
          open [--card <id>]                        open the flyout
          forget learned|tracking|capture|all [--since 15m|1h|today] [--yes]
          export data [--to DIR] [--since 30d] [--format csv|jsonl|both] [--include-raw] [--include-capture] [--yes]
          settings export <zip> [--include-learned] [--include-notes] | settings import <zip> --yes | settings reset --yes
          preset apply|arrange <name>
          log verbose on|off | log bundle [--to PATH]
          agent-hook <working|needs-permission|waiting|ended> [--agent NAME]   (reads the hook JSON on stdin)
          uninstall --yes

        exit codes: 0 ok · 1 error · 2 refused by policy or guard · 3 agent not running · 4 needs confirm
        """;

    public static async Task<int> RunAsync(string[] args, CliIo io)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(io);
        var a = new Args(args);
        try
        {
            return a.Verb switch
            {
                "status" => await Status(a, io).ConfigureAwait(false),
                "job" => await Job(a, io).ConfigureAwait(false),
                "proc" => await Proc(a, io).ConfigureAwait(false),
                "pause" or "resume" => await PauseResume(a, io).ConfigureAwait(false),
                "summary" => await Summary(a, io).ConfigureAwait(false),
                "open" => await Act(io, a, "open", a.Opt("--card") is { } card ? new() { ["card"] = card } : null).ConfigureAwait(false),
                "forget" => await Forget(a, io).ConfigureAwait(false),
                "export" => await Export(a, io).ConfigureAwait(false),
                "settings" => await Settings(a, io).ConfigureAwait(false),
                "preset" => await Preset(a, io).ConfigureAwait(false),
                "log" => await LogVerb(a, io).ConfigureAwait(false),
                "agent-hook" => AgentHook(a, io),
                "uninstall" => await Act(io, a, "uninstall", null).ConfigureAwait(false),
                "help" or "--help" or "-h" or null => Help(io),
                _ => Bad(io, "unknown command: " + a.Verb),
            };
        }
        catch (AgentNotRunningException)
        {
            if (a.Flag("--json"))
            {
                await io.Out.WriteLineAsync("{\"agent\":{\"running\":false}}").ConfigureAwait(false);
            }
            else
            {
                await io.Err.WriteLineAsync("Tasktor is not running.").ConfigureAwait(false);
            }

            return ExitCode.AgentDown;
        }
        catch (ArgumentException ex)
        {
            return Bad(io, ex.Message);
        }
    }

    private static int Help(CliIo io)
    {
        io.Out.WriteLine(Usage);
        return ExitCode.Ok;
    }

    private static int Bad(CliIo io, string message)
    {
        io.Err.WriteLine("tasktor: " + message);
        io.Err.WriteLine("run 'tasktor help' for usage");
        return ExitCode.Error;
    }

    private static async Task<PipeClient> Connect(CliIo io) =>
        await PipeClient.ConnectAsync("cli", io.PipeName).ConfigureAwait(false);

    private static async Task<int> Status(Args a, CliIo io)
    {
        await using var client = await Connect(io).ConfigureAwait(false);
        var pull = await client.PullAsync("snapshot").ConfigureAwait(false);
        if (pull?.Data is not JsonObject snap)
        {
            return Bad(io, "no snapshot yet");
        }

        var withCards = a.Flag("--cards");
        if (a.Flag("--json"))
        {
            if (!withCards)
            {
                snap.Remove("cards");
            }

            await io.Out.WriteLineAsync(snap.ToJsonString()).ConfigureAwait(false);
            return ExitCode.Ok;
        }

        var parsed = snap.Deserialize(ContractsJsonContext.Default.Snapshot);
        await io.Out.WriteAsync(RenderText(parsed!, snap, io.Clock.UtcNow)).ConfigureAwait(false);
        return ExitCode.Ok;
    }

    /// <summary>The text status: a one-line header, then every card at flyout size.</summary>
    public static string RenderText(SnapshotRecord snap, JsonNode root, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snap);
        var sb = new System.Text.StringBuilder();
        sb.Append("Tasktor ").Append(snap.Agent.Version);
        if (snap.Agent.Paused is { } p && (p.Tracking || p.Capture || p.Refits))
        {
            sb.Append(" · paused: ").Append(string.Join(", ", new[] { p.Tracking ? "tracking" : null, p.Capture ? "capture" : null, p.Refits ? "refits" : null }.Where(x => x is not null)));
        }

        if (snap.Agent.Self is { } self)
        {
            sb.Append(" · ").Append(self.PrivateMb.ToString("0.0", CultureInfo.InvariantCulture)).Append(" MB");
        }

        sb.Append('\n');
        var resolver = new BindingResolver(root);
        var renderer = new TextRenderer(now);
        foreach (var card in snap.Cards ?? [])
        {
            sb.Append(renderer.Render(CardFitter.Fit(card, FitTarget.Flyout, resolver), resolver));
        }

        if (snap.Cards is not { Count: > 0 })
        {
            sb.Append("(no cards yet)\n");
        }

        return sb.ToString();
    }

    private static async Task<int> Job(Args a, CliIo io)
    {
        var sub = a.Positional(0) ?? throw new ArgumentException("job: which action?");
        var id = a.Positional(1) ?? throw new ArgumentException("job " + sub + ": which job id?");
        var target = new Dictionary<string, string> { ["job"] = id };
        return sub switch
        {
            "lower" => await Act(io, a, "priority.set", target, new() { ["priority"] = "below_normal" }).ConfigureAwait(false),
            "restore" => await Act(io, a, "priority.restore", target).ConfigureAwait(false),
            "pause" => await Act(io, a, "job.pause", target, a.Flag("--freeze") ? new() { ["mode"] = "freeze" } : a.Flag("--polite") ? new() { ["mode"] = "polite" } : null).ConfigureAwait(false),
            "resume" => await Act(io, a, "job.resume", target).ConfigureAwait(false),
            "end" => await Act(io, a, "job.end", target).ConfigureAwait(false),
            "set" => JobSet(a, io, id, done: false),
            "done" => JobSet(a, io, id, done: true),
            "rm" => JobRemove(io, id),
            _ => Bad(io, "job: unknown action " + sub),
        };
    }

    /// <summary><c>tasktor job set</c> (B4.7): merge into the progress file, written atomically.</summary>
    private static int JobSet(Args a, CliIo io, string id, bool done)
    {
        if (!StoreLayout.IsValidId(id))
        {
            return Bad(io, "job id must be [A-Za-z0-9._-]{1,64}");
        }

        var store = StoreLayout.Resolve(io.DataDir);
        var path = store.JobFile(id);
        ProgressFile? old = null;
        if (File.Exists(path))
        {
            try
            {
                old = JsonSerializer.Deserialize(File.ReadAllText(path), ContractsJsonContext.Default.ProgressFile);
            }
            catch (JsonException)
            {
            }
        }

        var state = a.Opt("--state") is { } s ? ParseState(s) : old?.State;
        if (done)
        {
            state = ProgressState.Done;
        }

        var file = new ProgressFile
        {
            Schema = 1,
            Id = id,
            Pid = a.OptUInt("--pid") ?? old?.Pid,
            Label = a.Opt("--label") ?? old?.Label,
            Pct = a.OptDouble("--pct") is { } pct ? Math.Clamp(pct, 0, 100) : done ? 100 : old?.Pct,
            Done = a.OptLong("--done") ?? old?.Done,
            Total = a.OptLong("--total") ?? old?.Total,
            Unit = a.Opt("--unit") ?? old?.Unit,
            Stage = a.Opt("--stage") ?? old?.Stage,
            Msg = a.Opt("--msg") ?? old?.Msg,
            EtaS = a.OptLong("--eta") ?? old?.EtaS,
            Log = a.Opt("--log") ?? old?.Log,
            State = state,
            HeartbeatS = a.OptInt("--heartbeat") is { } hb ? Math.Clamp(hb, 5, 3600) : old?.HeartbeatS,
            Updated = io.Clock.UtcNow,
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(file, ContractsJsonContext.Default.ProgressFile);
        if (bytes.Length > 4096)
        {
            return Bad(io, "progress file would exceed 4 KB");
        }

        AtomicFile.Write(path, bytes);
        return ExitCode.Ok;
    }

    private static int JobRemove(CliIo io, string id)
    {
        if (!StoreLayout.IsValidId(id))
        {
            return Bad(io, "job id must be [A-Za-z0-9._-]{1,64}");
        }

        var path = StoreLayout.Resolve(io.DataDir).JobFile(id);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return ExitCode.Ok;
    }

    private static ProgressState ParseState(string s) => s switch
    {
        "running" => ProgressState.Running,
        "indeterminate" => ProgressState.Indeterminate,
        "warning" => ProgressState.Warning,
        "error" => ProgressState.Error,
        "done" => ProgressState.Done,
        _ => throw new ArgumentException("--state must be running, indeterminate, warning, error or done"),
    };

    private static async Task<int> Proc(Args a, CliIo io)
    {
        var sub = a.Positional(0) ?? throw new ArgumentException("proc: which action?");
        var pid = a.Positional(1) ?? throw new ArgumentException("proc " + sub + ": which pid?");
        if (!uint.TryParse(pid, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            return Bad(io, "pid must be a number");
        }

        var target = new Dictionary<string, string> { ["pid"] = pid };
        return sub switch
        {
            "lower" => await Act(io, a, "priority.set", target, new() { ["priority"] = "below_normal" }).ConfigureAwait(false),
            "restore" => await Act(io, a, "priority.restore", target).ConfigureAwait(false),
            "close" => await Act(io, a, "proc.close", target).ConfigureAwait(false),
            "end" => await Act(io, a, "proc.end", target).ConfigureAwait(false),
            _ => Bad(io, "proc: unknown action " + sub),
        };
    }

    private static async Task<int> PauseResume(Args a, CliIo io)
    {
        var what = a.Positional(0) ?? throw new ArgumentException(a.Verb + ": tracking, capture or refits?");
        if (what is not ("tracking" or "capture" or "refits"))
        {
            return Bad(io, a.Verb + ": tracking, capture or refits");
        }

        Dictionary<string, string>? args = a.Opt("--for") is { } span ? new() { ["for"] = span } : null;
        return await Act(io, a, what + "." + a.Verb, null, args).ConfigureAwait(false);
    }

    private static async Task<int> Summary(Args a, CliIo io)
    {
        await using var client = await Connect(io).ConfigureAwait(false);
        var pull = await client.PullAsync("summary", new Dictionary<string, string> { ["since"] = a.Opt("--since") ?? "7d" }).ConfigureAwait(false);
        if (pull is null)
        {
            return Bad(io, "summary is not available (turn on workflow tracking)");
        }

        await io.Out.WriteLineAsync(a.Flag("--json") ? pull.Data?.ToJsonString() : SummaryText(pull.Data)).ConfigureAwait(false);
        return ExitCode.Ok;
    }

    private static string SummaryText(JsonNode? data)
    {
        if (data is not JsonObject o)
        {
            return "(no data)";
        }

        var sb = new System.Text.StringBuilder();
        foreach (var kv in o)
        {
            sb.Append(kv.Key).Append(": ").Append(kv.Value is JsonValue ? BindingResolver.Format(kv.Value) : kv.Value?.ToJsonString()).Append('\n');
        }

        return sb.ToString().TrimEnd();
    }

    private static async Task<int> Forget(Args a, CliIo io)
    {
        var what = a.Positional(0) ?? throw new ArgumentException("forget: learned, tracking, capture or all?");
        if (what is not ("learned" or "tracking" or "capture" or "all"))
        {
            return Bad(io, "forget: learned, tracking, capture or all");
        }

        Dictionary<string, string>? args = a.Opt("--since") is { } since ? new() { ["since"] = since } : null;
        return await Act(io, a, what == "capture" && args is not null ? "capture.forget" : "forget", new() { ["what"] = what }, args).ConfigureAwait(false);
    }

    private static async Task<int> Export(Args a, CliIo io)
    {
        if (a.Positional(0) != "data")
        {
            return Bad(io, "export: only 'export data'");
        }

        var args = new Dictionary<string, string>();
        if (a.Opt("--to") is { } to)
        {
            args["to"] = Path.GetFullPath(to);
        }

        if (a.Opt("--since") is { } since)
        {
            args["since"] = since;
        }

        args["format"] = a.Opt("--format") ?? "both";
        if (a.Flag("--include-raw"))
        {
            args["include_raw"] = "true";
        }

        if (a.Flag("--include-capture"))
        {
            args["include_capture"] = "true";
        }

        if (!a.Flag("--yes"))
        {
            await io.Err.WriteLineAsync("Exports are plain text on disk.").ConfigureAwait(false);
        }

        return await Act(io, a, "export.data", null, args).ConfigureAwait(false);
    }

    private static async Task<int> Settings(Args a, CliIo io)
    {
        var sub = a.Positional(0) ?? throw new ArgumentException("settings: export, import or reset?");
        var args = new Dictionary<string, string>();
        switch (sub)
        {
            case "export":
                args["path"] = Path.GetFullPath(a.Positional(1) ?? throw new ArgumentException("settings export <zip>"));
                if (a.Flag("--include-learned"))
                {
                    args["include_learned"] = "true";
                }

                if (a.Flag("--include-notes"))
                {
                    args["include_notes"] = "true";
                }

                return await Act(io, a, "settings.export", null, args).ConfigureAwait(false);
            case "import":
                args["path"] = Path.GetFullPath(a.Positional(1) ?? throw new ArgumentException("settings import <zip>"));
                return await Act(io, a, "settings.import", null, args).ConfigureAwait(false);
            case "reset":
                return await Act(io, a, "settings.reset", null).ConfigureAwait(false);
            default:
                return Bad(io, "settings: export, import or reset");
        }
    }

    private static async Task<int> Preset(Args a, CliIo io)
    {
        var sub = a.Positional(0);
        var name = a.Positional(1) ?? throw new ArgumentException("preset " + sub + " <name>");
        return sub switch
        {
            "apply" => await Act(io, a, "preset.apply", new() { ["preset"] = name }).ConfigureAwait(false),
            "arrange" => await Act(io, a, "preset.arrange", new() { ["preset"] = name }).ConfigureAwait(false),
            _ => Bad(io, "preset: apply or arrange"),
        };
    }

    private static async Task<int> LogVerb(Args a, CliIo io) => a.Positional(0) switch
    {
        "verbose" => await Act(io, a, "log.verbose", null, new() { ["on"] = a.Positional(1) == "off" ? "false" : "true" }).ConfigureAwait(false),
        "bundle" => await Act(io, a, "log.bundle", null, a.Opt("--to") is { } to ? new() { ["to"] = Path.GetFullPath(to) } : null).ConfigureAwait(false),
        _ => Bad(io, "log: verbose on|off or bundle"),
    };

    /// <summary>
    /// <c>tasktor agent-hook &lt;state&gt;</c> (U1 I1): reads the agent's hook JSON on stdin and keeps only
    /// <c>session_id</c>, the last segment of <c>cwd</c> and the state. Prompts and transcripts are never read into the file.
    /// </summary>
    private static int AgentHook(Args a, CliIo io)
    {
        var state = a.Positional(0) switch
        {
            "working" => AgentSessionState.Working,
            "needs-permission" => AgentSessionState.NeedsPermission,
            "waiting" => AgentSessionState.Waiting,
            "ended" => AgentSessionState.Ended,
            _ => throw new ArgumentException("agent-hook: working, needs-permission, waiting or ended"),
        };
        string? session = null;
        string? folder = null;
        uint? pid = null;
        var input = io.In.ReadToEnd();
        if (input.Length > 1024 * 1024)
        {
            return Bad(io, "hook input too large");
        }

        try
        {
            using var doc = JsonDocument.Parse(input.Length == 0 ? "{}" : input);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("session_id", out var s) && s.ValueKind == JsonValueKind.String)
                {
                    session = s.GetString();
                }

                if (doc.RootElement.TryGetProperty("cwd", out var c) && c.ValueKind == JsonValueKind.String)
                {
                    folder = Path.GetFileName(c.GetString()!.TrimEnd('/', '\\'));
                }
            }
        }
        catch (JsonException)
        {
            return Bad(io, "hook input is not JSON");
        }

        if (string.IsNullOrEmpty(session))
        {
            return Bad(io, "hook input has no session_id");
        }

        if (a.OptUInt("--pid") is { } p)
        {
            pid = p;
        }

        var file = new AgentHookFile
        {
            Agent = a.Opt("--agent") ?? "agent",
            SessionId = session,
            Folder = folder,
            State = state,
            Since = io.Clock.UtcNow,
            Pid = pid,
        };
        var store = StoreLayout.Resolve(io.DataDir);
        AtomicFile.Write(store.AgentFile(session), JsonSerializer.SerializeToUtf8Bytes(file, ContractsJsonContext.Default.AgentHookFile));
        return ExitCode.Ok;
    }

    private static async Task<int> Act(CliIo io, Args a, string action, Dictionary<string, string>? target, Dictionary<string, string>? args = null)
    {
        await using var client = await Connect(io).ConfigureAwait(false);
        var req = new ActionRequest
        {
            Action = action,
            Target = target,
            Args = args,
            Origin = Origin.Cli,
            Confirm = a.Flag("--yes") ? "--yes" : null,
        };
        var result = await client.ActAsync(req).ConfigureAwait(false);
        if (a.Flag("--json"))
        {
            await io.Out.WriteLineAsync(JsonSerializer.Serialize(result, ContractsJsonContext.Default.ActionResult)).ConfigureAwait(false);
        }
        else
        {
            var line = result.Message ?? result.Summary ?? result.Status.ToString().ToLowerInvariant();
            await (result.Status is ActionStatus.Ok or ActionStatus.Partial ? io.Out : io.Err).WriteLineAsync(line).ConfigureAwait(false);
            foreach (var item in result.Items ?? [])
            {
                if (item.Status != ActionStatus.Ok)
                {
                    await io.Err.WriteLineAsync("  " + item.Item + ": " + item.Reason).ConfigureAwait(false);
                }
            }
        }

        return result.Status switch
        {
            ActionStatus.Ok or ActionStatus.Partial => ExitCode.Ok,
            ActionStatus.Refused => ExitCode.Refused,
            ActionStatus.NeedsConfirm => ExitCode.NeedsConfirm,
            _ => ExitCode.Error,
        };
    }

    /// <summary>Minimal argument reader: a verb, positionals, <c>--flags</c> and <c>--option value</c>.</summary>
    private sealed class Args
    {
        private static readonly HashSet<string> Flags = new(StringComparer.Ordinal)
        {
            "--json", "--cards", "--yes", "--freeze", "--polite", "--include-raw", "--include-capture", "--include-learned", "--include-notes",
        };

        private readonly List<string> _positional = [];
        private readonly HashSet<string> _flags = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _opts = new(StringComparer.Ordinal);

        public Args(string[] args)
        {
            for (var i = 0; i < args.Length; i++)
            {
                var x = args[i];
                if (x.StartsWith("--", StringComparison.Ordinal) && x.Length > 2)
                {
                    if (Flags.Contains(x) || i + 1 >= args.Length)
                    {
                        _flags.Add(x);
                    }
                    else
                    {
                        _opts[x] = args[++i];
                    }
                }
                else if (Verb is null)
                {
                    Verb = x;
                }
                else
                {
                    _positional.Add(x);
                }
            }
        }

        public string? Verb { get; }

        public string? Positional(int i) => i < _positional.Count ? _positional[i] : null;

        public bool Flag(string name) => _flags.Contains(name);

        public string? Opt(string name) => _opts.GetValueOrDefault(name);

        public double? OptDouble(string name) => Opt(name) is { } v ? double.Parse(v, CultureInfo.InvariantCulture) : null;

        public long? OptLong(string name) => Opt(name) is { } v ? long.Parse(v, CultureInfo.InvariantCulture) : null;

        public int? OptInt(string name) => Opt(name) is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : null;

        public uint? OptUInt(string name) => Opt(name) is { } v ? uint.Parse(v, CultureInfo.InvariantCulture) : null;
    }
}
