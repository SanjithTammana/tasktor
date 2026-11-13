using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Tasktor.Contracts;
using Tasktor.Contracts.Plugins;

namespace Tasktor.Core.Plugins;

/// <summary>A read <c>plugin.json</c>: the contract-1 view every host path uses, plus the contract-2 record when it is one.</summary>
public sealed record ReadManifest(PluginManifest View, PluginManifest2? V2);

/// <summary>
/// Reads <c>plugin.json</c> by its <c>contract</c>: 1 with the contract-1 record (unknown fields ignored, unchanged); 2 with
/// <see cref="ContractsJsonContext.Strict"/> (unknown fields and capabilities refused at every depth, the refusal naming the
/// closest known name). A contract-2 manifest is mapped to the contract-1 view (interval defaults to <c>manual</c>), so the
/// command, settings, actions, consent and hashing stay one code path.
/// </summary>
public static class PluginManifestReader
{
    private static readonly JsonDocumentOptions DocOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>The manifest, or why it cannot be read (a plain sentence for the plugin's error line).</summary>
    public static (ReadManifest? Manifest, string? Problem) Read(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        int contract;
        try
        {
            using var doc = JsonDocument.Parse(bytes, DocOptions);
            contract = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("contract", out var c)
                ? c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n) ? n : -1
                : 1;
        }
        catch (JsonException ex)
        {
            return (null, "plugin.json: " + FirstLine(ex.Message));
        }

        if (contract == 2)
        {
            try
            {
                var v2 = JsonSerializer.Deserialize(bytes, ContractsJsonContext.Strict.PluginManifest2);
                return v2 is null ? (null, "empty plugin.json") : (new ReadManifest(View(v2), v2), null);
            }
            catch (JsonException ex)
            {
                return (null, "plugin.json: " + (Explain(bytes) ?? FirstLine(ex.Message)));
            }
        }

        if (contract < 1)
        {
            return (null, "plugin.json: contract must be a number");
        }

        if (contract != 1)
        {
            return (null, "This plugin needs a newer Tasktor (contract " + contract + ")");
        }

        try
        {
            var v1 = JsonSerializer.Deserialize(bytes, ContractsJsonContext.Default.PluginManifest);
            return v1 is null ? (null, "empty plugin.json") : (new ReadManifest(v1, null), null);
        }
        catch (JsonException ex)
        {
            return (null, "plugin.json: " + FirstLine(ex.Message));
        }
    }

    /// <summary>The contract-1 view of a contract-2 manifest.</summary>
    public static PluginManifest View(PluginManifest2 m)
    {
        ArgumentNullException.ThrowIfNull(m);
        return new PluginManifest
        {
            SchemaRef = m.SchemaRef,
            Contract = 2,
            Id = m.Id,
            Name = m.Name,
            Version = m.Version,
            Author = m.Author,
            Description = m.Description,
            Command = m.Command,
            Interval = m.Interval ?? "manual",
            Timeout = m.Timeout,
            Reads = m.Reads,
            Network = m.Network,
            Settings = m.Settings,
            Actions = m.Actions,
            MinTasktor = m.MinTasktor,
        };
    }

    /// <summary>The first unknown field or capability in a contract-2 manifest, worded with a "did you mean"; null if none.</summary>
    public static string? Explain(byte[] bytes)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(bytes, documentOptions: DocOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (root is not JsonObject obj)
        {
            return null;
        }

        var strict = ContractsJsonContext.Strict;
        if (Unknown(obj, Info(strict, typeof(PluginManifest2))) is { } top)
        {
            return top;
        }

        if (obj["capabilities"] is JsonArray caps)
        {
            foreach (var cap in caps)
            {
                if (cap is JsonValue v && v.TryGetValue<string>(out var name) && !PluginCapabilities.Names.Contains(name, StringComparer.Ordinal))
                {
                    return ContractSpelling.Refusal("capability", name, PluginCapabilities.Names);
                }
            }
        }

        foreach (var (key, type) in new[] { ("settings", typeof(PluginSetting)), ("actions", typeof(PluginActionDecl)) })
        {
            if (obj[key] is JsonArray items)
            {
                foreach (var item in items)
                {
                    if (item is JsonObject o && Unknown(o, Info(strict, type)) is { } nested)
                    {
                        return key + ": " + nested;
                    }
                }
            }
        }

        return null;
    }

    private static JsonTypeInfo Info(ContractsJsonContext context, Type type) =>
        context.GetTypeInfo(type) ?? throw new InvalidOperationException("no contract for " + type.Name);

    private static string? Unknown(JsonObject obj, JsonTypeInfo info)
    {
        var known = ContractSpelling.FieldNames(info);
        foreach (var (key, _) in obj)
        {
            if (!known.Contains(key, StringComparer.Ordinal))
            {
                return ContractSpelling.Refusal("field", key, known);
            }
        }

        return null;
    }

    private static string FirstLine(string s) => s.Split('\n')[0];
}
