using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tasktor.Contracts;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Jobs;
using Tasktor.Contracts.Platform;
using Tasktor.Contracts.Snapshot;
using Tasktor.Core.Actions;
using Tasktor.Core.Features.SystemMonitor;
using Tasktor.Core.Learning;
using Tasktor.Core.Loop;
using Tasktor.Core.Rules;
using Tasktor.Core.Shapes;
using Tasktor.Core.Time;

namespace Tasktor.Core.Features.Jobs;

/// <summary>One live or finished job (B D-B11 JobRow fields + activity state). Loop-only.</summary>
public sealed class Job
{
    public required string Id { get; init; }

    public required ProcKey Root { get; set; }

    public KindId Kind { get; init; }

    public required string Label { get; set; }

    public required string Exe { get; init; }

    public Sensitive<string>? CommandLine { get; init; }

    public JobStatus Status { get; set; } = JobStatus.Working;

    public PauseMode Pause { get; set; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? EndedAt { get; set; }

    public int? ExitCode { get; set; }

    public bool EndedByUser { get; set; }

    public List<ProcKey> Workers { get; } = [];

    public double CpuTimeS { get; set; }

    public double CoresNow { get; set; }

    public double IoBps { get; set; }

    public ProcessPriority Priority { get; set; } = ProcessPriority.Normal;

    public bool Restrained { get; set; }

    public bool Hogging { get; set; }

    public DateTimeOffset LastEvidenceAt { get; set; }

    public TimeSpan LongestQuietGap { get; set; }

    public TimeSpan PausedTotal { get; set; }

    public DateTimeOffset? PausedAt { get; set; }

    public bool Flagged { get; set; }

    public bool SnoozedThisRun { get; set; }

    public bool FalseAlarmThisRun { get; set; }

    public bool OutOfDistribution { get; set; }

    public LearnedValue Silent { get; set; } = new(600, ValueSource.Default, 0, "silence.fallback", 600);

    public string? ProgressId { get; set; }

    public bool FileOnly { get; init; }

    public RateCounter Cpu { get; } = new();

    public RateCounter Io { get; } = new();

    public RingBuffer<float> Sparkline { get; } = new(60);

    public RingBuffer<float> CoresSamples { get; } = new(256);

    public HoldFor HogHold { get; } = new(TimeSpan.FromMinutes(2));

    public bool Live => Status is not (JobStatus.Done or JobStatus.Failed or JobStatus.Ended);

    public TimeSpan QuietFor(DateTimeOffset now) => Pause != PauseMode.None ? TimeSpan.Zero : now - LastEvidenceAt;
}

/// <summary>A candidate the gate is watching (not yet a job).</summary>
internal sealed class Candidate
{
    public required ProcKey Key { get; init; }

    public required string Exe { get; init; }

    public string? CommandLine { get; init; }

    public KindId Kind { get; init; }

    public DateTimeOffset FirstSeen { get; init; }

    public List<(DateTimeOffset At, double CpuS)> Samples { get; } = [];

    public DateTimeOffset NextProbe { get; set; }

    public bool ShownStarting { get; set; }

    public DateTimeOffset? StartingSince { get; set; }
}

/// <summary>
/// B: long-job watch, a thin <see cref="Feature"/> façade over the gate, the tracker, the activity monitor, the progress
/// joiner, the learner and the pause coordinator (D-B11). Always on (D-B2): a names-only scan rides the 60 s tick, per-process
/// handles only for watch-list matches; job reads every 10 s while a job exists and every 1–2 s while a viewer holds the
/// <c>jobs</c> lease; exits are pushed. Silent / stuck / auto-restrain decisions are rules over the subjects it contributes;
/// the B2.5 finished toast and the B1.7 hogging badge are built in.
/// </summary>
public sealed class JobWatch : Feature
{
    public const string SamplerName = "jobs";
    public const string FinishedRuleId = "job-finished";
    public static readonly TimeSpan JobRead = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan KeepFinished = TimeSpan.FromMinutes(10);
    public const int MaxFinished = 20;
    public const double EvidenceCores = 0.02;
    public const double EvidenceIoBytesPerRead = 64 * 1024;
    public static readonly TimeSpan FinishedToastMinRun = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan StuckFallback = TimeSpan.FromMinutes(10);

    private readonly SystemMonitor.Apps _apps;
    private readonly Func<ProcKey, ProcessRow?> _readOne;
    private readonly Func<ProcKey, string?> _commandLine;
    private readonly IEventSource<ProcessExit>? _exits;
    private readonly Action<ProcKey>? _watchExit;
    private readonly JobProgressSource? _progress;
    private readonly InternalTasks _tasks;
    private readonly KeyedHasher _hasher;
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, Candidate> _candidates = [];
    private readonly Dictionary<string, ProgressRow> _progressRows = new(StringComparer.Ordinal);
    private readonly HashSet<ProcKey> _ignored = [];
    private readonly List<RuleEvent> _events = [];
    private Dictionary<uint, uint> _parents = [];
    private DemandLease? _scan;
    private IDisposable? _jobTimer;
    private long _eventSeq;

    public JobWatch(
        FeatureContext context,
        SystemMonitor.Apps apps,
        Func<ProcKey, ProcessRow?> readOne,
        Func<ProcKey, string?> commandLine,
        IEventSource<ProcessExit>? exits,
        Action<ProcKey>? watchExit,
        JobProgressSource? progress,
        InternalTasks tasks)
        : base(context)
    {
        _apps = apps;
        _readOne = readOne;
        _commandLine = commandLine;
        _exits = exits;
        _watchExit = watchExit;
        _progress = progress;
        _tasks = tasks;
        _hasher = (context.Key ?? Storage.InstallKey.FromBytes(new byte[32])).Hasher("job.kind");
        Learner = new KindLearner(new KindStore(context.Key?.Fingerprint() ?? "none"));
        Gate = new JobGate();
    }

    public override string Id => "jobs";

    public override FeatureTier Tier => FeatureTier.Core;

    public JobGate Gate { get; }

    public KindLearner Learner { get; }

    public IReadOnlyCollection<Job> Jobs => _jobs.Values;

    /// <summary>GPU % for a PID (the tiered pre-flag check, B2.2).</summary>
    public Func<uint, double?>? GpuOf { get; set; }

