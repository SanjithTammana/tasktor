using System.Globalization;
using System.Text.Json.Serialization;

namespace Tasktor.Core.Features.Focus;

/// <summary>How a focus block ended (E11.2).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BlockEnd>))]
public enum BlockEnd
{
    [JsonStringEnumMemberName("completed")] Completed,
    [JsonStringEnumMemberName("skipped")] Skipped,
    [JsonStringEnumMemberName("stopped")] Stopped,
}

/// <summary>
/// What C measured for one block (numbers only, rule 5b): share of the block in the intended workflow, workflow switches,
/// idle share, agent-driven share (T1 only) and C's confidence for the block.
/// </summary>
public sealed record BlockMeasures(double OnIntentShare, int Switches, double IdleShare, double? AgentShare, double Confidence);

/// <summary>One stored focus block: numbers and workflow ids only, never titles.</summary>
public sealed record BlockResult
{
    public required DateTimeOffset At { get; init; }

    public required string Preset { get; init; }

    public required int Minutes { get; init; }

    public required BlockEnd End { get; init; }

    public string? IntendedWorkflow { get; init; }

    public double? OnIntentShare { get; init; }

    public int? Switches { get; init; }

    public double? IdleShare { get; init; }

    public double? AgentShare { get; init; }

    /// <summary>C's confidence was under its floor: kept for the count, left out of the comparison.</summary>
    public bool Excluded { get; init; }
}

/// <summary>Per preset over its last 20 blocks with measures.</summary>
public sealed record PresetStats(string Preset, int Blocks, double MeanOnIntent, double SwitchesPerBlock, double CompletionRate, int Excluded);

/// <summary>
/// E11.2 cross-validation: after each focus block, C's activity data for that block becomes numbers (on-intent share,
/// switches, idle share, agent share); per preset, rolling statistics over the last 20 blocks. Presets are compared only
/// after ≥ 5 blocks of each (C's trust ramp); a block whose confidence is below C's floor is excluded and counted. Report
/// only: it never switches the user's preset.
/// </summary>
public static class FocusReview
{
    public const int Window = 20;
    public const int MinBlocks = 5;
    public const double ConfidenceFloor = 0.5;
    public const int Keep = 200;

    public static BlockResult Result(DateTimeOffset at, string preset, int minutes, BlockEnd end, string? intended, BlockMeasures? m) => new()
    {
        At = at,
        Preset = preset,
        Minutes = minutes,
        End = end,
        IntendedWorkflow = intended,
        OnIntentShare = m?.OnIntentShare,
        Switches = m?.Switches,
        IdleShare = m?.IdleShare,
        AgentShare = m?.AgentShare,
        Excluded = m is not null && m.Confidence < ConfidenceFloor,
    };

    public static List<PresetStats> Stats(IEnumerable<BlockResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        return [.. results.GroupBy(r => r.Preset, StringComparer.Ordinal).Select(g =>
        {
            var all = g.OrderByDescending(r => r.At).ToList();
            var measured = all.Where(r => r.OnIntentShare is not null && !r.Excluded).Take(Window).ToList();
            var recent = all.Take(Window).ToList();
            return new PresetStats(
                g.Key,
                measured.Count,
                measured.Count == 0 ? 0 : measured.Average(r => r.OnIntentShare!.Value),
                measured.Count == 0 ? 0 : measured.Average(r => r.Switches ?? 0),
                recent.Count == 0 ? 0 : recent.Count(r => r.End == BlockEnd.Completed) / (double)recent.Count,
                all.Take(Window).Count(r => r.Excluded));
        })];
    }

    /// <summary>
    /// The comparison line, or why there is none: "50/10 kept you on course longer than 25/5 (82% vs 71%, 12 blocks each)".
    /// </summary>
    public static string Compare(IReadOnlyList<PresetStats> stats, bool trackingOn)
    {
        ArgumentNullException.ThrowIfNull(stats);
        if (!trackingOn)
        {
            return "Turn on tracking to see how focused blocks went";
        }

        var ready = stats.Where(s => s.Blocks >= MinBlocks).OrderByDescending(s => s.MeanOnIntent).ToList();
        if (ready.Count < 2)
        {
            var most = stats.MaxBy(s => s.Blocks);
            return most is null
                ? "Finish a few focus blocks to see how they went"
                : "Comparing presets after " + MinBlocks + " blocks of each (" + string.Join(", ", stats.Select(s => s.Preset + ": " + s.Blocks)) + ")";
        }

        var (a, b) = (ready[0], ready[^1]);
        var counts = a.Blocks == b.Blocks ? a.Blocks + " blocks each" : a.Blocks + " and " + b.Blocks + " blocks";
        return a.Preset + " kept you on course longer than " + b.Preset + " (" + Pct(a.MeanOnIntent) + " vs " + Pct(b.MeanOnIntent) + ", " + counts + ")";
    }

    private static string Pct(double share) => Math.Round(share * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
}
