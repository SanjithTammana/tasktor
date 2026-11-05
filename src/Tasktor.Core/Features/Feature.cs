using System.Text.Json.Nodes;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Pipe;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Snapshot;
using Tasktor.Core.Actions;
using Tasktor.Core.Logging;
using Tasktor.Core.Loop;
using Tasktor.Core.Rules;
using Tasktor.Core.Storage;

namespace Tasktor.Core.Features;

/// <summary>Feature tiers (catalog): Core is on with no setup; a Module is off until the user turns it on.</summary>
public enum FeatureTier
{
    Core,
    Module,
}

/// <summary>
/// What every feature gets from the composition root: the one loop, the scheduler, the router, state and the platform
/// services, all as plain members (no DI container; Native AOT). Loop-only unless noted.
/// </summary>
public sealed class FeatureContext
{
    public required LogicLoop Loop { get; init; }

    public required Scheduler Scheduler { get; init; }

    public required ActionRouter Router { get; init; }

    public required LogSink Log { get; init; }

    public required SettingsStore Settings { get; init; }

    public required StoreLayout Store { get; init; }

    public required Action<Func<SnapshotState, SnapshotState>> UpdateState { get; init; }

    public required Func<SnapshotState> State { get; init; }

    public required Action<string, Func<PullMessage, JsonNode?>> RegisterPull { get; init; }

    public INotifier Notifier { get; init; } = NullNotifier.Instance;

    public IProcessControl? Processes { get; init; }

    public InstallKey? Key { get; init; }

    /// <summary>Opens a URI, file or folder with the shell (Windows: ShellExecute). Thread-safe; returns false when it could not.</summary>
    public Func<string, bool> Launch { get; init; } = _ => false;

    /// <summary>Writes bytes to a path off the loop (the agent's coalescing writer).</summary>
    public Action<string, byte[]> Persist { get; init; } = (p, b) => AtomicFile.Write(p, b);

    /// <summary>Tells the agent that rule inputs changed (the agent re-evaluates rules, debounced).</summary>
    public Action RulesDirty { get; init; } = () => { };

    /// <summary>Whether a rule id is on in the current rule set (built-in watches honour <c>"enabled": false</c>).</summary>
    public Func<string, bool> RuleEnabled { get; init; } = _ => true;

    /// <summary>Runs a source read off the loop (sources only enqueue; the result is posted back). Tests run it inline.</summary>
    public Action<Action> OffLoop { get; init; } = work => Task.Run(work);

    public IClock Clock => Loop.Clock;
}

/// <summary>
/// A feature (A D-A5): an is-a over composition of shape classes. Enable takes its leases, Disable releases them and frees
/// buffers. It builds its cards (<see cref="ICardProvider"/>), writes its part of <c>state</c>, and contributes rule inputs.
/// </summary>
public abstract class Feature : ICardProvider
{
    protected Feature(FeatureContext context) => Context = context;

    public abstract string Id { get; }

    public abstract FeatureTier Tier { get; }

    public bool Enabled { get; private set; }

    protected FeatureContext Context { get; }

    public void Enable()
    {
        if (Enabled)
        {
            return;
        }

        Enabled = true;
        OnEnable();
    }

    public void Disable()
    {
        if (!Enabled)
        {
            return;
        }

        Enabled = false;
        OnDisable();
    }

    public abstract IReadOnlyList<Card> Cards();

    /// <summary>Add this feature's metrics, states, subjects, events and fields for one rule evaluation.</summary>
    public virtual void Contribute(RuleInputs inputs, DateTimeOffset now)
    {
    }

    protected abstract void OnEnable();

    protected abstract void OnDisable();
}

/// <summary>An <see cref="INotifier"/> that drops toasts (tests, headless runs).</summary>
public sealed class NullNotifier : INotifier
{
    public static NullNotifier Instance { get; } = new();

    public void Show(ToastRequest toast)
    {
    }

    public void Withdraw(string tag)
    {
    }
}

/// <summary>Card helpers shared by the built-in features.</summary>
public static class CardKit
{
    public static CardSource Core { get; } = new() { Kind = CardSourceKind.Core };

    public static CardSource ModuleSource { get; } = new() { Kind = CardSourceKind.Module };

    public static Value Bind(string pointer) => Value.Binding(pointer);

    public static Value Text(string text) => Value.Literal(text);

    public static Dictionary<string, string> Target(string key, string value) => new(StringComparer.Ordinal) { [key] = value };

    public static string SeverityWord(Severity s) => s switch { Severity.Critical => "critical", Severity.Warn => "warn", _ => "normal" };
}