    /// <summary>B2.5: is any of these processes (the job and its ancestors) the foreground window's? (one GetForegroundWindow).</summary>
    public Func<IReadOnlyList<uint>, bool>? WindowInFront { get; set; }

    public Job? Find(string id) => _jobs.GetValueOrDefault(id);

    protected override void OnEnable()
    {
        Gate.Configure(Context.Settings.Current.Jobs?.Watch ?? [], Context.Settings.Current.Jobs?.Excludes ?? []);
        Learner.LearningOn = Context.Settings.JobLearning;
        if (File.Exists(Context.Store.JobKinds))
        {
            Learner.Store.Load(File.ReadAllBytes(Context.Store.JobKinds));
        }

        _apps.Sampled += OnTable;
        _scan = Context.Scheduler.Lease(SystemMonitor.Apps.SamplerName, SystemTotals.Tick, ColumnSet.Names, holder: "jobs");
        if (!Context.Scheduler.IsRegistered(SamplerName))
        {
            Context.Scheduler.Register(SamplerName, _ => ReadJobs(Context.Clock.UtcNow));
        }

        _exits?.Start(e => Context.Loop.Post(() => OnExit(e)));
        _progress?.Start((rows, removed) => Context.Loop.Post(() => OnProgress(rows, removed)));
        _tasks.Changed += Publish;
        _apps.Targets.Job = id => _jobs.TryGetValue(id, out var j) && j.Live && !j.FileOnly
            ? new ResolvedTarget([j.Root, .. j.Workers], j.Label, j.Exe)
            : null;
        RegisterHandlers();
        RecoverFrozen();
        Context.RegisterPull("learned", _ => PullLearned());
    }

    protected override void OnDisable()
    {
        _apps.Sampled -= OnTable;
        _scan?.Dispose();
        _jobTimer?.Dispose();
        _exits?.Stop();
        _progress?.Dispose();
    }

    // ---------------- detection (60 s names-only scan) ----------------

    private void OnTable(KeyedTable<ProcKey, ProcessRow> table, ColumnSet columns)
    {
        if ((columns & ColumnSet.Names) == 0)
        {
            return;
        }

        Scan(table.Rows.Values.ToList(), Context.Clock.UtcNow);
    }

    /// <summary>One detection pass over the process names (tests call it directly).</summary>
    public void Scan(IReadOnlyList<ProcessRow> rows, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var byPid = rows.GroupBy(r => r.Key.Pid).ToDictionary(g => g.Key, g => g.First());
        _parents = byPid.ToDictionary(kv => kv.Key, kv => kv.Value.ParentPid);
        var bypassPids = _progressRows.Values.Where(p => p.Pid is not null && p.Invalid is null && p.State is not (ProgressState.Done or ProgressState.Error)).Select(p => p.Pid!.Value).ToHashSet();
        // A pinned process (job.pin, "Watch as a job") counts whatever its exe: the user chose it.
        bool Watched(ProcessRow r) => Gate.IsWatched(r.Exe) || bypassPids.Contains(r.Key.Pid) || Gate.IsPinnedPid(r.Key.Pid);

        // Topmost watch-list ancestor per watched process (B1.4: workers fold into the root).
        uint Root(ProcessRow r)
        {
            var cur = r;
            var seen = new HashSet<uint>();
            while (byPid.TryGetValue(cur.ParentPid, out var parent) && parent.Key.Pid != cur.Key.Pid && seen.Add(parent.Key.Pid) && Gate.IsWatched(parent.Exe))
            {
                cur = parent;
            }

            return cur.Key.Pid;
        }

        var trees = rows.Where(Watched).GroupBy(Root).ToDictionary(g => g.Key, g => g.Select(r => r.Key.Pid).ToList());
        var livePids = rows.Select(r => r.Key.Pid).ToHashSet();

        foreach (var (rootPid, members) in trees)
        {
            var existing = _jobs.Values.FirstOrDefault(j => j.Live && !j.FileOnly && j.Root.Pid == rootPid);
            if (existing is not null)
            {
                existing.Workers.Clear();
                foreach (var pid in members.Where(p => p != rootPid))
                {
                    if (_readOne(new ProcKey(pid, 0)) is { } w)
                    {
                        existing.Workers.Add(w.Key);
                    }
                }

                continue;
            }

            if (!_candidates.TryGetValue(rootPid, out var cand))
            {
                if (_readOne(new ProcKey(rootPid, 0)) is not { } fresh || _ignored.Contains(fresh.Key))
                {
                    continue;
                }

                var cmd = _commandLine(fresh.Key); // read once per (PID, creation time), never re-read
                cand = new Candidate
                {
                    Key = fresh.Key,
                    Exe = fresh.Exe,
                    CommandLine = cmd,
                    Kind = JobGate.Kind(_hasher, fresh.Exe, cmd),
                    FirstSeen = now,
                    NextProbe = now,
                };
                _candidates[rootPid] = cand;
            }

            if (now < cand.NextProbe)
            {
                continue; // B1.8: kinds learned idle are probed every 10 min
            }

            double treeCpu = 0;
            ProcessRow? rootRow = null;
            foreach (var pid in members)
            {
                if (_readOne(new ProcKey(pid, 0)) is { } r)
                {
                    treeCpu += r.CpuTimeS;
                    if (pid == rootPid)
                    {
                        rootRow = r;
                    }
                }
            }

            if (rootRow is null || rootRow.Key != cand.Key)
            {
                _candidates.Remove(rootPid); // exited or PID reused
                continue;
            }

            cand.Samples.Add((now, treeCpu));
            cand.Samples.RemoveAll(s => now - s.At > JobGate.Window + TimeSpan.FromMinutes(1));
            var bypass = bypassPids.Contains(rootPid);
            var verdict = Gate.Classify(cand.Exe, cand.CommandLine, cand.Key, cand.FirstSeen, cand.Samples, now, bypass, alreadyJob: false, Learner.ShowStarting(cand.Kind));
            cand.NextProbe = Learner.ProbeRarely(cand.Kind) ? now + TimeSpan.FromMinutes(10) : now;
            switch (verdict)
            {
                case GateVerdict.Job:
                    _candidates.Remove(rootPid);
                    Learner.OnAdmission(cand.Kind, becameJob: true, now);
                    Start(cand, rootRow, members.Where(p => p != rootPid).ToList(), now);
                    break;
                case GateVerdict.Starting when !cand.ShownStarting:
                    cand.ShownStarting = true;
                    cand.StartingSince = now;
                    break;
                case GateVerdict.Excluded:
                case GateVerdict.NotWatched:
                    _candidates.Remove(rootPid);
                    _ignored.Add(cand.Key);
                    break;
            }

            if (cand.ShownStarting && cand.StartingSince is { } since && now - since > TimeSpan.FromMinutes(5) && _candidates.ContainsKey(rootPid))
            {
                cand.ShownStarting = false; // shown early but never passed the gate: removed and counted (P4)
                cand.StartingSince = null;
                Learner.OnAdmission(cand.Kind, becameJob: false, now);
            }
        }

        foreach (var gone in _candidates.Keys.Where(p => !livePids.Contains(p)).ToList())
        {
            var c = _candidates[gone];
            if (now - c.FirstSeen >= JobGate.MinAge)
            {
                Learner.OnAdmission(c.Kind, becameJob: false, now); // B1.8: this kind stayed idle
            }

            _candidates.Remove(gone);
        }

        foreach (var j in _jobs.Values.Where(j => j.Live && !j.FileOnly && !livePids.Contains(j.Root.Pid)).ToList())
        {
            OnExit(new ProcessExit(j.Root, null, now)); // vanished without an exit event
        }

        Expire(now);
        EnsureJobTimer();
        Publish();
    }

