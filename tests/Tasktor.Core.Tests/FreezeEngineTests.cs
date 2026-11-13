using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Platform;
using Tasktor.Core.Features.Jobs;
using Tasktor.Tests.Shared;

namespace Tasktor.Core.Tests;

/// <summary>
/// B3.7 freeze and thaw (proofs/process-control.md P1–P3): every thread Tasktor suspended is recorded once and resumed
/// once, and only while it is still that thread of that process.
/// </summary>
public sealed class FreezeEngineTests : IDisposable
{
    private readonly TempStore _store = new();
    private readonly FakeThreads _os = new();
    private readonly FrozenRegistry _registry;
    private readonly FreezeEngine _engine;

    public FreezeEngineTests()
    {
        _registry = new FrozenRegistry(_store.Layout.Frozen);
        _engine = new FreezeEngine(_registry, _os, () => DateTimeOffset.UnixEpoch);
    }

    public void Dispose() => _store.Dispose();

    private static readonly ProcKey App = new(100, 1000);

    [Fact]
    public void Freeze_then_thaw_leaves_every_thread_running()
    {
        _os.Process(App, 1, 2, 3);
        _engine.Freeze("app:x", [App]);
        Assert.All(_os.Counts.Values, c => Assert.Equal(1, c));
        _engine.Thaw("app:x", [App]);
        Assert.All(_os.Counts.Values, c => Assert.Equal(0, c));
        Assert.Empty(_registry.Jobs);
    }

    [Fact]
    public void Freezing_twice_then_thawing_once_leaves_every_thread_running()
    {
        _os.Process(App, 1, 2, 3);
        _engine.Freeze("app:x", [App]);
        _engine.Freeze("app:x", [App]);
        Assert.All(_os.Counts.Values, c => Assert.Equal(1, c));
        _engine.Thaw("app:x", [App]);
        Assert.All(_os.Counts.Values, c => Assert.Equal(0, c));
    }

    [Fact]
    public void A_second_freeze_adds_threads_started_since_the_first()
    {
        _os.Process(App, 1, 2);
        _engine.Freeze("app:x", [App]);
        _os.Process(App, 3);
        _engine.Freeze("app:x", [App]);
        Assert.Equal([1u, 2u, 3u], _registry.Find("app:x")!.Threads.Select(t => t.Tid).Order());
        _engine.Thaw("app:x", [App]);
        Assert.All(_os.Counts.Values, c => Assert.Equal(0, c));
    }

    [Fact]
    public void A_thaw_during_a_freeze_waits_for_it()
    {
        _os.Process(App, Enumerable.Range(1, 10).Select(i => (uint)i).ToArray());
        Task? thaw = null;
        _os.OnSuspend = _ =>
        {
            thaw ??= Task.Run(() => _engine.Thaw("app:x", [App]));
            Thread.Sleep(10); // a slow freeze: without the job lock the thaw would run in between
        };
        _engine.Freeze("app:x", [App]);
        Assert.True(thaw!.Wait(TimeSpan.FromSeconds(5)));
        Assert.All(_os.Counts.Values, c => Assert.Equal(0, c));
        Assert.Empty(_registry.Jobs);
    }

    [Fact]
    public void A_thread_suspended_after_its_record_is_gone_is_resumed_at_once()
    {
        _os.Process(App, 1, 2, 3);
        _os.OnSuspend = tid =>
        {
            if (tid == 1)
            {
                _registry.Remove("app:x"); // the entry vanishes mid-freeze
            }
        };
        _engine.Freeze("app:x", [App]);
        Assert.Equal(0, _os.Counts[2]);
        Assert.Equal(0, _os.Counts[3]);
    }

    [Fact]
    public void Freeze_does_not_suspend_a_thread_id_reused_by_another_process_since_the_listing()
    {
        _os.Process(App, 1, 2);
        var other = new ProcKey(200, 2000);
        _os.Reassign(2, other, suspended: 0, listedAs: App); // the listing still says tid 2 is App's
        _engine.Freeze("app:x", [App]);
        Assert.Equal(1, _os.Counts[1]);
        Assert.Equal(0, _os.Counts[2]);
        Assert.DoesNotContain(_registry.Find("app:x")!.Threads, t => t.Tid == 2);
    }

