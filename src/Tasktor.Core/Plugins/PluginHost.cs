using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tasktor.Contracts;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Plugins;
using Tasktor.Contracts.Rules;
using Tasktor.Contracts.Snapshot;
using Tasktor.Core.Logging;
using Tasktor.Core.Storage;
using Tasktor.Core.Time;

namespace Tasktor.Core.Plugins;

/// <summary>Where a plugin stands.</summary>
public enum PluginLifecycle
{
    /// <summary>Found and off: no consent yet, or its files changed since the consent (asks again).</summary>
    Found,

    On,

    /// <summary>Paused by the user or a rule (consent kept).</summary>
    Paused,

    /// <summary>Turned off after 3 failures in a row; shows an error card.</summary>
    Error,

    /// <summary><c>plugin.json</c> is not valid.</summary>
    Invalid,
}

/// <summary>One discovered plugin and its run state. Loop-only.</summary>
public sealed class PluginEntry
{
    public required string Id { get; init; }

    public required string Dir { get; init; }

    public PluginManifest? Manifest { get; set; }

    public string? Problem { get; set; }

    public string ManifestSha256 { get; set; } = string.Empty;

    public string CommandSha256 { get; set; } = string.Empty;

    public string? Pack { get; init; }

    public bool Builtin { get; init; }

    public PluginLifecycle State { get; set; }

    public bool ChangedSinceConsent { get; set; }

    public int Failures { get; set; }

    public string? LastError { get; set; }

    public string? StderrTail { get; set; }

    public IReadOnlyList<Card> Cards { get; set; } = [];

    public DateTimeOffset? UpdatedAt { get; set; }

    public DateTimeOffset NextRun { get; set; }

    public bool Running { get; set; }

    public long Runs { get; set; }
}

/// <summary>A discovered pack: one consent dialog for all its plugins; its rule and config entries start off until ticked.</summary>
public sealed record PackEntry(string Id, string Dir, PackManifest Manifest, RulesFile? Rules, string? Problem);

/// <summary>What the consent dialog shows (U1 D-H7): name, author, reads, network, actions and the exact command line.</summary>
public sealed record PluginConsentView(string Id, string Name, string? Author, IReadOnlyList<string> Reads, IReadOnlyList<string> Network, IReadOnlyList<string> Actions, string CommandLine, string ManifestSha256, string CommandSha256, string? Pack);

/// <summary>Outcome of one plugin run.</summary>
public sealed record PluginRunResult(bool Ok, string? Error);

/// <summary>
/// The plugin host (U1 D-H7 + the pack amendment). Discovers <c>plugins\</c> and <c>plugins-builtin\</c>; a new folder is
/// "Found, off"; enabling pins SHA-256 of <c>plugin.json</c> and of the command's target file, and any change asks again.
/// Runs one process per run through <see cref="IProcessControl.StartWorker"/> (kind plugin: Job Object, 256 MB, BelowNormal on
/// Windows), never resident, one run at a time per plugin, killed at its timeout; output ≤ 64 KB, checked against the
/// block subset; 3 failures in a row turn it off with an error card. Plugins get only their settings, their data folder
/// and the snapshot path. Loop-only; runs happen off the loop and post back.
/// </summary>
public sealed class PluginHost : ICardProvider
{
    public const int FailureLimit = 3;

    private static readonly string[] ClearedEnv = ["PYTHONPATH", "PYTHONHOME", "PYTHONSTARTUP", "NODE_PATH", "NODE_OPTIONS"];

    private readonly StoreLayout _layout;
    private readonly ConsentStore _consents;
    private readonly IProcessControl _processes;
    private readonly Action<Action> _post;
    private readonly LogSink? _log;
    private readonly Dictionary<string, PluginEntry> _plugins = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PackEntry> _packs = new(StringComparer.Ordinal);