    private void Start(Candidate c, ProcessRow root, List<uint> workers, DateTimeOffset now)
    {
        var id = "p" + c.Key;
        var job = new Job
        {
            Id = id,
            Root = c.Key,
            Kind = c.Kind,
            Exe = c.Exe,
            Label = JobGate.Label(c.Exe, c.CommandLine),
            CommandLine = c.CommandLine is null ? null : Sensitive.Of(c.CommandLine),
            StartedAt = c.Key.CreatedUtc ?? now,
            LastEvidenceAt = now,
            Priority = root.Priority,
            CpuTimeS = root.CpuTimeS,
        };
        job.Cpu.Update(c.Samples[^1].CpuS, now);
        foreach (var pid in workers)
        {
            if (_readOne(new ProcKey(pid, 0)) is { } w)
            {
                job.Workers.Add(w.Key);
            }
        }

        job.Silent = Learner.SilentFor(job.Kind, false);
        AttachProgress(job);
        _jobs[id] = job;
        _watchExit?.Invoke(job.Root);
        Event("job.new", job);
        Context.Log.Info("job.started", Logging.LogField.Of("exe", job.Exe));
    }

    // ---------------- job reads (10 s; 1–2 s with a viewer) ----------------

    private void EnsureJobTimer()
    {
        var anyLive = _jobs.Values.Any(j => j.Live && !j.FileOnly);
        if (anyLive && _jobTimer is null)
        {
            _jobTimer = Context.Loop.Every(JobRead, () => ReadJobs(Context.Clock.UtcNow));
        }
        else if (!anyLive && _jobTimer is not null)
        {
            _jobTimer.Dispose();
            _jobTimer = null;
        }
    }

    /// <summary>Read every live job's tree: CPU (cores), I/O, evidence of work, quiet time, hogging.</summary>
    public void ReadJobs(DateTimeOffset now)
    {
        var systemCpu = Context.State().System?.CpuPct ?? 0;
        foreach (var job in _jobs.Values.Where(j => j.Live).ToList())
        {
            var progressUpdated = job.ProgressId is { } pid && _progressRows.TryGetValue(pid, out var pr) && pr.UpdatedUtc > job.LastEvidenceAt;
            if (job.FileOnly)
            {
                if (progressUpdated)
                {
                    Evidence(job, now);
                }

                UpdateQuiet(job, now, busyCpu: false);
                continue;
            }

            double cpu = 0, io = 0;
            var alive = false;
            foreach (var k in job.Workers.Prepend(job.Root))
            {
                if (_readOne(k) is { } r && r.Key == k)
                {
                    cpu += r.CpuTimeS;
                    io += r.IoBytes;
                    if (k == job.Root)
                    {
                        alive = true;
                        job.Priority = r.Priority;
                    }
                }
            }

            if (!alive)
            {
                continue; // the exit event (or the next scan) ends it
            }

            var interval = job.Cpu.Rate is null ? JobRead : now - LastRead(job, now);
            var cores = job.Cpu.Update(cpu, now) ?? 0;
            var ioRate = job.Io.Update(io, now) ?? 0;
            job.CpuTimeS = cpu;
            job.CoresNow = Math.Round(cores, 2);
            job.IoBps = Math.Round(ioRate);
            job.Sparkline.Add((float)cores);
            job.CoresSamples.Add((float)cores);
            var ioEvidence = ioRate * Math.Max(1, interval.TotalSeconds) >= EvidenceIoBytesPerRead;
            var busy = cores >= EvidenceCores;
            if (job.Pause == PauseMode.None && (busy || ioEvidence || progressUpdated))
            {
                Evidence(job, now);
            }

            // B1.7 hogging: Normal priority, ≥ half the logical CPUs, system CPU ≥ 80 %, both for 2 min.
            var hogNow = job.Priority == ProcessPriority.Normal && cores >= 0.5 * Environment.ProcessorCount && systemCpu >= 80;
            job.Hogging = job.HogHold.Update(hogNow, now);
            UpdateQuiet(job, now, busy);
        }

        Publish();
        Context.RulesDirty();
    }

    private readonly Dictionary<string, DateTimeOffset> _lastRead = new(StringComparer.Ordinal);

    private DateTimeOffset LastRead(Job job, DateTimeOffset now)
    {
        var last = _lastRead.GetValueOrDefault(job.Id, now - JobRead);
        _lastRead[job.Id] = now;
        return last;
    }

    private static void Evidence(Job job, DateTimeOffset now)
    {
        var gap = now - job.LastEvidenceAt;
        if (gap > job.LongestQuietGap)
        {
            job.LongestQuietGap = gap;
        }

        job.LastEvidenceAt = now;
    }

