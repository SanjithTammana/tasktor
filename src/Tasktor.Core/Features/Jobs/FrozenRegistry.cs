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

public sealed record FrozenThread(uint Pid, uint Tid);

public sealed record FrozenFile
{
    public int SchemaVersion { get; init; } = 1;

    public List<FrozenJob> Jobs { get; init; } = [];
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

    public void Begin(string jobId, IReadOnlyList<ProcKey> keys, DateTimeOffset now)
    {
        lock (_gate)
        {
            _file.Jobs.RemoveAll(j => j.JobId == jobId);
            _file.Jobs.Add(new FrozenJob { JobId = jobId, Processes = keys.Select(k => new FrozenProcess(k.Pid, k.CreatedTicks)).ToList(), At = now });
            Save();
        }
    }

    public void AddThread(string jobId, uint pid, uint tid)
    {
        lock (_gate)
        {
            var job = _file.Jobs.FirstOrDefault(j => j.JobId == jobId);
            if (job is null)
            {
                return;
            }

            job.Threads.Add(new FrozenThread(pid, tid));
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

    /// <summary>
    /// Start-up recovery: thaw every job a previous run left frozen. <paramref name="thaw"/> resumes exactly the recorded
    /// threads and clears the entry last; a job whose entry is still present afterwards is dropped here so one bad record
    /// cannot block every later start.
    /// </summary>
    public IReadOnlyList<string> Recover(Action<string, IReadOnlyList<ProcKey>> thaw)
    {
        var ids = new List<string>();
        foreach (var job in Jobs)
        {
            thaw(job.JobId, job.Processes.Select(p => new ProcKey(p.Pid, p.Created)).ToList());
            Remove(job.JobId);
            ids.Add(job.JobId);
        }

        return ids;
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
