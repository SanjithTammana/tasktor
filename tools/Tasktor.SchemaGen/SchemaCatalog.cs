using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using Tasktor.Contracts;
using Tasktor.Contracts.Cards;

namespace Tasktor.SchemaGen;

/// <summary>One public schema file: its name (<c>schemas/&lt;file&gt;</c>), contract id and root type.</summary>
public sealed record SchemaEntry(string File, string ContractId, string Title, JsonTypeInfo TypeInfo);

/// <summary>Generates the JSON Schemas for every public contract from the C# records (U1 D-H1).</summary>
public static class SchemaCatalog
{
    public static IReadOnlyList<SchemaEntry> Entries { get; } = Build();

    private static List<SchemaEntry> Build()
    {
        var c = ContractsJsonContext.Default;
        return
        [
            new("tasktor.snapshot-1.json", ContractIds.Snapshot, "Tasktor snapshot (state + cards)", c.Snapshot),
            new("tasktor.action-request-1.json", ContractIds.Action, "Tasktor action request", c.ActionRequest),
            new("tasktor.action-result-1.json", ContractIds.Action, "Tasktor action result", c.ActionResult),
            new("tasktor.rules-1.json", ContractIds.Rules, "Tasktor rules file (rules.json)", c.RulesFile),
            new("tasktor.plugin-1.json", ContractIds.Plugin, "Tasktor plugin manifest (plugin.json)", c.PluginManifest),
            new("tasktor.plugin-input-1.json", ContractIds.Plugin, "Tasktor plugin input (stdin)", c.PluginInput),
            new("tasktor.plugin-output-1.json", ContractIds.Plugin, "Tasktor plugin output (stdout)", c.PluginOutput),
            new("tasktor.pack-1.json", ContractIds.Plugin, "Tasktor plugin pack (pack.json)", c.PackManifest),
            new("tasktor.job-1.json", ContractIds.Job, "Tasktor job progress file (jobs/<id>.json)", c.ProgressFile),
            new("tasktor.agent-1.json", ContractIds.Agent, "Tasktor agent hook file (agents/<session>.json)", c.AgentHookFile),
            new("tasktor.calendar-1.json", ContractIds.Calendar, "Tasktor calendar events file", c.CalendarFile),
            new("tasktor.export-1.json", ContractIds.Export, "Tasktor data export manifest", c.ExportManifest),
            new("tasktor.presets-1.json", ContractIds.Presets, "Tasktor window presets (presets.json)", c.PresetsFile),
            new("tasktor.config-1.json", ContractIds.Config, "Tasktor settings (config.json)", c.ConfigFile),
            new("tasktor.prereg-1.json", ContractIds.Prereg, "Tasktor workflow learning thresholds (prereg.json)", c.PreregFile),
            new("tasktor.extract-1.json", ContractIds.Extract, "Tasktor training extract metadata (meta.json)", c.ExtractMeta),
            new("tasktor.worker-job-1.json", ContractIds.WorkerJob, "Tasktor learning worker job (stdin)", c.WorkerJob),
            new("tasktor.model-1.json", ContractIds.Model, "Tasktor workflow model manifest (model.json)", c.ModelManifest),
            new("tasktor.eval-1.json", ContractIds.Eval, "Tasktor workflow model evaluation (eval.json)", c.EvalReport),
            new("tasktor.overrides-1.json", ContractIds.Overrides, "Tasktor workflow overrides (overrides.json)", c.OverridesFile),
        ];
    }

    /// <summary>The schema for one entry, as indented JSON text with a trailing newline.</summary>
    public static string Generate(SchemaEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var exporter = new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = Transform,
        };
        var node = entry.TypeInfo.Options.GetJsonSchemaAsNode(entry.TypeInfo.Type, exporter);
        var root = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$id"] = ContractIds.SchemaIdBase + entry.File,
            ["title"] = entry.Title,
            ["description"] = "Contract " + entry.ContractId + ". Generated from the C# records in Tasktor.Contracts; do not edit by hand.",
        };
        if (node is JsonObject obj)
        {
            foreach (var kv in obj.ToList())
            {
                obj.Remove(kv.Key);
                root[kv.Key] = kv.Value;
            }
        }

        var text = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return text.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    private static JsonNode Transform(JsonSchemaExporterContext context, JsonNode schema)
    {
        if (context.TypeInfo.Type == typeof(Value))
        {
            // A literal (scalar or array) or a binding {"bind": "<JSON Pointer>"} (U1 D-H2).
            return new JsonObject
            {
                ["anyOf"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["bind"] = new JsonObject { ["type"] = "string", ["pattern"] = "^(/.*)?$" },
                        },
                        ["required"] = new JsonArray("bind"),
                        ["additionalProperties"] = false,
                    },
                    new JsonObject { ["type"] = new JsonArray("number", "string", "boolean", "array", "null") },
                },
            };
        }

        if (context.TypeInfo.Type == typeof(DateTimeOffset))
        {
            return new JsonObject { ["type"] = "string", ["format"] = "date-time" };
        }

        if (context.TypeInfo.Type == typeof(DateTimeOffset?))
        {
            return new JsonObject { ["type"] = new JsonArray("string", "null"), ["format"] = "date-time" };
        }

        return schema;
    }
}