    private void UpdateQuiet(Job job, DateTimeOffset now, bool busyCpu)
    {
        if (job.Pause != PauseMode.None)
        {
            job.Status = JobStatus.Paused;
            return;
        }

        var quiet = job.QuietFor(now);
        var previous = job.Status;
        if (job.ProgressId is { } pid && _progressRows.TryGetValue(pid, out var pr) && busyCpu && !job.FileOnly)
        {
            var limit = pr.HeartbeatS is { } hb ? TimeSpan.FromSeconds(2 * hb) : StuckFallback;
            if (now - pr.UpdatedUtc >= limit)
            {
                job.Status = JobStatus.StuckBusy;
                if (previous != JobStatus.StuckBusy)
                {
                    Event("job.stuck", job);
                }

                return;
            }
        }

        if (quiet <= TimeSpan.FromSeconds(1) || (busyCpu && !job.FileOnly))
        {
            if (previous is JobStatus.Silent or JobStatus.StuckBusy && job.Flagged)
            {
                job.FalseAlarmThisRun |= false;
            }

            job.Status = JobStatus.Working;
            return;
        }

        if (quiet.TotalSeconds >= job.Silent.Value && !job.SnoozedThisRun)
        {
            // B2.2: one GPU read before flagging; a busy GPU keeps it "working (GPU)" and the time does not count as quiet.
            if (!job.FileOnly && GpuOf?.Invoke(job.Root.Pid) is > 5)
            {
                job.Status = JobStatus.WorkingGpu;
                job.LastEvidenceAt = now;
                return;
            }

            if (previous != JobStatus.Silent)
            {
                job.Flagged = true;
                Event("job.silent", job);
            }

            job.Status = JobStatus.Silent;
            return;
        }

        job.Status = JobStatus.Quiet;
    }

    // ---------------- exits and run outcomes ----------------

    public void OnExit(ProcessExit exit)
    {
        ArgumentNullException.ThrowIfNull(exit);
        var job = _jobs.Values.FirstOrDefault(j => j.Live && !j.FileOnly && j.Root == exit.Key);
        if (job is null)
        {
            return;
        }

        job.ExitCode = exit.ExitCode;
        job.EndedAt = exit.AtUtc;
        job.Status = job.EndedByUser ? JobStatus.Ended : exit.ExitCode is 0 ? JobStatus.Done : exit.ExitCode is null ? JobStatus.Ended : JobStatus.Failed;
        Finish(job, exit.AtUtc);
    }

    private void Finish(Job job, DateTimeOffset at)
    {
        if (job.PausedAt is { } p)
        {
            job.PausedTotal += at - p;
            job.PausedAt = null;
        }

        var duration = (at - job.StartedAt) - job.PausedTotal;
        var good = job.Status == JobStatus.Done && !job.EndedByUser && !(job.Flagged && !job.FalseAlarmThisRun && job.ExitCode != 0);
        if (job.Flagged && !job.SnoozedThisRun)
        {
            // Label the alarm: a flagged job that resumed and exited 0 was a false alarm; ended or failed, a true one.
            var falseAlarm = job.Status == JobStatus.Done;
            Learner.OnAlarmLabelled(job.Kind, falseAlarm, shadowFalse: falseAlarm && job.LongestQuietGap.TotalSeconds >= 600, at);
            job.FalseAlarmThisRun = falseAlarm;
        }

        var median = job.CoresSamples.Count == 0 ? 0 : WindowStats.Median(job.CoresSamples);
        Learner.OnRunEnded(job.Kind, new RunOutcome
        {
            Good = good,
            QuietMaxS = job.LongestQuietGap.TotalSeconds,
            DurationS = Math.Max(0, duration.TotalSeconds),
            CoresMedian = median,
            LoweredByHand = job.Restrained,
        }, job.FalseAlarmThisRun, at);
        Persist();
        Event(job.Status == JobStatus.Failed ? "job.failed" : "job.done", job);
        FinishedToast(job, duration);
        Context.Log.Info("job.finished", Logging.LogField.Of("exe", job.Exe), Logging.LogField.Of("exit", job.ExitCode ?? -1));
        EnsureJobTimer();
        Publish();
    }

    /// <summary>B2.5: only for jobs that ran ≥ 10 min, not ended by the user, and whose window is not in front.</summary>
    private void FinishedToast(Job job, TimeSpan duration)
    {
        if (job.FileOnly || job.EndedByUser || duration < FinishedToastMinRun || !Context.RuleEnabled(FinishedRuleId) || Context.Settings.Current.Jobs?.FinishedToast == false)
        {
            return;
        }

        // The job and its ancestors (its console host / terminal, found through the process tree).
        var chain = new List<uint> { job.Root.Pid };
        var cur = job.Root.Pid;
        for (var i = 0; i < 8 && _parents.TryGetValue(cur, out var parent) && parent != 0 && !chain.Contains(parent); i++)
        {
            chain.Add(parent);
            cur = parent;
        }

        if (WindowInFront?.Invoke(chain) == true)
        {
            return;
        }

        var failed = job.Status == JobStatus.Failed;
        var tag = FinishedRuleId + ":" + job.Id;
        var text = job.Label + (failed ? " failed after " : " finished after ") + DurationText.Humanize(duration) + " (exit " + (job.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "?") + ")";
        var buttons = new List<ToastButton> { new() { Label = "Open folder", Request = new ActionRequest { Action = "job.folder", Origin = Origin.Toast, Toast = tag, Target = CardKit.Target("job", job.Id) } } };
        if (job.ProgressId is { } p && _progressRows.TryGetValue(p, out var pr) && pr.LogPath is not null)
        {
            buttons.Add(new ToastButton { Label = "Open log", Request = new ActionRequest { Action = "job.log", Origin = Origin.Toast, Toast = tag, Target = CardKit.Target("job", job.Id) } });
        }

        foreach (var b in buttons)
        {
            Context.Router.Toasts.Offer(tag, b.Request);
        }

        Context.Notifier.Show(new ToastRequest { Tag = tag, Title = text, Severity = failed ? Severity.Warn : Severity.Normal, Buttons = buttons });
    }

    private void Expire(DateTimeOffset now)
    {
        var finished = _jobs.Values.Where(j => !j.Live).OrderByDescending(j => j.EndedAt).ToList();
        foreach (var (j, i) in finished.Select((j, i) => (j, i)))
        {
            if (i >= MaxFinished || (j.EndedAt is { } e && now - e > KeepFinished))
            {
                _jobs.Remove(j.Id);
                _lastRead.Remove(j.Id);
            }
        }
    }

