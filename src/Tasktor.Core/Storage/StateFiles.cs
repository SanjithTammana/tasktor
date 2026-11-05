using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tasktor.Core.Storage;

/// <summary><c>state\consents.json</c> (internal): module, T1, plugin (with pinned hashes) and per-rule arrange consents.</summary>
public sealed record ConsentsFile
{
    public int SchemaVersion { get; init; } = 1;

    public Dictionary<string, DateTimeOffset> Modules { get; init; } = new(StringComparer.Ordinal);

    public Dictionary<string, PluginConsent> Plugins { get; init; } = new(StringComparer.Ordinal);

    public Dictionary<string, DateTimeOffset> ArrangeRules { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>A plugin consent pinned to the SHA-256 of <c>plugin.json</c> and of the command's target file (U1 D-H7).</summary>
public sealed record PluginConsent
{
    public required string ManifestSha256 { get; init; }

    public required string CommandSha256 { get; init; }

    public required DateTimeOffset GivenAt { get; init; }

    public string? Pack { get; init; }
}

/// <summary><c>state\selfuse.json</c> (H14): counts per feature, numbers only.</summary>
public sealed record SelfUseFile
{
    public int SchemaVersion { get; init; } = 1;

    public DateTimeOffset? Since { get; init; }

    public Dictionary<string, long> Counts { get; init; } = new(StringComparer.Ordinal);
}

/// <summary><c>state\pause.json</c>: pause state persists across restarts (privacy rule 3).</summary>
public sealed record PauseFile
{
    public bool Tracking { get; init; }

    public bool Capture { get; init; }

    public bool Refits { get; init; }

    public DateTimeOffset? TrackingUntil { get; init; }

    public DateTimeOffset? CaptureUntil { get; init; }
}

/// <summary>Source-generated JSON for Tasktor's own state files (internal, no stability promise).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true,
    Converters = [typeof(Tasktor.Contracts.UtcTimestampConverter)])]
[JsonSerializable(typeof(ConsentsFile))]
[JsonSerializable(typeof(SelfUseFile))]
[JsonSerializable(typeof(PauseFile))]
public sealed partial class CoreJsonContext : JsonSerializerContext;
