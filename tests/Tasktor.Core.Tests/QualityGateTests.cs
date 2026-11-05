using System.Text.RegularExpressions;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Platform;
using Tasktor.Core.Actions;
using Tasktor.Core.Features.Agents;
using Tasktor.Core.Features.Folders;
using Tasktor.Core.Rules;
using Tasktor.Tests.Shared;

namespace Tasktor.Core.Tests;

/// <summary>
/// U1 D-H11 quality gates that run on every CI runner: G-redact (personal data never reaches the log), G-generic and
/// G-private (no personal names or machine paths in the repository), shipped rules are reversible only, and every
/// catalog action has a handler or a named owner milestone.
/// </summary>
public sealed partial class QualityGateTests
{
    private static readonly string Root = RepoPaths.Root;

    [GeneratedRegex(@"[\\/](bin|obj|\.git|\.claude|node_modules|__pycache__|\.venv|\.pytest_cache)[\\/]")]
    private static partial Regex Ignored();

    /// <summary>Source and text files (build output, the git folder and agent worktrees excluded).</summary>
    private static IEnumerable<string> RepoFiles(params string[] extensions) =>
        Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .Where(f => !Ignored().IsMatch(Path.GetRelativePath(Root, f).Insert(0, "/")))
            .Where(f => extensions.Length == 0 || extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));

    private static string Rel(string f) => Path.GetRelativePath(Root, f).Replace('\\', '/');

    // ---------------- G-redact ----------------

    [GeneratedRegex(@"\.(Info|Warn|Error|Debug)\((?<args>[^;]*?)\);", RegexOptions.Singleline)]
    private static partial Regex LogCall();

    [Fact]
    public void G_redact_no_log_call_in_src_passes_a_revealed_sensitive_value()
    {
        var offenders = new List<string>();
        foreach (var f in RepoFiles(".cs").Where(f => Rel(f).StartsWith("src/", StringComparison.Ordinal)))
        {
            foreach (Match m in LogCall().Matches(File.ReadAllText(f)))
            {
                var args = m.Groups["args"].Value;
                if (args.Contains(".Reveal()", StringComparison.Ordinal))
                {
                    offenders.Add(Rel(f) + ": " + m.Value.Split('\n')[0].Trim());
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void G_redact_sensitive_log_fields_print_redacted()
    {
        var lines = new Logging.MemoryLogWriter();
        var log = new Logging.LogSink(new FakeClock(), lines);
        log.Info("probe", Logging.LogField.Of("title", Sensitive.Of("FixtureSecretTitle")));
        Assert.DoesNotContain(lines.Lines, l => l.Contains("FixtureSecretTitle", StringComparison.Ordinal));
        Assert.Contains(lines.Lines, l => l.Contains(Sensitive<string>.Redacted, StringComparison.Ordinal));
    }

    [Fact]
    public void G_redact_features_handling_personal_data_never_log_it()
    {
        using var rig = new FeatureRig();
        const string Secret = "FixturePersonalWord";

        // I12: a file name under a watched folder.
        var downloads = Path.Combine(rig.Store.Root, "Downloads");
        var folders = rig.Add(new FolderAction(rig.Context, _ => new NullFiles()) { Folders = () => [downloads] });
        folders.OnFile(new FileEvent(downloads, Sensitive.Of(Path.Combine(downloads, Secret + ".pdf")), rig.Clock.UtcNow));

        // I1: an agent session's folder name reported by a hook.
        var apps = rig.Add(new Features.SystemMonitor.Apps(rig.Context, new FakeTable<ProcKey, ProcessRow>(), new Apps.AppIdentity(), _ => null));
        var agents = rig.Add(new AgentWatch(rig.Context, apps, new AgentHookSource(rig.Store.Layout.AgentsDir), _ => null, _ => null));
        agents.ApplyHooks([new Contracts.Agents.AgentHookFile { Agent = "claude", SessionId = "s", Folder = Secret, State = Contracts.Snapshot.AgentSessionState.Working, Since = rig.Clock.UtcNow }], rig.Clock.UtcNow);
        rig.LoadRules();
        rig.Rules.Evaluate();
        rig.Loop.RunPending();

        Assert.NotEmpty(rig.LogLines.Lines);
        Assert.DoesNotContain(rig.LogLines.Lines, l => l.Contains(Secret, StringComparison.Ordinal));
    }

    private sealed class NullFiles : IEventSource<FileEvent>
    {
        public string Name => "null";

        public void Start(Action<FileEvent> post)
        {
        }

        public void Stop()
        {
        }
    }

    // ---------------- G-generic / G-private ----------------

    /// <summary>Synthetic profile names the tests and docs use on purpose (redaction fixtures, CI runners, placeholders).</summary>
    private static readonly HashSet<string> SyntheticProfiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "alexex", "runneradmin", "runner", "user", "u", "public", "default", "you", "me", "example", "name", "username", "someone",
    };

    [GeneratedRegex(@"(?:[A-Za-z]:\\{1,2}Users\\{1,2}|/home/|/Users/)(?<name>[A-Za-z0-9._-]+)")]
    private static partial Regex ProfilePath();

    [Fact]
    public void G_generic_no_real_user_profile_path_in_the_repository()
    {
        var hits = new List<string>();
        foreach (var f in RepoFiles(".cs", ".py", ".md", ".json", ".yml", ".yaml", ".props", ".csproj", ".sh", ".txt", ".ics"))
        {
            foreach (Match m in ProfilePath().Matches(File.ReadAllText(f)))
            {
                var name = m.Groups["name"].Value;
                if (!SyntheticProfiles.Contains(name))
                {
                    hits.Add(Rel(f) + ": " + m.Value);
                }
            }
        }

        Assert.Empty(hits);
    }

    /// <summary>G-private: names from the <c>PRIVATE_NAMES</c> secret (comma-separated) appear nowhere. Skipped without it.</summary>
    [Fact]
    public void G_private_names_from_the_secret_appear_nowhere()
    {
        var names = (Environment.GetEnvironmentVariable("PRIVATE_NAMES") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(n => n.Length >= 3).ToList();
        if (names.Count == 0)
        {
            return; // the gate runs only where the secret is set (U1 D-H11)
        }

        var hits = RepoFiles().Where(f => new FileInfo(f).Length < 4 * 1024 * 1024)
            .Where(f =>
            {
                var text = File.ReadAllText(f);
                return names.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));
            })
            .Select(Rel).ToList();
        Assert.Empty(hits); // only file paths are reported, never the names
    }

    // ---------------- rules: reversible only ----------------

    [Fact]
    public void Shipped_rules_call_only_reversible_rule_safe_actions_and_offer_buttons_toasts_may_carry()
    {
        var set = RulesLoader.Load(File.ReadAllText(Path.Combine(Root, "src", "Tasktor.Agent", "rules.default.json")), null);
        Assert.Empty(set.Errors);
        foreach (var rule in set.Rules)
        {
            foreach (var a in rule.Rule.Then)
            {
                if (a.Toast is not null)
                {
                    foreach (var b in a.Buttons ?? [])
                    {
                        var verb = b.Split(':')[0];
                        var ok = verb is "open" or "run" or "snooze" || (ActionCatalog.Find(verb) is { } d && (d.Surfaces & Surface.Toast) != 0);
                        Assert.True(ok, rule.Id + ": button " + b);
                    }

                    continue;
                }

                var id = RuleActionMap.ActionId(a);
                var def = id is null ? null : ActionCatalog.Find(id);
                Assert.True(def is { Class: ActionClass.Reversible, UserOnly: false, LiftsBlock: false }, rule.Id + ": " + id);
                if (rule.Rule.Undo == true && def!.Undo is { } undo)
                {
                    Assert.NotNull(ActionCatalog.Find(undo));
                }
            }
        }
    }

    // ---------------- every action has a handler ----------------

    /// <summary>Catalog rows whose handler lands with a later milestone (C's card actions, T1 capture, the viewers).</summary>
    private static readonly IReadOnlyDictionary<string, string> Pending = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["capture.pause"] = "M12",
        ["capture.resume"] = "M12",
        ["capture.exclude"] = "M12",
        ["capture.unexclude"] = "M12",
        ["capture.tab_pause"] = "M12",
        ["preset.arrange"] = "M14 (U3)",
        ["preset.undo"] = "M14 (U3)",
        ["preset.apply"] = "M14 (U3)",
    };

    [GeneratedRegex(@"Register\(""(?<id>[a-z_.]+)""")]
    private static partial Regex Registration();

    [Fact]
    public void Every_catalog_action_has_a_handler_or_a_named_owner_milestone()
    {
        var registered = RepoFiles(".cs").Where(f => Rel(f).StartsWith("src/", StringComparison.Ordinal))
            .SelectMany(f => Registration().Matches(File.ReadAllText(f)).Select(m => m.Groups["id"].Value))
            .ToHashSet(StringComparer.Ordinal);
        var missing = ActionCatalog.All.Select(a => a.Id).Where(id => !registered.Contains(id) && !Pending.ContainsKey(id)).ToList();
        Assert.Empty(missing);
        Assert.All(Pending.Keys, id => Assert.NotNull(ActionCatalog.Find(id)));
    }
}