    private void Persist()
    {
        if (Learner.Store.Dirty)
        {
            Context.Persist(Context.Store.JobKinds, Learner.Store.Serialize());
        }
    }

    // ---------------- progress files ----------------

    public void OnProgress(IReadOnlyList<ProgressRow> rows, IReadOnlyList<string> removed)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(removed);
        var now = Context.Clock.UtcNow;
        foreach (var id in removed)
        {
            _progressRows.Remove(id);
            if (_jobs.TryGetValue("f-" + id, out var fj) && fj.Live)
            {
                fj.Status = JobStatus.Done;
                fj.EndedAt = now;
            }
        }

        foreach (var row in rows)
        {
            _progressRows[row.Id] = row;
            var owner = _jobs.Values.FirstOrDefault(j => j.Live && !j.FileOnly && row.Pid == j.Root.Pid && j.StartedAt <= row.UpdatedUtc);
            if (owner is not null)
            {
                owner.ProgressId = row.Id;
                if (row.Label != row.Id)
                {
                    owner.Label = row.Label;
                }

                continue;
            }

            if (row.Pid is not null && row.Invalid is null && row.State is not (ProgressState.Done or ProgressState.Error))
            {
                continue; // names a process: bypasses the gate at the next scan
            }

            // A file-only row (no pid, or an invalid file): its own job, silence from "updated".
            if (!_jobs.TryGetValue("f-" + row.Id, out var fileJob))
            {
                fileJob = new Job { Id = "f-" + row.Id, Root = default, Label = row.Label, Exe = "progress", FileOnly = true, StartedAt = row.UpdatedUtc, LastEvidenceAt = row.UpdatedUtc, ProgressId = row.Id };
                _jobs[fileJob.Id] = fileJob;
            }

            fileJob.Label = row.Invalid is null ? row.Label : "Invalid progress file " + row.Id;
            Evidence(fileJob, row.UpdatedUtc > fileJob.LastEvidenceAt ? row.UpdatedUtc : fileJob.LastEvidenceAt);
            if (row.State is ProgressState.Done or ProgressState.Error && fileJob.Live)
            {
                fileJob.Status = row.State == ProgressState.Done ? JobStatus.Done : JobStatus.Failed;
                fileJob.EndedAt = now;
                FinishedToast(fileJob, now - fileJob.StartedAt);
            }
        }

        Publish();
        Context.RulesDirty();
    }

    /// <summary>Files whose process is gone and that were not updated for 24 h are deleted (Tasktor owns <c>jobs\</c>; nothing outside it).</summary>
    public int CleanStaleFiles(DateTimeOffset now)
    {
        var n = 0;
        foreach (var row in _progressRows.Values.ToList())
        {
            var processGone = row.Pid is not { } p || _readOne(new ProcKey(p, 0)) is null;
            if (processGone && now - row.UpdatedUtc > TimeSpan.FromHours(24))
            {
                var path = Context.Store.JobFile(row.Id);
                if (File.Exists(path))
                {
                    File.Delete(path);
                    n++;
                }

                _progressRows.Remove(row.Id);
                _jobs.Remove("f-" + row.Id);
            }
        }

        return n;
    }

    private void AttachProgress(Job job)
    {
        var row = _progressRows.Values.Where(r => r.Pid == job.Root.Pid).OrderByDescending(r => r.UpdatedUtc).FirstOrDefault();
        if (row is not null)
        {
            job.ProgressId = row.Id;
            if (row.Label != row.Id)
            {
                job.Label = row.Label;
            }
        }
    }

    // ---------------- snapshot, cards, rules ----------------

    private void Event(string name, Job job)
    {
        _eventSeq++;
        _events.Add(new RuleEvent
        {
            Name = name,
            Id = job.Id + "#" + _eventSeq.ToString(CultureInfo.InvariantCulture),
            Fields = new Dictionary<string, string>(StringComparer.Ordinal) { ["job"] = job.Label, ["process"] = job.Exe },
            Target = CardKit.Target("job", job.Id),
        });
    }

