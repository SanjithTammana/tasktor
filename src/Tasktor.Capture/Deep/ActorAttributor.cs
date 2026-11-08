namespace Tasktor.Capture.Deep;

/// <summary>
/// D-C17 actor attribution. Keys: hardware input is <c>human</c>; injected input while a known agent is active is
/// <c>agent:&lt;exe&gt;</c>, otherwise <c>injected</c> (remote desktop and accessibility tools inject too; no name is ever
/// guessed). Processes: a start whose parent chain reaches a shell and follows an Enter-terminated run in that terminal
/// within 2 s is <c>cli:&lt;shell&gt;</c>; a chain through a known agent is <c>agent:&lt;exe&gt;</c>; else <c>app</c> or
/// <c>system</c>. Evidence bits travel with every label.
/// </summary>
public sealed class ActorAttributor
{
    public static readonly string[] Shells = ["pwsh.exe", "powershell.exe", "cmd.exe", "bash.exe", "wsl.exe", "zsh.exe", "sh.exe"];
    public static readonly TimeSpan EnterWindow = TimeSpan.FromSeconds(2);

    private readonly Func<IReadOnlyList<string>> _agents;

    /// <param name="agents">The <c>agents</c> group (exe globs or names, D-S3).</param>
    public ActorAttributor(Func<IReadOnlyList<string>> agents) => _agents = agents;

    /// <summary>Which agent exe is busy right now (from the agent watch), or null.</summary>
    public Func<string?> ActiveAgent { get; set; } = () => null;

    public bool IsAgent(string exe) => _agents().Any(a => Core.Apps.Glob.IsMatch(a.Contains('.', StringComparison.Ordinal) || a.Contains('*', StringComparison.Ordinal) ? a : a + ".exe", exe));

    public Actor ForKey(DecodedKey key)
    {
        if (!key.Injected)
        {
            return new Actor(ActorKind.Human, null, ActorEvidence.Hardware);
        }

        var evidence = ActorEvidence.InjectedFlag | (key.LowerIlInjected ? ActorEvidence.LowerIlInjected : ActorEvidence.None);
        return ActiveAgent() is { } agent
            ? new Actor(ActorKind.Agent, agent, evidence | ActorEvidence.AgentActive)
            : new Actor(ActorKind.Injected, null, evidence);
    }

    /// <summary>The run's actor: the majority of its keys; ties go to the non-human label (never under-report automation).</summary>
    public static Actor ForRun(IReadOnlyList<Actor> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
        {
            return new Actor(ActorKind.Unknown, null, ActorEvidence.None);
        }

        var human = keys.Count(a => a.Kind == ActorKind.Human);
        var other = keys.Where(a => a.Kind != ActorKind.Human).ToList();
        if (other.Count >= human)
        {
            var top = other.GroupBy(a => (a.Kind, a.Via)).OrderByDescending(g => g.Count()).First();
            return new Actor(top.Key.Kind, top.Key.Via, other.Aggregate(ActorEvidence.None, (e, a) => e | a.Evidence));
        }

        return new Actor(ActorKind.Human, null, ActorEvidence.Hardware);
    }

    /// <summary>
    /// A process start: <paramref name="chain"/> is the exe chain from the new process's parent upward;
    /// <paramref name="lastEnterInTerminal"/> is when a run in that terminal last ended with Enter.
    /// </summary>
    public Actor ForProcess(IReadOnlyList<string> chain, DateTimeOffset startedAt, DateTimeOffset? lastEnterInTerminal)
    {
        ArgumentNullException.ThrowIfNull(chain);
        foreach (var exe in chain)
        {
            if (IsAgent(exe))
            {
                return new Actor(ActorKind.Agent, exe.ToLowerInvariant(), ActorEvidence.AgentAncestor);
            }
        }

        var shell = chain.FirstOrDefault(e => Shells.Contains(e, StringComparer.OrdinalIgnoreCase));
        if (shell is not null)
        {
            var typed = lastEnterInTerminal is { } enter && startedAt >= enter && startedAt - enter <= EnterWindow;
            if (typed)
            {
                return new Actor(ActorKind.Cli, Path.GetFileNameWithoutExtension(shell).ToLowerInvariant(), ActorEvidence.ShellParent | ActorEvidence.EnterBefore);
            }
        }

        return chain.Count == 0 || chain.All(e => e.Equals("services.exe", StringComparison.OrdinalIgnoreCase) || e.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase) || e.Equals("wininit.exe", StringComparison.OrdinalIgnoreCase))
            ? new Actor(ActorKind.System, null, ActorEvidence.None)
            : new Actor(ActorKind.App, null, shell is null ? ActorEvidence.None : ActorEvidence.ShellParent);
    }
}
