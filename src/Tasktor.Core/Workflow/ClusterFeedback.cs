using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tasktor.Core.Workflow;

/// <summary>
/// C7 "That's wrong" feedback (D-C5 guardrail): per cluster, the last 10 labels as a bitmask (1 = wrong). A cluster shown
/// at least 5 times whose recent labels are more than 30% wrong is hidden until the next fit (CurrentWorkflow falls back
/// a level for it). Loop-only.
/// </summary>
public sealed class ClusterFeedback
{
    public const int Window = 10;
    public const int MinShown = 5;
    public const double MaxWrong = 0.30;

    private readonly Dictionary<string, (ushort Bits, int Count)> _labels = new(StringComparer.Ordinal);

    /// <summary>The cluster was shown for a closed activity: one "right" label unless corrected.</summary>
    public void Shown(string cluster)
    {
        var (bits, count) = _labels.GetValueOrDefault(cluster);
        _labels[cluster] = ((ushort)((bits << 1) & ((1 << Window) - 1)), Math.Min(count + 1, Window));
    }

    /// <summary>The user said the shown cluster was wrong: the latest label flips (or one is added). True when the guardrail fires.</summary>
    public bool Wrong(string cluster)
    {
        var (bits, count) = _labels.GetValueOrDefault(cluster);
        if (count == 0)
        {
            count = 1;
        }

        bits |= 1;
        _labels[cluster] = (bits, count);
        return ShouldHide(cluster);
    }

    public double WrongShare(string cluster)
    {
        var (bits, count) = _labels.GetValueOrDefault(cluster);
        return count == 0 ? 0 : BitOperations.PopCount((uint)bits) / (double)count;
    }

    public bool ShouldHide(string cluster) => _labels.TryGetValue(cluster, out var l) && l.Count >= MinShown && WrongShare(cluster) > MaxWrong;

    /// <summary>A new model starts with clean counters (ids may be reused, but the evidence was about the old fit).</summary>
    public void Reset() => _labels.Clear();
}

/// <summary><c>models/planner.json</c>: the refit cadence's learned state.</summary>
public sealed record RefitPlannerFile
{
    public int SchemaVersion { get; init; } = 1;

    public DateTimeOffset? LastFit { get; init; }

    public double IntervalDays { get; init; } = 7;

    public int FailuresInARow { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(RefitPlannerFile))]
internal sealed partial class WorkflowStateJsonContext : JsonSerializerContext;

public static class RefitPlannerStore
{
    public static RefitPlannerFile Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllBytes(path), WorkflowStateJsonContext.Default.RefitPlannerFile) ?? new() : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public static byte[] Bytes(RefitPlannerFile file) => JsonSerializer.SerializeToUtf8Bytes(file, WorkflowStateJsonContext.Default.RefitPlannerFile);
}
