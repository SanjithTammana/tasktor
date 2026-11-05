using System.Text.Json.Nodes;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Pipe;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Snapshot;
using Tasktor.Core.Actions;
using Tasktor.Core.Features;
using Tasktor.Core.Logging;
using Tasktor.Core.Loop;
using Tasktor.Core.Plugins;
using Tasktor.Core.Rules;
using Tasktor.Core.State;
using Tasktor.Core.Storage;

namespace Tasktor.Tests.Shared;

/// <summary>Records toasts.</summary>
internal sealed class RecordingNotifier : INotifier
{
    public List<ToastRequest> Shown { get; } = [];

    public List<string> Withdrawn { get; } = [];

    public void Show(ToastRequest toast) => Shown.Add(toast);

    public void Withdraw(string tag) => Withdrawn.Add(tag);
}

/// <summary>An IProcessControl that records calls and answers from a script.</summary>
internal sealed class RecordingProcessControl : PortableProcessControl
{
    public RecordingProcessControl()
        : base(new ChildProcessRunner())
    {
    }

    public List<string> Calls { get; } = [];

    public Dictionary<ProcKey, ProcessPriority> Priorities { get; } = [];

    public HashSet<ProcKey> Frozen { get; } = [];

    public override IReadOnlyList<ProcessOutcome> End(IReadOnlyList<ProcKey> keys) => Record("end", keys);

    public override IReadOnlyList<ProcessOutcome> Close(IReadOnlyList<ProcKey> keys) => Record("close", keys);

    public override IReadOnlyList<ProcessOutcome> SetEfficiency(IReadOnlyList<ProcKey> keys, bool on) => Record(on ? "eco" : "eco_off", keys);

    public override IReadOnlyList<(ProcessOutcome Outcome, ProcessPriority Previous)> SetPriority(IReadOnlyList<ProcKey> keys, ProcessPriority priority)
    {
        Calls.Add("priority:" + priority + ":" + string.Join(",", keys.Select(k => k.Pid)));
        return keys.Select(k =>
        {
            var prev = Priorities.GetValueOrDefault(k, ProcessPriority.Normal);
            Priorities[k] = priority;
            return (new ProcessOutcome(k, ActionStatus.Ok), prev);
        }).ToList();
    }

    public override IReadOnlyList<ProcessOutcome> Freeze(string jobId, IReadOnlyList<ProcKey> tree)
    {
        Frozen.UnionWith(tree);
        return Record("freeze:" + jobId, tree);
    }

    public override IReadOnlyList<ProcessOutcome> Thaw(string jobId, IReadOnlyList<ProcKey> tree)
    {
        Frozen.ExceptWith(tree);
        return Record("thaw:" + jobId, tree);
    }

    public override IReadOnlyList<ProcessOutcome> CapCores(string capId, IReadOnlyList<ProcKey> keys, int maxCores) => Record("cap:" + maxCores, keys);

    private List<ProcessOutcome> Record(string what, IReadOnlyList<ProcKey> keys)
    {
        Calls.Add(what + ":" + string.Join(",", keys.Select(k => k.Pid)));
        return keys.Select(k => new ProcessOutcome(k, ActionStatus.Ok)).ToList();
    }
}

/// <summary>Everything a feature needs, driven by hand: manual loop, fake clock, inline off-loop work.</summary>
internal sealed class FeatureRig : IDisposable
{
    public FeatureRig()
    {
        Store = new TempStore();
        Loop = new LogicLoop(Clock);
        Scheduler = new Scheduler(Clock);
        Log = new LogSink(Clock, LogLines);
        Router = new ActionRouter(Loop, new OriginPolicy(), GuardChain.Default(), k => ProcessTable.GetValueOrDefault(k.Pid), Log, work => Task.FromResult(work()));
        Settings = new SettingsStore(Store.Layout);
        Rules = new RuleRunner(Loop, new RuleEngine(), Router, Notifier, Log, () => Features, change => State.Update(change));
        Context = new FeatureContext
        {
            Loop = Loop,
            Scheduler = Scheduler,
            Router = Router,
            Log = Log,
            Settings = Settings,
            Store = Store.Layout,
            UpdateState = change => State.Update(change),
            State = () => State.Current,
            RegisterPull = (name, handler) => Pulls[name] = handler,
            Notifier = Notifier,
            Processes = Processes,
            Key = InstallKey.FromBytes(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()),
            Launch = target =>
            {
                Launched.Add(target);
                return true;
            },
            Persist = (path, bytes) => AtomicFile.Write(path, bytes),
            RulesDirty = () => RulesDirtyCount++,
            RuleEnabled = id => Rules.Engine.Rules.IsOn(id),
            OffLoop = work => work(),
        };
    }

    public FakeClock Clock { get; } = new();

    public TempStore Store { get; }

    public LogicLoop Loop { get; }

    public Scheduler Scheduler { get; }

    public MemoryLogWriter LogLines { get; } = new();

    public LogSink Log { get; }

    public ActionRouter Router { get; }

    public SettingsStore Settings { get; }

    public StateTracker State { get; } = new();

    public RecordingNotifier Notifier { get; } = new();

    public RecordingProcessControl Processes { get; } = new();

    public Dictionary<uint, ProcessRow> ProcessTable { get; } = [];

    public Dictionary<string, Func<PullMessage, JsonNode?>> Pulls { get; } = [];

    public List<string> Launched { get; } = [];

    public List<Feature> Features { get; } = [];

    public RuleRunner Rules { get; }

    public FeatureContext Context { get; }

    public int RulesDirtyCount { get; private set; }

    public T Add<T>(T feature)
        where T : Feature
    {
        Features.Add(feature);
        feature.Enable();
        return feature;
    }

    /// <summary>Advance the clock and run the scheduler and any due timers.</summary>
    public void Advance(TimeSpan by, TimeSpan? step = null)
    {
        var s = step ?? by;
        var end = Clock.UtcNow + by;
        while (Clock.UtcNow < end)
        {
            Clock.Advance(s < end - Clock.UtcNow ? s : end - Clock.UtcNow);
            Pump();
        }
    }

    public void Pump()
    {
        Scheduler.Tick();
        Loop.RunDue();
        Loop.RunPending();
    }

    public ActionResult Act(ActionRequest req) => LoopPump.Run(Loop, Router.Submit(req));

    public void LoadRules(string? userText = null, bool withDefaults = true)
    {
        var defaults = withDefaults ? File.ReadAllText(Path.Combine(RepoPaths.Root, "src", "Tasktor.Agent", "rules.default.json")) : null;
        var set = RulesLoader.Load(defaults, userText);
        Assert.Empty(set.Errors);
        Rules.SetRules(set);
    }

    public void Dispose() => Store.Dispose();
}
