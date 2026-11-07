using System.Text.Json.Serialization;
namespace Tasktor.Contracts.Workflow;

/// <summary><c>prereg.json</c> (D-C6): every threshold of C, fixed before data; its hash is stamped into each model.</summary>
public sealed record PreregFile
{
    public string Schema { get; set; } = ContractIds.Prereg;

    public int SchemaVersion { get; init; } = 1;

    public EpisodeThresholds Episodes { get; set; } = new();

    public FeatureSpec Features { get; set; } = new();

    public ClusteringSpec Clustering { get; set; } = new();

    public GateThresholds Gates { get; set; } = new();

    public AssignmentSpec Assignment { get; set; } = new();
}

public sealed record EpisodeThresholds
{
    public long SegmentMinMs { get; init; } = 2_000;

    public long GlanceMaxMs { get; init; } = 30_000;

    public long IdleSplitMs { get; init; } = 300_000;

    public long ActivityCapMs { get; init; } = 3_600_000;

    public long WindowActiveMs { get; init; } = 900_000;

    public long WindowMinActiveMs { get; init; } = 180_000;

    /// <summary>Input idle starts after this long without input (C1.2).</summary>
    public long IdleAfterMs { get; init; } = 180_000;
}

public sealed record FeatureSpec
{
    public int Version { get; init; } = 1;

    public int Buckets { get; init; } = 262_144;

    public int MaxTokens { get; init; } = 256;

    public int MinDf { get; init; } = 3;

    public int LsaDims { get; init; } = 64;

    public int PcaDims { get; init; } = 16;

    public int MinAppActivities { get; init; } = 3;

    public int ContextApps { get; init; } = 20;

    public double TextWeight { get; init; } = 1.0;

    public double AppWeight { get; init; } = 1.0;

    public double TimeWeight { get; init; } = 0.3;

    public double ContextWeight { get; init; } = 0.3;

    public double ShapeWeight { get; init; } = 0.2;

    /// <summary>Terms readable after 30 days: ≥ this many activities …</summary>
    public int TermMinActivities { get; init; } = 5;

    /// <summary>… on ≥ this many days (D-C4).</summary>
    public int TermMinDays { get; init; } = 3;
}

public sealed record ClusteringSpec
{
    public int MinClusterSize { get; init; } = 8;

    public double MinClusterSizeFrac { get; init; } = 0.01;

    public int MinSamples { get; init; } = 5;

    public int KMin { get; init; } = 5;

    public int KMax { get; init; } = 40;

    public int Sample { get; init; } = 3_000;

    public int FitCap { get; init; } = 10_000;

    public int FitDays { get; init; } = 90;

    public int Seed { get; init; } = 7;
}

public sealed record GateThresholds
{
    public int G0Activities { get; init; } = 300;

    public int G0Days { get; init; } = 7;

    public double G0Hours { get; init; } = 20;

    public int G0Windows { get; init; } = 200;

    public int G0WindowDays { get; init; } = 10;

    public double G1Coverage { get; init; } = 0.60;

    public double G2Dbcv { get; init; } = 0.20;

    public double G3Show { get; init; } = 0.60;

    public double G3Stable { get; init; } = 0.75;

    public int G3Resamples { get; init; } = 20;

    public double G3Fraction { get; init; } = 0.8;

    public double G4Ari { get; init; } = 0.50;

    public double G5Persistence { get; init; } = 0.80;

    public int G6Clusters { get; init; } = 3;

    public double G6bGain { get; init; } = 0.05;

    public double MatchJaccard { get; init; } = 0.5;
}

public sealed record AssignmentSpec
{
    public double Floor { get; init; } = 0.5;

    public int Hysteresis { get; init; } = 2;

    public double RadiusPercentile { get; init; } = 95;

    public int ExemplarsPerCluster { get; init; } = 20;
}

/// <summary>The training extract's <c>meta.json</c> (numbers only plus the frequent-term dictionary; no titles).</summary>
public sealed record ExtractMeta
{
    public string Schema { get; init; } = ContractIds.Extract;

    public required string Version { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required int Activities { get; init; }

    /// <summary>App identity keys; <c>apps.i32</c> indexes into this (−1 = none).</summary>
    public required IReadOnlyList<string> Apps { get; init; }

    /// <summary>Frequent terms only (D-C4): bucket → term, for names. Hidden terms are left out.</summary>
    public IReadOnlyDictionary<string, string> Terms { get; init; } = new Dictionary<string, string>();

    /// <summary>Column names of <c>dense.f32</c>, in order.</summary>
    public required IReadOnlyList<string> Dense { get; init; }

    public required IReadOnlyList<ExtractWindow> Windows { get; init; }

    /// <summary>Activity ids (episodes.db) in extract order.</summary>
    public required IReadOnlyList<long> ActivityIds { get; init; }

    /// <summary>File name → SHA-256 (hex) of each array file.</summary>
    public required IReadOnlyDictionary<string, string> Files { get; init; }
}

/// <summary>A workflow window: its activities (extract indices) with active dwell (ms).</summary>
public sealed record ExtractWindow(long Id, int Day, [property: JsonRequired] IReadOnlyList<int> Activities, [property: JsonRequired] IReadOnlyList<long> DwellMs);

/// <summary>The worker's job on stdin (C-H: one JSON document).</summary>
public sealed record WorkerJob
{
    public string Schema { get; init; } = ContractIds.WorkerJob;

