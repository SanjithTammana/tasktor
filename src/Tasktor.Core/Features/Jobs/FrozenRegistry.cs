using System.Text.Json;
using System.Text.Json.Serialization;
using Tasktor.Contracts.Platform;
using Tasktor.Core.Storage;

namespace Tasktor.Core.Features.Jobs;

/// <summary>One frozen job: its processes and exactly the threads Tasktor suspended.</summary>
public sealed record FrozenJob
{
    public required string JobId { get; init; }

    public List<FrozenProcess> Processes { get; init; } = [];

    public List<FrozenThread> Threads { get; init; } = [];

    public DateTimeOffset At { get; init; }
}

public sealed record FrozenProcess(uint Pid, long Created);

/// <summary>A suspended thread, its process's creation time and its own (both 0 in files written before they were recorded:
/// the process list's entry for that pid is used for the first, and the thread check is skipped).</summary>
public sealed record FrozenThread(uint Pid, uint Tid, long Created = 0, long ThreadCreated = 0);

public sealed record FrozenFile
{
    public int SchemaVersion { get; init; } = 1;

    public List<FrozenJob> Jobs { get; set; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(FrozenFile))]
internal sealed partial class FrozenJsonContext : JsonSerializerContext;

/// <summary>
/// B3.7 crash-safe freeze: the intent is written to <c>state\frozen.json</c> <b>before</b> the first suspend and every
/// suspended thread is added as it happens; Thaw clears the entry <b>last</b>. If the agent dies while a job is frozen, the
/// next start thaws it (<c>RecoverFrozen</c>). Thread-safe: freeze and thaw run off the loop.
/// </summary>
public sealed class FrozenRegistry
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private FrozenFile _file = new();

    public FrozenRegistry(string path)
    {
        _path = path;
        Load();
    }

    public IReadOnlyList<FrozenJob> Jobs
    {
        get
        {
            lock (_gate)
            {
                return [.. _file.Jobs];
            }
        }
    }

    /// <summary>Record the intent to freeze. A job already frozen keeps its entry and recorded threads (a second freeze adds
    /// to it, never forgets what the first one suspended); new processes are added.</summary>
    public void Begin(string jobId, IReadOnlyList<ProcKey> keys, DateTimeOffset now)
    {
        lock (_gate)
        {
            var job = _file.Jobs.FirstOrDefault(j => j.JobId == jobId);
            if (job is null)
            {
                job = new FrozenJob { JobId = jobId, At = now };
                _file.Jobs.Add(job);
            }

            foreach (var k in keys)
            {
                if (!job.Processes.Any(p => p.Pid == k.Pid && p.Created == k.CreatedTicks))
                {
                    job.Processes.Add(new FrozenProcess(k.Pid, k.CreatedTicks));
                }
            }

            Save();
        }
    }

    /// <summary>Record one suspended thread (see <see cref="AddThreads"/>).</summary>
    public bool AddThread(string jobId, uint pid, uint tid, long created = 0, long threadCreated = 0) =>
        AddThreads(jobId, [new FrozenThread(pid, tid, created, threadCreated)]);

    /// <summary>Record threads about to be suspended, in one write. False when the job's entry is gone (thawed meanwhile):
    /// the caller must not suspend them, since nothing would resume them.</summary>
    public bool AddThreads(string jobId, IReadOnlyCollection<FrozenThread> threads)
    {
        ArgumentNullException.ThrowIfNull(threads);
        lock (_gate)
        {
            var job = _file.Jobs.FirstOrDefault(j => j.JobId == jobId);
            if (job is null)
            {
                return false;
            }

            job.Threads.AddRange(threads);
            Save();
            return true;
        }
    }

    /// <summary>Forget threads that were recorded but could not be suspended. False when the job's entry is gone.</summary>
    public bool RemoveThreads(string jobId, IReadOnlyCollection<FrozenThread> threads)
    {
        ArgumentNullException.ThrowIfNull(threads);
        lock (_gate)
        {
            var job = _file.Jobs.FirstOrDefault(j => j.JobId == jobId);
            if (job is null)
            {
                return false;
            }

            if (threads.Count > 0)
            {
                job.Threads.RemoveAll(threads.Contains);
                Save();
            }

            return true;
        }
    }

    /// <summary>After a thaw: keep only the threads still suspended (it could not resume them); none left removes the job.</summary>
    public void Retain(string jobId, IReadOnlyList<FrozenThread> stillSuspended)
    {
        ArgumentNullException.ThrowIfNull(stillSuspended);
        lock (_gate)
        {
            var job = _file.Jobs.FirstOrDefault(j => j.JobId == jobId);
            if (job is null)
            {
                return;
            }

            if (stillSuspended.Count == 0)
            {
                _file.Jobs.Remove(job);
            }
            else
            {
                job.Threads.Clear();
                job.Threads.AddRange(stillSuspended);
            }

            Save();
        }
    }

    public FrozenJob? Find(string jobId)
    {
        lock (_gate)
        {
            return _file.Jobs.FirstOrDefault(j => j.JobId == jobId);
        }
    }

    public bool IsFrozen(uint pid)
    {
        lock (_gate)
        {
            return _file.Jobs.Any(j => j.Processes.Any(p => p.Pid == pid));
        }
    }

    public void Remove(string jobId)
    {
        lock (_gate)
        {
            if (_file.Jobs.RemoveAll(j => j.JobId == jobId) > 0)
            {
                Save();
            }
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                _file = JsonSerializer.Deserialize(File.ReadAllText(_path), FrozenJsonContext.Default.FrozenFile) ?? new();
            }
        }
        catch (JsonException)
        {
            _file = new();
        }
    }

    private void Save() => AtomicFile.Write(_path, JsonSerializer.SerializeToUtf8Bytes(_file, FrozenJsonContext.Default.FrozenFile));
}