    [Fact]
    public void Thaw_skips_a_thread_id_now_owned_by_another_process()
    {
        _os.Process(App, 1, 2);
        _engine.Freeze("app:x", [App]);
        _os.Reassign(2, new ProcKey(200, 2000), suspended: 1); // App's thread exited; tid 2 is another program's, suspended on purpose
        _engine.Thaw("app:x", [App]);
        Assert.Equal(0, _os.Counts[1]);
        Assert.Equal(1, _os.Counts[2]);
        Assert.Empty(_registry.Jobs);
    }

    [Fact]
    public void Thaw_skips_a_thread_whose_pid_was_reused_by_a_new_process()
    {
        _os.Process(App, 1);
        _engine.Freeze("app:x", [App]);
        _os.Reassign(1, new ProcKey(App.Pid, 9999), suspended: 1);
        _engine.Thaw("app:x", [App]);
        Assert.Equal(1, _os.Counts[1]);
    }

    [Fact]
    public void Thaw_skips_a_thread_id_reused_inside_the_same_process()
    {
        _os.Process(App, 1);
        _engine.Freeze("app:x", [App]);
        _os.Reassign(1, App, suspended: 1); // the frozen thread was ended; a new thread of App got its id and is suspended
        _engine.Thaw("app:x", [App]);
        Assert.Equal(1, _os.Counts[1]);
    }

    [Fact]
    public void A_thread_that_could_not_be_resumed_keeps_its_record()
    {
        _os.Process(App, 1, 2);
        _engine.Freeze("app:x", [App]);
        _os.FailResume.Add(2);
        var outcome = Assert.Single(_engine.Thaw("app:x", [App]));
        Assert.Equal(ActionStatus.Failed, outcome.Status);
        Assert.Equal([2u], _registry.Find("app:x")!.Threads.Select(t => t.Tid));
        _os.FailResume.Clear();
        _engine.Thaw("app:x", [App]);
        Assert.All(_os.Counts.Values, c => Assert.Equal(0, c));
        Assert.Empty(_registry.Jobs);
    }

    [Fact]
    public void Recovery_after_a_restart_resumes_only_threads_that_are_still_theirs()
    {
        _os.Process(App, 1, 2);
        _engine.Freeze("app:x", [App]);
        _os.Reassign(2, new ProcKey(300, 3000), suspended: 1);
        var restarted = new FreezeEngine(new FrozenRegistry(_store.Layout.Frozen), _os, () => DateTimeOffset.UnixEpoch);
        Assert.Equal(["app:x"], restarted.Recover());
        Assert.Equal(0, _os.Counts[1]);
        Assert.Equal(1, _os.Counts[2]);
    }

    [Fact]
    public void Recovery_clears_every_job_so_a_second_start_finds_nothing()
    {
        _os.Process(App, 1);
        _os.Process(new ProcKey(200, 2000), 2);
        _engine.Freeze("a", [App]);
        _engine.Freeze("b", [new ProcKey(200, 2000)]);
        var restarted = new FreezeEngine(new FrozenRegistry(_store.Layout.Frozen), _os, () => DateTimeOffset.UnixEpoch);
        Assert.Equal(["a", "b"], restarted.Recover());
        Assert.All(_os.Counts.Values, c => Assert.Equal(0, c));
        Assert.Empty(new FrozenRegistry(_store.Layout.Frozen).Jobs);
    }

    [Fact]
    public void A_freeze_that_died_before_any_suspend_is_recovered_and_cleared()
    {
        _registry.Begin("half", [App], DateTimeOffset.UnixEpoch);
        var restarted = new FreezeEngine(new FrozenRegistry(_store.Layout.Frozen), _os, () => DateTimeOffset.UnixEpoch);
        Assert.Equal(["half"], restarted.Recover());
        Assert.Empty(new FrozenRegistry(_store.Layout.Frozen).Jobs);
    }

    [Fact]
    public void A_freeze_that_arrives_during_recovery_keeps_its_record()
    {
        _os.Process(App, 1);
        _engine.Freeze("app:x", [App]);
        Task? freeze = null;
        _os.OnResume = _ =>
        {
            freeze ??= Task.Run(() => _engine.Freeze("app:x", [App]));
            Thread.Sleep(20); // without the lock the new freeze would record before recovery's removal
        };
        _engine.Recover();
        Assert.True(freeze!.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, _os.Counts[1]);
        Assert.Equal([1u], _registry.Find("app:x")!.Threads.Select(t => t.Tid));
    }