    /// <param name="post">Posts work back onto the logic loop (the agent's <c>LogicLoop.Post</c>).</param>
    public PluginHost(StoreLayout layout, ConsentStore consents, IProcessControl processes, Action<Action> post, LogSink? log = null)
    {
        _layout = layout;
        _consents = consents;
        _processes = processes;
        _post = post;
        _log = log;
    }

    /// <summary><c>plugins-builtin\</c> in the install dir (first-party plugins, off by default, same consent).</summary>
    public string? BuiltinDir { get; init; }

    /// <summary>The bundled Python, offered as <c>{python}</c> in <c>command</c>.</summary>
    public string? PythonPath { get; init; }

    public Version TasktorVersion { get; init; } = new(1, 0);

    /// <summary>How plugin secrets (e.g. an ICS URL) are protected at rest (DPAPI on Windows, D-S5).</summary>
    public KeyProtection SecretProtection { get; init; } = KeyProtection.None;

    public IReadOnlyCollection<PluginEntry> Plugins => _plugins.Values;

    public IReadOnlyCollection<PackEntry> Packs => _packs.Values;

    /// <summary>Raised on the loop when cards or states change.</summary>
    public event Action? Changed;

    public PluginEntry? Find(string id) => _plugins.GetValueOrDefault(id);

    /// <summary>Scan the plugin folders. Existing run state is kept for plugins that are still there.</summary>
    public void Discover(DateTimeOffset now)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        _packs.Clear();
        foreach (var (root, builtin) in new[] { (BuiltinDir, true), (_layout.PluginsDir, false) })
        {
            if (root is null || !Directory.Exists(root))
            {
                continue;
            }

            foreach (var dir in Directory.GetDirectories(root).Order(StringComparer.Ordinal))
            {
                var packFile = Path.Combine(dir, "pack.json");
                if (File.Exists(packFile))
                {
                    var pack = LoadPack(dir, packFile);
                    _packs[pack.Id] = pack;
                    foreach (var sub in pack.Manifest.Plugins)
                    {
                        var pdir = Path.Combine(dir, StoreLayout.SafeName(sub));
                        if (Directory.Exists(pdir))
                        {
                            Load(pdir, builtin, pack.Id, now, seen);
                        }
                    }

                    continue;
                }

                Load(dir, builtin, null, now, seen);
            }
        }

        foreach (var gone in _plugins.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _plugins.Remove(gone);
        }