    public required string Extract { get; init; }

    public string? PreviousModel { get; init; }

    public string? Overrides { get; init; }

    public required string Prereg { get; init; }

    public required string Output { get; init; }

    public string? ResumeFrom { get; init; }
}

/// <summary><c>model.json</c>: what the agent validates before it swaps a model in (D-C10).</summary>
public sealed record ModelManifest
{
    public string Schema { get; init; } = ContractIds.Model;

    public required string Version { get; init; }

    public required string PreregHash { get; init; }

    public int FeatureSpec { get; init; } = 1;

    public required string ExtractHash { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required IReadOnlyList<string> Apps { get; init; }

    public required IReadOnlyList<string> ContextApps { get; init; }

    public required BlockWeights Weights { get; init; }

    public required int Dims { get; init; }

    public required int PcaDims { get; init; }

    public required IReadOnlyList<ModelCluster> Clusters { get; init; }

    public IReadOnlyList<ModelCluster> Workflows { get; init; } = [];

    /// <summary>The cluster ids that span the workflow window space, in order.</summary>
    public IReadOnlyList<string> WorkflowSpace { get; init; } = [];

    public required IReadOnlyList<SidecarFile> Files { get; init; }

    public required bool Activated { get; init; }

    public IReadOnlyList<string> FailedGates { get; init; } = [];
}

public sealed record BlockWeights(double Text, double App, double Time, double Context, double Shape);

public sealed record ModelCluster
{
    /// <summary>Stable id (D-C12), e.g. <c>a3</c> (activity) or <c>w1</c> (workflow).</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary><c>shown</c>, <c>unstable</c> (G3 below 0.60), <c>hidden</c>, <c>retired</c>.</summary>
    public required string Status { get; init; }

    public double Stability { get; init; }

    public required double Radius { get; init; }

    public required int ExemplarStart { get; init; }

    public required int ExemplarCount { get; init; }

    public int Size { get; init; }

    public string? SplitFrom { get; init; }

    public string? MergedInto { get; init; }
}

public sealed record SidecarFile([property: JsonRequired] string Name, [property: JsonRequired] string Dtype, [property: JsonRequired] IReadOnlyList<int> Shape, [property: JsonRequired] string Sha256);

/// <summary><c>eval.json</c>: every gate with its value and threshold, the null model's numbers beside the model's.</summary>
public sealed record EvalReport
{
    public string Schema { get; init; } = ContractIds.Eval;

    public required string Version { get; init; }

    public required bool Activated { get; init; }

    public required IReadOnlyList<GateResult> Gates { get; init; }
}

public sealed record GateResult
{
    public required string Id { get; init; }

    public double? Value { get; init; }

    public double? Threshold { get; init; }

    public required bool Pass { get; init; }

    /// <summary>The null model's value (group by app), when the gate applies to it.</summary>
    public double? NullValue { get; init; }

    /// <summary>Report-only gates (G7) never block activation.</summary>
    public bool ReportOnly { get; init; }
}

/// <summary><c>overrides.json</c> (C7), kept forever, keyed by stable ids; applied after every fit.</summary>
public sealed record OverridesFile
{
    public string Schema { get; set; } = ContractIds.Overrides;

    public IReadOnlyDictionary<string, string> Names { get; set; } = new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> Merge { get; set; } = new Dictionary<string, string>();

    public IReadOnlyList<string> Hide { get; set; } = [];

    public IReadOnlyList<string> SplitRequest { get; set; } = [];

    public IReadOnlyList<string> HideTerm { get; set; } = [];
}

/// <summary>
/// U4 D-G3 "What Tasktor learned", Workflows tab (C7, C9, C10): the current model's workflows with the user's names and
/// merges, minutes in the last 7 days, and today's switches. Names and numbers only (rule 5b): no titles.
/// </summary>
public sealed record WorkflowsReport
{
    /// <summary>The model version, or null while nothing is learned yet (activities are shown by app).</summary>
    public string? Model { get; init; }

    public long SwitchesToday { get; init; }

    public IReadOnlyList<WorkflowEntry> Workflows { get; set; } = [];
}

public sealed record WorkflowEntry
{
    public required string Id { get; init; }

    /// <summary>What Tasktor shows: the user's name if they renamed it, else the model's.</summary>
    public required string Name { get; init; }

    public required string AutoName { get; init; }

    public bool Renamed { get; init; }

    /// <summary><c>shown</c>, <c>unstable</c> or <c>hidden</c>.</summary>
    public required string Status { get; init; }

    public double Stability { get; init; }

    public double MinutesWeek { get; init; }

    /// <summary>The workflow this one was merged into (shown as that one until the next refit).</summary>
    public string? MergedInto { get; init; }
}

