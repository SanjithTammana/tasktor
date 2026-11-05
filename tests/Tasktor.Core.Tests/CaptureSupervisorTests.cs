using Tasktor.Contracts;
using Tasktor.Contracts.Platform;
using Tasktor.Core.Features;
using Tasktor.Core.Plugins;
using Tasktor.Tests.Shared;

namespace Tasktor.Core.Tests;

/// <summary>U1 D-H9: the agent starts tasktor-capture only while wanted and consented, restarts a crash ≤ 3 / hour with backoff, then reports.</summary>
public sealed class CaptureSupervisorTests : IDisposable
{
    private readonly FeatureRig _rig = new();
    private readonly FakeWorkers _workers = new();
    private readonly List<Action> _offLoop = [];
    private readonly FeatureContext _context;
    private bool _consent = true;

    public CaptureSupervisorTests()
    {
        var c = _rig.Context;
        _context = new FeatureContext
        {
            Loop = c.Loop,
            Scheduler = c.Scheduler,
            Router = c.Router,
            Log = c.Log,
            Settings = c.Settings,
            Store = c.Store,
            UpdateState = c.UpdateState,
            State = c.State,
            RegisterPull = c.RegisterPull,
            Processes = _workers,
            OffLoop = _offLoop.Add, // the wait runs when the test lets it
        };
    }

    public void Dispose() => _rig.Dispose();

    /// <summary>Started workers stay "running" until the test ends them with an exit code.</summary>
    private sealed class FakeWorkers() : PortableProcessControl(new ChildProcessRunner())
    {
        private readonly Dictionary<string, TaskCompletionSource<WorkerExit>> _running = [];

        public List<WorkerSpec> Started { get; } = [];

        public List<string> Cancelled { get; } = [];

        public bool FailStart { get; set; }

        public override WorkerHandle StartWorker(WorkerSpec spec)
        {
            if (FailStart)
            {
                throw new InvalidOperationException("could not start");
            }

            Started.Add(spec);
            var h = new WorkerHandle("w" + Started.Count, new ProcKey(1000 + (uint)Started.Count, 1), spec.Kind);
            _running[h.Id] = new TaskCompletionSource<WorkerExit>(TaskCreationOptions.RunContinuationsAsynchronously);
            return h;
        }

        public override Task<WorkerExit> WaitWorker(WorkerHandle handle, CancellationToken cancel) => _running[handle.Id].Task;

        public override void CancelWorker(WorkerHandle handle)
        {
            Cancelled.Add(handle.Id);
            Exit(handle.Id, -1);
        }

        public void Exit(string id, int code)
        {
            if (_running.TryGetValue(id, out var tcs))
            {
                tcs.TrySetResult(new WorkerExit(new WorkerHandle(id, new ProcKey(0, 0), WorkerKind.Capture), code, string.Empty, string.Empty, false, false));
            }
        }
    }

    private CaptureSupervisor Supervisor() =>
        new(_context, CaptureSupervisor.DefaultSpec("/opt/tasktor", "/data"), () => _consent);

    private void Settle()
    {
        while (_offLoop.Count > 0)
        {
            var w = _offLoop[0];
            _offLoop.RemoveAt(0);
            w();
        }

        _rig.Pump();
    }

    private void Crash(int code = 1)
    {
        _workers.Exit("w" + _workers.Started.Count, code);
        Settle();
    }

    [Fact]
    public void Starts_a_capture_worker_in_the_install_dir_with_the_store_root()
    {
        using var sup = Supervisor();
        sup.Start();
        var spec = Assert.Single(_workers.Started);
        Assert.Equal(WorkerKind.Capture, spec.Kind);
        Assert.StartsWith("/opt/tasktor", spec.Exe, StringComparison.Ordinal);
        Assert.Contains("tasktor-capture", spec.Exe, StringComparison.Ordinal);
        Assert.Equal(["--data-dir", "/data"], spec.Args);
        Assert.True(sup.Running);
        Assert.Null(sup.Failure);
        Assert.Throws<ArgumentException>(() => new CaptureSupervisor(_context, spec with { Kind = WorkerKind.Plugin }, () => true));
    }

    [Fact]
    public void No_consent_means_no_process()
    {
        _consent = false;
        using var sup = Supervisor();
        sup.Start();
        Assert.Empty(_workers.Started);
        Assert.Equal("no_consent", sup.Failure);
    }

    [Fact]
    public void A_crash_restarts_with_backoff_three_times_an_hour_then_the_card_says_crashed()
    {
        using var sup = Supervisor();
        var changes = 0;
        sup.Changed += () => changes++;
        sup.Start();

        for (var i = 0; i < CaptureSupervisor.MaxRestartsPerHour; i++)
        {
            Crash();
            Assert.False(sup.Running);
            var delay = CaptureSupervisor.Backoff[i];
            _rig.Advance(delay - TimeSpan.FromSeconds(1));
            Assert.Equal(i + 1, _workers.Started.Count); // not before the backoff
            _rig.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(i + 2, _workers.Started.Count);
            Assert.True(sup.Running);
        }

        Crash();
        Assert.False(sup.Running);
        Assert.Equal("crashed", sup.Failure);
        _rig.Advance(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(30));
        Assert.Equal(4, _workers.Started.Count); // budget spent: no more starts
        Assert.True(changes >= 8);

        // Turning the Module off and on again is the user's reset.
        sup.Stop();
        sup.Start();
        Assert.Equal(5, _workers.Started.Count);
        Assert.Null(sup.Failure);
    }

    [Fact]
    public void Restarts_older_than_an_hour_no_longer_count()
    {
        using var sup = Supervisor();
        sup.Start();
        Crash();
        _rig.Advance(TimeSpan.FromSeconds(5));
        Crash();
        _rig.Advance(TimeSpan.FromSeconds(30));
        _rig.Advance(TimeSpan.FromMinutes(61), TimeSpan.FromMinutes(1)); // both restarts age out
        Crash();
        _rig.Advance(TimeSpan.FromSeconds(5)); // first backoff again
        Assert.Equal(4, _workers.Started.Count);
        Assert.Null(sup.Failure);
    }

    [Fact]
    public void Deliberate_exits_are_not_restarted()
    {
        using var sup = Supervisor();
        sup.Start();
        Crash(code: 0); // another instance, or a clean stop
        _rig.Advance(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(5));
        Assert.Single(_workers.Started);
        Assert.Null(sup.Failure);

        sup.Start();
        Crash(code: CaptureSupervisor.ExitNoConsent);
        _rig.Advance(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(5));
        Assert.Equal(2, _workers.Started.Count);
        Assert.Equal("refused", sup.Failure);
    }

    [Fact]
    public void Stop_cancels_the_worker_and_a_pending_restart()
    {
        using var sup = Supervisor();
        sup.Start();
        sup.Stop();
        Settle();
        Assert.Equal(["w1"], _workers.Cancelled);
        Assert.False(sup.Running);
        Assert.Null(sup.Failure); // the cancel's exit is not a crash

        sup.Start();
        Crash();
        sup.Stop(); // inside the backoff
        _rig.Advance(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(5));
        Assert.Equal(2, _workers.Started.Count);
    }

    [Fact]
    public void A_start_failure_is_reported_not_retried()
    {
        _workers.FailStart = true;
        using var sup = Supervisor();
        sup.Start();
        Assert.Equal("start_failed", sup.Failure);
        Assert.False(sup.Running);
    }
}