        Changed?.Invoke();
    }

    /// <summary>What the consent dialog shows.</summary>
    public PluginConsentView? ConsentView(string id)
    {
        if (!_plugins.TryGetValue(id, out var e) || e.Manifest is not { } m)
        {
            return null;
        }

        return new PluginConsentView(
            m.Id,
            m.Name,
            m.Author,
            m.Reads,
            m.Network,
            (m.Actions ?? []).Select(a => a.Label).ToList(),
            string.Join(" ", ResolveCommand(m, e.Dir).Select(Quote)),
            e.ManifestSha256,
            e.CommandSha256,
            e.Pack);
    }

    /// <summary>Consent given: pin the hashes and turn the plugin on (first run at once).</summary>
    public bool Enable(string id, DateTimeOffset now)
    {
        if (!_plugins.TryGetValue(id, out var e) || e.Manifest is null || e.State == PluginLifecycle.Invalid)
        {
            return false;
        }

        _consents.GivePlugin(id, new PluginConsent { ManifestSha256 = e.ManifestSha256, CommandSha256 = e.CommandSha256, GivenAt = now, Pack = e.Pack });
        e.State = PluginLifecycle.On;
        e.ChangedSinceConsent = false;
        e.Failures = 0;
        e.LastError = null;
        e.NextRun = now;
        _log?.Info("plugin.enabled", LogField.Of("plugin", id));
        Changed?.Invoke();
        return true;
    }

    /// <summary>One dialog for a whole pack.</summary>
    public int EnablePack(string packId, DateTimeOffset now) =>
        _plugins.Values.Where(p => p.Pack == packId).Count(p => Enable(p.Id, now));

    /// <summary>Turn off and drop the consent (re-enabling asks again).</summary>
    public void Disable(string id)
    {
        if (_plugins.TryGetValue(id, out var e))
        {
            _consents.RevokePlugin(id);
            e.State = e.State == PluginLifecycle.Invalid ? PluginLifecycle.Invalid : PluginLifecycle.Found;
            e.Cards = [];
            Changed?.Invoke();
        }
    }

    public bool Pause(string id, bool paused, DateTimeOffset now)
    {
        if (!_plugins.TryGetValue(id, out var e) || e.State is not (PluginLifecycle.On or PluginLifecycle.Paused))
        {
            return false;
        }

        e.State = paused ? PluginLifecycle.Paused : PluginLifecycle.On;
        if (!paused)
        {
            e.NextRun = now;
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>Start every due run (call on the loop, e.g. from a timer).</summary>
    public int Tick(DateTimeOffset now)
    {
        var started = 0;
        foreach (var e in _plugins.Values)
        {
            if (e.State == PluginLifecycle.On && !e.Running && e.Manifest is { } m && (m.Interval != "manual" || e.Runs == 0) && e.NextRun <= now)
            {
                StartRun(e, PluginCall.Render, null, null, now);
                started++;
            }
        }

        return started;
    }

    /// <summary>When the next run is due (for the loop's timer).</summary>
    public DateTimeOffset? NextDue =>
        _plugins.Values.Where(e => e.State == PluginLifecycle.On && !e.Running && e.Manifest is not null && (e.Manifest.Interval != "manual" || e.Runs == 0))
            .Select(e => (DateTimeOffset?)e.NextRun).Min();

    /// <summary>Run a plugin's own declared action (D-H4 plugin row), then re-render.</summary>
    public Task<PluginRunResult> Act(string id, string action, IReadOnlyDictionary<string, string>? args, DateTimeOffset now)
    {
        if (!_plugins.TryGetValue(id, out var e) || e.Manifest is not { } m || e.State != PluginLifecycle.On)
        {
            return Task.FromResult(new PluginRunResult(false, "Plugin is off"));
        }

        if ((m.Actions ?? []).All(a => a.Id != action))
        {
            return Task.FromResult(new PluginRunResult(false, "Not one of the plugin's declared actions"));
        }

        if (e.Running)
        {
            return Task.FromResult(new PluginRunResult(false, "Plugin is busy; try again"));
        }

        return StartRun(e, PluginCall.Action, action, args, now);
    }

    /// <summary>Refresh a plugin now (manual plugins, the Refresh button).</summary>
    public Task<PluginRunResult> RunNow(string id, DateTimeOffset now) =>
        _plugins.TryGetValue(id, out var e) && e.State == PluginLifecycle.On && !e.Running
            ? StartRun(e, PluginCall.Render, null, null, now)
            : Task.FromResult(new PluginRunResult(false, "Plugin is off or busy"));

    public IReadOnlyList<Card> Cards()
    {
        var cards = new List<Card>();
        foreach (var e in _plugins.Values.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            if (e.State == PluginLifecycle.On || e.State == PluginLifecycle.Paused)
            {
                cards.AddRange(e.Cards);
            }
            else if (e.State == PluginLifecycle.Error)
            {
                cards.Add(new Card
                {
                    Id = "plugin." + e.Id + ".error",
                    Source = new CardSource { Kind = CardSourceKind.Plugin, Plugin = e.Id, Name = e.Manifest?.Name ?? e.Id },
                    Title = e.Manifest?.Name ?? e.Id,
                    Icon = IconKey.Plugin,
                    Severity = Value.Literal("critical"),
                    Blocks =
                    [
                        new BadgeBlock { Text = Value.Literal("Turned off after 3 failures"), Severity = Value.Literal("critical"), Priority = 1 },
                        new BadgeBlock { Text = Value.Literal(e.LastError ?? "Unknown error"), Priority = 2 },
                    ],
                    Actions = [new CardAction { Id = "enable", Label = "Turn on again", Action = "plugin.enable", Target = new Dictionary<string, string>(StringComparer.Ordinal) { ["plugin"] = e.Id } }],
                });
            }
        }

        return cards;
    }

    /// <summary>The snapshot's <c>state.plugins</c>.</summary>
    public IReadOnlyList<PluginStatus> Statuses() => _plugins.Values.OrderBy(p => p.Id, StringComparer.Ordinal).Select(p => new PluginStatus
    {
        Id = p.Id,
        State = p.State switch
        {
            PluginLifecycle.On => PluginState.Ok,
            PluginLifecycle.Error or PluginLifecycle.Invalid => PluginState.Error,
            PluginLifecycle.Found => PluginState.Found,
            _ => PluginState.Off,
        },
        UpdatedAt = p.UpdatedAt,
        Error = p.State is PluginLifecycle.Error or PluginLifecycle.Invalid ? p.LastError ?? p.Problem : null,
    }).ToList();

    /// <summary>A pack's rules, only the ticked ones, as a rules file text for the loader (every entry is off until ticked).</summary>
    public string? TickedPackRules(Func<string, string, bool> isTicked)
    {
        ArgumentNullException.ThrowIfNull(isTicked);
        var rules = new List<Rule>();
        var groups = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var pack in _packs.Values)
        {
            if (pack.Rules is not { } file || !_plugins.Values.Any(p => p.Pack == pack.Id && p.State != PluginLifecycle.Found))
            {
                continue;
            }

            foreach (var r in file.Rules ?? [])
            {
                if (isTicked(pack.Id, r.Id))
                {
                    rules.Add(r with { Builtin = null });
                }
            }

            foreach (var g in file.Groups ?? new Dictionary<string, IReadOnlyList<string>>())
            {
                groups.TryAdd(g.Key, g.Value);
            }
        }

        return rules.Count == 0 ? null : JsonSerializer.Serialize(new RulesFile { Rules = rules, Groups = groups }, ContractsJsonContext.Default.RulesFile);
    }

    /// <summary>Plugin settings live in <c>plugin-data\&lt;id&gt;\settings.json</c>; secrets are protected at rest.</summary>
    public Dictionary<string, string> Settings(string id)
    {
        var path = Path.Combine(_layout.PluginData(id), "settings.json");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return result;
        }

        try
        {
            var raw = JsonSerializer.Deserialize(File.ReadAllText(path), ContractsJsonContext.Default.DictionaryStringString) ?? [];
            foreach (var kv in raw)
            {
                result[kv.Key] = kv.Value.StartsWith("protected:", StringComparison.Ordinal)
                    ? Encoding.UTF8.GetString(SecretProtection.Unprotect(Convert.FromBase64String(kv.Value[10..])))
                    : kv.Value;
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException)
        {
            _log?.Warn("plugin.settings_unreadable", LogField.Of("plugin", id));
        }

        return result;
    }

    public void SetSetting(string id, string key, string value)
    {
        if (!_plugins.TryGetValue(id, out var e) || e.Manifest is not { } m)
        {
            return;
        }

        var decl = (m.Settings ?? []).FirstOrDefault(s => s.Key == key) ?? throw new ArgumentException("unknown setting " + key);
        var all = Settings(id);
        all[key] = value;
        var stored = all.ToDictionary(
            kv => kv.Key,
            kv => (m.Settings ?? []).FirstOrDefault(s => s.Key == kv.Key)?.Type == PluginSettingType.Secret
                ? "protected:" + Convert.ToBase64String(SecretProtection.Protect(Encoding.UTF8.GetBytes(kv.Value)))
                : kv.Value,
            StringComparer.Ordinal);
        _ = decl;
        AtomicFile.Write(Path.Combine(_layout.PluginData(id), "settings.json"), JsonSerializer.SerializeToUtf8Bytes(stored, ContractsJsonContext.Default.DictionaryStringString));
    }

    private Task<PluginRunResult> StartRun(PluginEntry e, PluginCall call, string? action, IReadOnlyDictionary<string, string>? args, DateTimeOffset now)
    {
        var m = e.Manifest!;
        e.Running = true;
        e.Runs++;
        var command = ResolveCommand(m, e.Dir);
        var dataDir = _layout.PluginData(e.Id);
        Directory.CreateDirectory(dataDir);
        var input = new PluginInput
        {
            Call = call,
            Action = action,
            Args = args,
            Settings = Settings(e.Id),
            SnapshotPath = _layout.Snapshot,
            DataDir = dataDir,
        };
        var spec = new WorkerSpec
        {
            Kind = WorkerKind.Plugin,
            Exe = command[0],
            Args = command.Skip(1).ToList(),
            WorkingDir = e.Dir,
            MemoryMb = 256,
            TimeoutMs = (int)DurationText.Parse(m.Timeout).TotalMilliseconds,
            Stdin = JsonSerializer.Serialize(input, ContractsJsonContext.Default.PluginInput),
            ClearEnv = ClearedEnv,
        };
        var tcs = new TaskCompletionSource<PluginRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task.Run(async () =>
        {
            var handle = _processes.StartWorker(spec);
            return await _processes.WaitWorker(handle, CancellationToken.None).ConfigureAwait(false);
        }).ContinueWith(
            t => _post(() => tcs.SetResult(Complete(e, t.IsCompletedSuccessfully ? t.Result : null, t.Exception?.InnerException, now))),
            TaskScheduler.Default);
        return tcs.Task;
    }

    private PluginRunResult Complete(PluginEntry e, WorkerExit? exit, Exception? startError, DateTimeOffset startedAt)
    {
        e.Running = false;
        var m = e.Manifest!;
        if (m.Interval != "manual")
        {
            e.NextRun = startedAt + DurationText.Parse(m.Interval);
        }

        string? error = null;
        IReadOnlyList<Card>? cards = null;
        if (startError is not null)
        {
            error = "could not start: " + startError.Message;
        }
        else if (exit!.TimedOut)
        {
            error = "timed out after " + m.Timeout;
        }
        else if (exit.OutputTooLarge)
        {
            error = "output is over 64 KB";
        }
        else if (exit.ExitCode != 0)
        {
            error = "exited with code " + exit.ExitCode;
        }
        else
        {
            (cards, error) = PluginValidation.CheckOutput(m, exit.Stdout, startedAt);
        }

        e.StderrTail = exit?.StderrTail;
        if (error is null)
        {
            e.Failures = 0;
            e.LastError = null;
            e.Cards = cards!;
            e.UpdatedAt = startedAt;
            if (e.State == PluginLifecycle.Error)
            {
                e.State = PluginLifecycle.On;
            }
        }
        else
        {
            e.Failures++;
            e.LastError = error;
            _log?.Warn("plugin.failed", LogField.Of("plugin", e.Id), LogField.Of("failures", e.Failures));
            if (e.Failures >= FailureLimit && e.State == PluginLifecycle.On)
            {
                e.State = PluginLifecycle.Error;
                e.Cards = [];
                _log?.Warn("plugin.turned_off", LogField.Of("plugin", e.Id));
            }
        }

        Changed?.Invoke();
        return new PluginRunResult(error is null, error);
    }

    private void Load(string dir, bool builtin, string? pack, DateTimeOffset now, HashSet<string> seen)
    {
        var folder = Path.GetFileName(dir);
        var manifestPath = Path.Combine(dir, "plugin.json");
        if (!File.Exists(manifestPath))
        {
            return;
        }

        if (!_plugins.TryGetValue(folder, out var e))
        {
            e = new PluginEntry { Id = folder, Dir = dir, Builtin = builtin, Pack = pack, NextRun = now };
            _plugins[folder] = e;
        }

        seen.Add(folder);
        var bytes = File.ReadAllBytes(manifestPath);
        e.ManifestSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        try
        {
            e.Manifest = JsonSerializer.Deserialize(bytes, ContractsJsonContext.Default.PluginManifest);
            e.Problem = e.Manifest is null ? "empty plugin.json" : PluginValidation.CheckManifest(e.Manifest, folder, TasktorVersion);
        }
        catch (JsonException ex)
        {
            e.Manifest = null;
            e.Problem = "plugin.json: " + ex.Message.Split('\n')[0];
        }

        if (e.Problem is not null)
        {
            e.State = PluginLifecycle.Invalid;
            e.LastError = e.Problem;
            return;
        }

        e.CommandSha256 = CommandHash(e.Manifest!, dir);
        var consented = _consents.PluginAllowed(folder, e.ManifestSha256, e.CommandSha256);
        e.ChangedSinceConsent = !consented && _consents.Current.Plugins.ContainsKey(folder);
        if (!consented)
        {
            e.State = PluginLifecycle.Found; // new, or changed since consent: ask again
            e.Cards = [];
        }
        else if (e.State is PluginLifecycle.Found or PluginLifecycle.Invalid)
        {
            e.State = PluginLifecycle.On;
        }
    }

    private static PackEntry LoadPack(string dir, string packFile)
    {
        var id = Path.GetFileName(dir);
        try
        {
            var m = JsonSerializer.Deserialize(File.ReadAllText(packFile), ContractsJsonContext.Default.PackManifest)
                ?? throw new JsonException("empty pack.json");
            RulesFile? rules = null;
            if (m.Rules is { } rulesName && File.Exists(Path.Combine(dir, StoreLayout.SafeName(rulesName))))
            {
                rules = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(dir, StoreLayout.SafeName(rulesName))), ContractsJsonContext.Default.RulesFile);
            }

            return new PackEntry(m.Id, dir, m, rules, null);
        }
        catch (JsonException ex)
        {
            return new PackEntry(id, dir, new PackManifest { Id = id, Name = id, Version = "?", Plugins = [] }, null, ex.Message.Split('\n')[0]);
        }
    }

    /// <summary>The command with <c>{python}</c> substituted; relative paths stay relative to the plugin dir (its working directory).</summary>
    private List<string> ResolveCommand(PluginManifest m, string dir)
    {
        var list = m.Command.Select(c => c == "{python}" ? PythonPath ?? s_pathPython.Value : c).ToList();
        var first = Path.Combine(dir, list[0]);
        if (!Path.IsPathRooted(list[0]) && File.Exists(first))
        {
            list[0] = first;
        }

        return list;
    }

    /// <summary>SHA-256 of the command's target file: the first command part that is a file inside the plugin dir.</summary>
    private string CommandHash(PluginManifest m, string dir)
    {
        var root = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
        foreach (var part in ResolveCommand(m, dir))
        {
            var full = Path.GetFullPath(Path.Combine(dir, part));
            if (full.StartsWith(root, StringComparison.Ordinal) && File.Exists(full))
            {
                return Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(full)));
            }
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u0000", m.Command))));
    }

    private static readonly Lazy<string> s_pathPython = new(() =>
    {
        // No bundled runtime (development, Linux): the first python on PATH.
        foreach (var name in OperatingSystem.IsWindows() ? new[] { "python.exe", "py.exe" } : ["python3", "python"])
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = Path.Combine(dir, name);
                if (File.Exists(p))
                {
                    return p;
                }
            }
        }

        return "python";
    });

    private static string Quote(string s) => s.Contains(' ', StringComparison.Ordinal) ? "\"" + s + "\"" : s;
}