    public override void Contribute(RuleInputs inputs, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var live = _jobs.Values.Where(j => j.Live).ToList();
        inputs.Metric("jobs.running", live.Count);
        inputs.Metric("jobs.silent", live.Count(j => j.Status == JobStatus.Silent));
        inputs.State("jobs.running", live.Count > 0);
        inputs.State("jobs.silent", live.Any(j => j.Status == JobStatus.Silent));
        foreach (var j in live)
        {
            var quiet = j.SnoozedThisRun || j.Pause != PauseMode.None || j.Status is JobStatus.Working or JobStatus.WorkingGpu ? 0 : j.QuietFor(now).TotalSeconds;
            double stuck = 0;
            LearnedValue? hb = null;
            if (j.ProgressId is { } pid && _progressRows.TryGetValue(pid, out var pr))
            {
                var limit = pr.HeartbeatS is { } h ? 2.0 * h : StuckFallback.TotalSeconds;
                hb = new LearnedValue(limit, ValueSource.Default, 0, "heartbeat", pr.HeartbeatS ?? 0);
                stuck = j.Status == JobStatus.StuckBusy ? (now - pr.UpdatedUtc).TotalSeconds : 0;
            }

            var thresholds = new Dictionary<string, LearnedValue>(StringComparer.Ordinal)
            {
                ["silence"] = j.Silent,
                ["hogging"] = new LearnedValue(1, ValueSource.Default, 0, "hogging", 1),
            };
            if (hb is not null)
            {
                thresholds["heartbeat"] = hb;
            }

            if (Learner.Eta(j.Kind) is { } eta)
            {
                thresholds["duration"] = eta with { Value = 1.5 * eta.Value };
            }

            inputs.Jobs.Add(new RuleSubject
            {
                Id = j.Id,
                Name = j.Exe,
                Values = new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    ["silence"] = quiet,
                    ["heartbeat"] = stuck,
                    ["hogging"] = j.Hogging ? 1 : 0,
                    ["duration"] = (now - j.StartedAt - j.PausedTotal).TotalSeconds,
                },
                Thresholds = thresholds,
                Fields = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["job"] = j.Label,
                    ["process"] = j.Exe,
                    ["basis"] = Basis(j),
                },
                Target = CardKit.Target("job", j.Id),
            });
        }

        inputs.Events.AddRange(_events);
        _events.Clear();
    }

    /// <summary>The explanation line (B: "silent 14 min: longer than any quiet gap in its last 12 runs (max 9 min) · learned").</summary>
    public static string Basis(Job j) => j.Silent.Source switch
    {
        ValueSource.Learned or ValueSource.Blended => "its longest normal pause is " + DurationText.Humanize(TimeSpan.FromSeconds(j.Silent.BasisValue)) + " (" + j.Silent.Runs + " runs) · learned",
        ValueSource.FallbackGuardrail => "default limit (learning paused for this kind)",
        _ => "new job, default limit",
    };

    private void Publish()
    {
        var now = Context.Clock.UtcNow;
        var rows = _jobs.Values
            .OrderBy(j => j.Live ? 0 : 1)
            .ThenBy(j => j.Status == JobStatus.Silent ? 0 : j.Hogging ? 1 : 2)
            .ThenBy(j => j.StartedAt)
            .Select(j => Row(j, now))
            .Concat(_tasks.Rows.Select(t => new JobState
            {
                Id = "t-" + t.Id,
                Label = t.Label,
                Status = JobStatus.Working,
                StartedAt = t.StartedUtc,
                Stage = t.Stage,
                Msg = t.Msg,
                Pct = t.Pct,
                Internal = true,
            }))
            .ToList();
        Context.UpdateState(s => s with { Jobs = rows.Count == 0 ? null : rows });
    }

    private JobState Row(Job j, DateTimeOffset now)
    {
        _progressRows.TryGetValue(j.ProgressId ?? string.Empty, out var pr);
        var elapsed = (j.EndedAt ?? now) - j.StartedAt - j.PausedTotal;
        long? eta = pr?.EtaS;
        ValueSource? etaSource = eta is null ? null : ValueSource.Default;
        int? etaRuns = null;
        if (eta is null && Learner.Eta(j.Kind) is { } learned && j.Live)
        {
            eta = (long)Math.Max(0, learned.Value - elapsed.TotalSeconds);
            etaSource = ValueSource.Learned;
            etaRuns = learned.Runs;
        }

        return new JobState
        {
            Id = j.Id,
            Label = j.Label,
            Status = j.Status,
            Pause = j.Pause,
            StartedAt = j.StartedAt,
            CoresNow = j.CoresNow,
            CpuTimeS = Math.Round(j.CpuTimeS, 1),
            IoBps = j.IoBps,
            Priority = j.Priority,
            Restrained = j.Restrained ? true : null,
            Hogging = j.Hogging ? true : null,
            QuietS = j.Status is JobStatus.Quiet or JobStatus.Silent ? (long)j.QuietFor(now).TotalSeconds : null,
            PausedS = j.PausedAt is { } p ? (long)(now - p).TotalSeconds : null,
            ExitCode = j.ExitCode,
            Workers = j.Workers.Count,
            Pct = pr?.Pct,
            Stage = pr?.Stage,
            Msg = pr?.Invalid ?? pr?.Msg,
            EtaS = eta,
            EtaSource = etaSource,
            EtaRuns = etaRuns,
            LongerThanUsual = j.Live && Learner.LongerThanUsual(j.Kind, elapsed) ? true : null,
            SilentSource = j.Silent.Source,
            Basis = j.Status == JobStatus.Silent ? "silent " + DurationText.Humanize(j.QuietFor(now)) + ": " + Basis(j) : null,
            HasLog = pr?.LogPath is not null ? true : null,
        };
    }

    public override IReadOnlyList<Card> Cards()
    {
        var live = _jobs.Values.Count(j => j.Live);
        var silent = _jobs.Values.Count(j => j.Status == JobStatus.Silent);
        return
        [
            new Card
            {
                Id = "core.jobs",
                Source = CardKit.Core,
                Title = "Jobs",
                Icon = IconKey.Jobs,
                Severity = Value.Literal(silent > 0 ? "warn" : "normal"),
                Blocks =
                [
                    new TableBlock
                    {
                        Columns =
                        [
                            new TableColumn { Key = "label", Label = "Job" },
                            new TableColumn { Key = "status", Label = "State" },
                            new TableColumn { Key = "pct", Label = "Progress", Unit = "%" },
                            new TableColumn { Key = "cores_now", Label = "Cores" },
                        ],
                        Rows = Value.Binding("/state/jobs"),
                        Max = 6,
                        RowKey = "id",
                        Priority = 1,
                    },
                    new MetricBlock { Label = "Running", Value = Value.Literal(live), Priority = 2, Severity = Value.Literal(silent > 0 ? "warn" : "normal") },
                    new BadgeBlock { Text = Value.Literal(live == 0 ? "No jobs" : live + " job" + (live == 1 ? string.Empty : "s") + (silent > 0 ? " · " + silent + " silent" : string.Empty)), Priority = 1 },
                ],
                Actions =
                [
                    new CardAction { Id = "pause", Label = "Pause", Action = "job.pause", Target = CardKit.Target("job", "{row}"), Priority = 1 },
                    new CardAction { Id = "resume", Label = "Resume", Action = "job.resume", Target = CardKit.Target("job", "{row}"), Priority = 2 },
                    new CardAction { Id = "log", Label = "Open log", Action = "job.log", Target = CardKit.Target("job", "{row}"), Priority = 3 },
                    new CardAction { Id = "end", Label = "End…", Action = "job.end", Target = CardKit.Target("job", "{row}"), Priority = 4, Destructive = true },
                ],
                Link = "tasktor://open?card=core.jobs",
                StaleAfterS = 120,
            },
        ];
    }

    private JsonNode PullLearned()
    {
        var kinds = new JsonArray();
        foreach (var (kind, s) in Learner.Store.All)
        {
            var liveName = _jobs.Values.FirstOrDefault(j => j.Kind == kind)?.Label;
            kinds.Add((JsonNode)new JsonObject
            {
                ["kind"] = kind.ToString(),
                ["name"] = liveName ?? "kind seen " + s.LastSeenUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["runs"] = s.Runs,
                ["good_runs"] = s.GoodRuns,
                ["usual_duration_s"] = s.Duration.Count == 0 ? null : Math.Round(WindowStats.Median(s.Duration)),
                ["silent_threshold_s"] = Math.Round(KindLearner.Silent.Current(s, Learner.LearningOn).Value),
                ["source"] = KindLearner.Silent.Current(s, Learner.LearningOn).Source.ToString().ToLowerInvariant(),
                ["false_alarms"] = s.FalseAlarms,
                ["mode"] = s.Mode.ToString().ToLowerInvariant(),
            });
        }

        var log = new JsonArray();
        foreach (var g in Learner.Guardrails.TakeLast(50))
        {
            log.Add((JsonNode)new JsonObject { ["kind"] = g.Kind.ToString(), ["at"] = g.At.ToString("O", CultureInfo.InvariantCulture), ["action"] = g.Action, ["reason"] = g.Reason });
        }

        return new JsonObject { ["learning"] = Learner.LearningOn, ["kinds"] = kinds, ["guardrails"] = log };
    }

    // ---------------- pause / freeze (B3.7) ----------------

    private void RecoverFrozen()
    {
        if (Context.Processes is not { } pc)
        {
            return;
        }

        Context.OffLoop(() =>
        {
            var thawed = pc.RecoverFrozen();
            if (thawed.Count > 0)
            {
                Context.Loop.Post(() => Context.Log.Info("jobs.recovered_frozen", Logging.LogField.Of("count", thawed.Count)));
            }
        });
    }

    /// <summary>Polite when the job has a progress file (the helper checks the control file), else freeze.</summary>
    public PauseMode PickMode(Job job, string? requested) => requested switch
    {
        "freeze" => PauseMode.Frozen,
        "polite" => PauseMode.Polite,
        _ => job.ProgressId is not null && !job.FileOnly ? PauseMode.Polite : PauseMode.Frozen,
    };

    private void WriteControl(Job job, bool pause)
    {
        var id = job.ProgressId ?? job.Id;
        Context.Persist(Context.Store.JobControlFile(id), JsonSerializer.SerializeToUtf8Bytes(new JobControlFile { Pause = pause, Message = pause ? "paused by Tasktor" : null }, ContractsJsonContext.Default.JobControlFile));
    }

    /// <summary>Quit resumes every paused job first (B, U4 tray "Quit Tasktor").</summary>
    public void ResumeAll()
    {
        foreach (var j in _jobs.Values.Where(j => j.Live && j.Pause != PauseMode.None))
        {
            if (j.Pause == PauseMode.Polite)
            {
                WriteControl(j, false);
            }
            else
            {
                Context.Processes?.Thaw(j.Id, [j.Root, .. j.Workers]);
            }

            j.Pause = PauseMode.None;
        }
    }

    private void RegisterHandlers()
    {
        var router = Context.Router;
        PreparedAction ForJob(ActionRequest req, Func<Job, PreparedAction> make)
        {
            var id = req.Target?.GetValueOrDefault("job");
            return id is not null && _jobs.TryGetValue(id, out var j)
                ? make(j)
                : new PreparedAction { Summary = "job", Refusal = "No such job (it may have finished)" };
        }

        router.Register("job.pause", req => ForJob(req, j =>
        {
            if (!j.Live || j.Pause != PauseMode.None)
            {
                return new PreparedAction { Summary = "Pause " + j.Label, Refusal = j.Live ? "Already paused" : "The job has finished" };
            }

            var mode = PickMode(j, req.Args?.GetValueOrDefault("mode"));
            if (mode == PauseMode.Polite)
            {
                return new PreparedAction
                {
                    Summary = "Pause " + j.Label + " at its next safe point",
                    TargetName = j.Exe,
                    Immediate = new ActionResult { Action = "job.pause", Status = ActionStatus.Ok, Message = "Asked " + j.Label + " to pause at its next update" },
                    Done = _ => MarkPaused(j, PauseMode.Polite),
                };
            }

            var tree = new List<ProcKey> { j.Root };
            tree.AddRange(j.Workers);
            return new PreparedAction
            {
                Summary = "Freeze " + j.Label + (j.Workers.Count > 0 ? " + " + j.Workers.Count + " workers" : string.Empty),
                TargetName = j.Exe,
                Processes = tree,
                Run = keys => SystemMonitor.Apps.Summarize("job.pause", "Freeze", j.Label, tree.Count, Context.Processes!.Freeze(j.Id, keys)),
                Done = r =>
                {
                    if (r.Status is ActionStatus.Ok or ActionStatus.Partial)
                    {
                        MarkPaused(j, PauseMode.Frozen);
                    }
                },
            };
        }));
        router.Register("job.resume", req => ForJob(req, j =>
        {
            if (j.Pause == PauseMode.None)
            {
                return new PreparedAction { Summary = "Resume", Refusal = "Not paused" };
            }

            var polite = j.Pause == PauseMode.Polite;
            var tree = new List<ProcKey> { j.Root };
            tree.AddRange(j.Workers);
            return new PreparedAction
            {
                Summary = "Resume " + j.Label,
                TargetName = j.Exe,
                Processes = polite ? [] : tree,
                Immediate = polite ? new ActionResult { Action = "job.resume", Status = ActionStatus.Ok, Message = "Resumed " + j.Label } : null,
                Run = polite ? null : keys => SystemMonitor.Apps.Summarize("job.resume", "Resume", j.Label, tree.Count, Context.Processes!.Thaw(j.Id, keys)),
                Done = r =>
                {
                    if (r.Status is ActionStatus.Ok or ActionStatus.Partial)
                    {
                        MarkResumed(j); // a refused or failed resume leaves the job paused, so Resume can be tried again
                    }
                },
            };
        }));
        router.Register("job.end", req => ForJob(req, j =>
        {
            var tree = ProcessTargets.LeafFirst([j.Root, .. j.Workers], _apps.Table);
            return new PreparedAction
            {
                Summary = "End " + j.Label + (j.Workers.Count > 0 ? " + " + j.Workers.Count + " workers" : string.Empty) + "?",
                TargetName = j.Exe,
                Processes = tree,
                Run = keys => SystemMonitor.Apps.Summarize("job.end", "End", j.Label, tree.Count, Context.Processes!.End(keys)),
                Done = r =>
                {
                    if (r.Status is ActionStatus.Ok or ActionStatus.Partial)
                    {
                        j.EndedByUser = true;
                    }
                },
            };
        }));
        router.Register("job.log", req => ForJob(req, j =>
        {
            var log = j.ProgressId is { } p && _progressRows.TryGetValue(p, out var pr) ? pr.LogPath : null;
            return log is null
                ? new PreparedAction { Summary = "Open log", Refusal = "This job names no log" }
                : new PreparedAction { Summary = "Open log", Run = _ => new ActionResult { Action = "job.log", Status = Context.Launch(log) ? ActionStatus.Ok : ActionStatus.Failed } };
        }));
        router.Register("job.folder", req => ForJob(req, j =>
        {
            var script = JobGate.Tokenize(j.CommandLine?.Reveal()).Skip(1).FirstOrDefault(t => !t.StartsWith('-'));
            var folder = script is not null && Path.IsPathRooted(script) ? Path.GetDirectoryName(script) : null;
            folder ??= _readOne(j.Root)?.Path is { } exe ? Path.GetDirectoryName(exe) : null;
            return folder is null
                ? new PreparedAction { Summary = "Open folder", Refusal = "The folder is not known" }
                : new PreparedAction { Summary = "Open folder", Run = _ => new ActionResult { Action = "job.folder", Status = Context.Launch(folder) ? ActionStatus.Ok : ActionStatus.Failed } };
        }));
        router.Register("job.pin", req =>
        {
            if (!uint.TryParse(req.Target?.GetValueOrDefault("pid"), out var pid) || _readOne(new ProcKey(pid, 0)) is not { } row)
            {
                return new PreparedAction { Summary = "Watch as job", Refusal = "No such process" };
            }

            return new PreparedAction
            {
                Summary = "Watch " + row.Exe + " as a job",
                Immediate = new ActionResult { Action = "job.pin", Status = ActionStatus.Ok, Message = "Watching " + row.Exe + " as a job" },
                Done = _ =>
                {
                    Gate.Pin(row.Key);
                    _ignored.Remove(row.Key);
                },
            };
        });
        router.Register("job.unpin", req => new PreparedAction
        {
            Summary = "Stop watching",
            Immediate = new ActionResult { Action = "job.unpin", Status = ActionStatus.Ok },
            Done = _ =>
            {
                if (uint.TryParse(req.Target?.GetValueOrDefault("pid"), out var pid) && _readOne(new ProcKey(pid, 0)) is { } row)
                {
                    Gate.Unpin(row.Key);
                }
            },
        });
        router.Register("job.cancel", req =>
        {
            var id = req.Target?.GetValueOrDefault("job") ?? string.Empty;
            return id.StartsWith("t-", StringComparison.Ordinal)
                ? new PreparedAction { Summary = "Cancel", Immediate = new ActionResult { Action = "job.cancel", Status = _tasks.Cancel(id[2..]) ? ActionStatus.Ok : ActionStatus.Failed } }
                : new PreparedAction { Summary = "Cancel", Refusal = "Only Tasktor's own tasks can be cancelled here" };
        });
        router.Register("rule.its_fine", req => ForJob(req, j => new PreparedAction
        {
            Summary = "It's fine",
            Immediate = new ActionResult { Action = "rule.its_fine", Status = ActionStatus.Ok, Message = "Okay: no more silent alerts for this run" },
            Done = _ =>
            {
                // B2.4: snooze this run and teach the policy it was a false alarm.
                j.SnoozedThisRun = true;
                j.FalseAlarmThisRun = true;
                var now = Context.Clock.UtcNow;
                Learner.OnAlarmLabelled(j.Kind, falseAlarm: true, shadowFalse: j.QuietFor(now).TotalSeconds >= 600, now);
                Persist();
                Context.RulesDirty();
            },
        }));
        router.Register("learning.set", req => new PreparedAction
        {
            Summary = "Learn from my jobs",
            Immediate = new ActionResult { Action = "learning.set", Status = ActionStatus.Ok },
            Done = _ => Learner.LearningOn = req.Args?.GetValueOrDefault("on") != "false",
        });
        router.Register("learned.forget", req => new PreparedAction
        {
            Summary = "Forget what Tasktor learned" + (req.Target?.GetValueOrDefault("kind") is null ? " about all job kinds" : " about this kind"),
            Immediate = new ActionResult { Action = "learned.forget", Status = ActionStatus.Ok, Message = "Forgotten" },
            Done = _ =>
            {
                if (KindOf(req) is { } k)
                {
                    Learner.Store.Forget(k);
                }
                else
                {
                    Learner.Store.ForgetAll();
                }

                Persist();
            },
        });
        router.Register("learned.relearn", req => KindOf(req) is { } kind
            ? new PreparedAction { Summary = "Relearn", Immediate = new ActionResult { Action = "learned.relearn", Status = ActionStatus.Ok }, Done = _ => Learner.Relearn(kind, Context.Clock.UtcNow) }
            : new PreparedAction { Summary = "Relearn", Refusal = "Which kind?" });
        router.Register("learned.use_fallback", req => KindOf(req) is { } kind
            ? new PreparedAction { Summary = "Use default", Immediate = new ActionResult { Action = "learned.use_fallback", Status = ActionStatus.Ok }, Done = _ => Learner.UseFallback(kind, Context.Clock.UtcNow, "rule " + req.Rule) }
            : new PreparedAction { Summary = "Use default", Refusal = "Which kind?" });
    }

    private KindId? KindOf(ActionRequest req)
    {
        if (req.Target?.GetValueOrDefault("kind") is { } hex && KindId.TryParse(hex, out var k))
        {
            return k;
        }

        return req.Target?.GetValueOrDefault("job") is { } id && _jobs.TryGetValue(id, out var j) ? j.Kind : null;
    }

    private void MarkPaused(Job j, PauseMode mode)
    {
        if (mode == PauseMode.Polite)
        {
            WriteControl(j, true);
        }

        j.Pause = mode;
        j.PausedAt = Context.Clock.UtcNow;
        j.Status = JobStatus.Paused;
        Publish();
    }

    private void MarkResumed(Job j)
    {
        if (j.Pause == PauseMode.Polite)
        {
            WriteControl(j, false);
        }

        var now = Context.Clock.UtcNow;
        if (j.PausedAt is { } p)
        {
            j.PausedTotal += now - p; // paused time is not quiet time and not run time
        }

        j.PausedAt = null;
        j.Pause = PauseMode.None;
        j.LastEvidenceAt = now;
        j.Status = JobStatus.Working;
        Publish();
    }
}