    [Fact]
    public void A_record_written_before_creation_times_were_kept_uses_the_process_list()
    {
        _os.Process(App, 1);
        _registry.Begin("old", [App], DateTimeOffset.UnixEpoch);
        _os.Counts[1] = 1;
        Assert.True(_registry.AddThread("old", App.Pid, 1)); // no creation times, as old files have
        _engine.Thaw("old", [App]);
        Assert.Equal(0, _os.Counts[1]);
    }

    [Fact]
    public void An_old_record_whose_owner_is_ambiguous_is_not_resumed()
    {
        _os.Process(App, 1);
        _registry.Begin("old", [App, new ProcKey(App.Pid, 5555)], DateTimeOffset.UnixEpoch); // the pid twice: which one?
        _os.Counts[1] = 1;
        _registry.AddThread("old", App.Pid, 1);
        _engine.Thaw("old", [App]);
        Assert.Equal(1, _os.Counts[1]);
        Assert.Empty(_registry.Jobs);
    }

    /// <summary>Threads with an owner process, their own creation time and a suspend count.</summary>
    private sealed class FakeThreads : IThreadApi
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<uint, (ProcKey Owner, long Created)> _threads = [];
        private readonly Dictionary<uint, ProcKey> _listedAs = [];
        private long _clock = 1;

        public Dictionary<uint, int> Counts { get; } = [];

        public HashSet<uint> FailResume { get; } = [];

        public Action<uint>? OnSuspend { get; set; }

        public Action<uint>? OnResume { get; set; }

        public void Process(ProcKey key, params uint[] tids)
        {
            lock (_gate)
            {
                foreach (var tid in tids)
                {
                    _threads[tid] = (key, _clock++);
                    Counts.TryAdd(tid, 0);
                }
            }
        }

        /// <summary>The thread with this id is now a new thread of <paramref name="owner"/>; <paramref name="listedAs"/> keeps a
        /// stale listing that still shows the old owner.</summary>
        public void Reassign(uint tid, ProcKey owner, int suspended, ProcKey? listedAs = null)
        {
            lock (_gate)
            {
                _threads[tid] = (owner, _clock++);
                Counts[tid] = suspended;
                if (listedAs is { } l)
                {
                    _listedAs[tid] = l;
                }
            }
        }

        public bool IsAlive(ProcKey key)
        {
            lock (_gate)
            {
                return _threads.Values.Any(t => t.Owner == key);
            }
        }

        public IEnumerable<(uint Pid, uint Tid)> Threads(IReadOnlySet<uint> pids)
        {
            lock (_gate)
            {
                return _threads.Select(kv => (Pid: _listedAs.TryGetValue(kv.Key, out var l) ? l.Pid : kv.Value.Owner.Pid, Tid: kv.Key))
                    .Where(t => pids.Contains(t.Pid)).ToList();
            }
        }

        public SuspendResult Suspend(uint tid, ProcKey owner, out long threadCreated)
        {
            lock (_gate)
            {
                threadCreated = 0;
                if (!_threads.TryGetValue(tid, out var t) || !Matches(t.Owner, owner))
                {
                    return SuspendResult.Skipped;
                }

                threadCreated = t.Created;
                Counts[tid]++;
            }

            OnSuspend?.Invoke(tid);
            return SuspendResult.Suspended;
        }

        public ResumeResult Resume(uint tid, ProcKey owner, long threadCreated)
        {
            OnResume?.Invoke(tid);
            lock (_gate)
            {
                if (!_threads.TryGetValue(tid, out var t) || !Matches(t.Owner, owner) || (threadCreated != 0 && t.Created != threadCreated))
                {
                    return ResumeResult.Gone;
                }

                if (FailResume.Contains(tid))
                {
                    return ResumeResult.Failed;
                }

                Counts[tid] = Math.Max(0, Counts[tid] - 1);
                return ResumeResult.Resumed;
            }
        }

        private static bool Matches(ProcKey actual, ProcKey owner) =>
            actual.Pid == owner.Pid && (owner.CreatedTicks == 0 || actual.CreatedTicks == owner.CreatedTicks);
    }
}
