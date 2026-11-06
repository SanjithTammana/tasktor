using System.Text.Json;
using System.Text.Json.Serialization;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Agents;
using Tasktor.Contracts.Calendar;
using Tasktor.Contracts.Capture;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Export;
using Tasktor.Contracts.Jobs;
using Tasktor.Contracts.Pipe;
using Tasktor.Contracts.Plugins;
using Tasktor.Contracts.Presets;
using Tasktor.Contracts.Rules;
using Tasktor.Contracts.Workflow;
using SnapshotRecord = Tasktor.Contracts.Snapshot.Snapshot;

namespace Tasktor.Contracts;

/// <summary>
/// The one source-generated JSON context for every contract (U1 D-H1; Native AOT safe, no reflection).
/// snake_case names, nulls omitted, comments and trailing commas accepted (JSON with comments, D-H5).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    AllowOutOfOrderMetadataProperties = true,
    RespectNullableAnnotations = true,
    MaxDepth = 32,
    Converters = [typeof(UtcTimestampConverter)])]
[JsonSerializable(typeof(SnapshotRecord))]
[JsonSerializable(typeof(DataReport))]
[JsonSerializable(typeof(Tasktor.Contracts.Config.ModulesReport))]
[JsonSerializable(typeof(PreregFile))]
[JsonSerializable(typeof(ExtractMeta))]
[JsonSerializable(typeof(WorkerJob))]
[JsonSerializable(typeof(ModelManifest))]
[JsonSerializable(typeof(EvalReport))]
[JsonSerializable(typeof(OverridesFile))]
[JsonSerializable(typeof(CaptureCommand))]
[JsonSerializable(typeof(CaptureStatus))]
[JsonSerializable(typeof(CaptureEvent))]
[JsonSerializable(typeof(Card))]
[JsonSerializable(typeof(IReadOnlyList<Card>))]
[JsonSerializable(typeof(Tasktor.Contracts.Snapshot.SnapshotState))]
[JsonSerializable(typeof(Block))]
[JsonSerializable(typeof(ActionRequest))]
[JsonSerializable(typeof(ActionResult))]
[JsonSerializable(typeof(RulesFile))]
[JsonSerializable(typeof(PluginManifest))]
[JsonSerializable(typeof(PluginInput))]
[JsonSerializable(typeof(PluginOutput))]
[JsonSerializable(typeof(PackManifest))]
[JsonSerializable(typeof(ProgressFile))]
[JsonSerializable(typeof(JobControlFile))]
[JsonSerializable(typeof(AgentHookFile))]
[JsonSerializable(typeof(CalendarFile))]
[JsonSerializable(typeof(ExportManifest))]
[JsonSerializable(typeof(PresetsFile))]
[JsonSerializable(typeof(PipeMessage))]
[JsonSerializable(typeof(Tasktor.Contracts.Capture.BrowserMessage))]
[JsonSerializable(typeof(WorkerSpec))]
[JsonSerializable(typeof(Tasktor.Contracts.Config.ConfigFile))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public sealed partial class ContractsJsonContext : JsonSerializerContext
{
    private static ContractsJsonContext? s_indented;

    /// <summary>The same context, writing indented JSON (for files people read).</summary>
    public static ContractsJsonContext Indented =>
        s_indented ??= new ContractsJsonContext(new JsonSerializerOptions(Default.Options) { WriteIndented = true });
}
