using System.Text.Json.Serialization;
using Tasktor.Contracts.Actions;
using Tasktor.Contracts.Cards;

namespace Tasktor.Contracts.Plugins;

/// <summary><c>tasktor.plugin/1</c>: <c>plugin.json</c> (U1 D-H7).</summary>
public sealed record PluginManifest
{
    [JsonPropertyName("$schema")]
    public string? SchemaRef { get; init; }

    public int Contract { get; init; } = 1;

    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Version { get; init; }

    public string? Author { get; init; }

    public string? Description { get; init; }

    /// <summary>The command line. <c>{python}</c> names the bundled Python.</summary>
    public required IReadOnlyList<string> Command { get; init; }

    /// <summary>≥ <c>30s</c>, or <c>manual</c>.</summary>
    public required string Interval { get; init; }

    /// <summary>≤ <c>60s</c>.</summary>
    public required string Timeout { get; init; }

    /// <summary>Plain-language list of what the plugin reads (shown in the consent dialog).</summary>
    public required IReadOnlyList<string> Reads { get; init; }

    /// <summary>Plain-language list of what it contacts over the network (a declaration Tasktor cannot enforce).</summary>
    public required IReadOnlyList<string> Network { get; init; }

    public IReadOnlyList<PluginSetting>? Settings { get; init; }

    public IReadOnlyList<PluginActionDecl>? Actions { get; init; }

    public string? MinTasktor { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<PluginSettingType>))]
public enum PluginSettingType
{
    [JsonStringEnumMemberName("string")] String,
    [JsonStringEnumMemberName("path")] Path,
    [JsonStringEnumMemberName("bool")] Bool,
    [JsonStringEnumMemberName("number")] Number,
    [JsonStringEnumMemberName("choice")] Choice,
    [JsonStringEnumMemberName("secret")] Secret,
}

public sealed record PluginSetting
{
    public required string Key { get; init; }

    public required string Label { get; init; }

    public required PluginSettingType Type { get; init; }

    public string? Default { get; init; }

    public IReadOnlyList<string>? Choices { get; init; }
}

public sealed record PluginActionDecl
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    /// <summary>Plugins never get destructive process actions; their own actions are read or reversible.</summary>
    [JsonPropertyName("class")]
    public required ActionClass Class { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<PluginCall>))]
public enum PluginCall
{
    [JsonStringEnumMemberName("render")] Render,
    [JsonStringEnumMemberName("action")] Action,
}

/// <summary>The one JSON object a plugin run gets on stdin.</summary>
public sealed record PluginInput
{
    public int Contract { get; init; } = 1;

    public required PluginCall Call { get; init; }

    public string? Action { get; init; }

    public IReadOnlyDictionary<string, string>? Args { get; init; }

    public required IReadOnlyDictionary<string, string> Settings { get; init; }

    public required string SnapshotPath { get; init; }

    /// <summary>The plugin's own data folder (<c>plugin-data\&lt;id&gt;\</c>).</summary>
    public string? DataDir { get; init; }
}

/// <summary>The one JSON document a plugin prints (≤ 64 KB): ≤ 3 cards of the block subset, literal values only.</summary>
public sealed record PluginOutput
{
    public int Contract { get; init; } = 1;

    public required IReadOnlyList<Card> Cards { get; init; }
}

/// <summary><c>pack.json</c>: a folder of plugins with one consent dialog; may ship rule and config packs (U5 amendment).</summary>
public sealed record PackManifest
{
    [JsonPropertyName("$schema")]
    public string? SchemaRef { get; init; }

    public int Contract { get; init; } = 1;

    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Version { get; init; }

    public string? Author { get; init; }

    public string? Description { get; init; }

    /// <summary>Plugin folder names inside the pack.</summary>
    public required IReadOnlyList<string> Plugins { get; init; }

    /// <summary>Optional <c>rules.pack.json</c> (every entry off until ticked).</summary>
    public string? Rules { get; init; }

    /// <summary>Optional <c>config.pack.json</c> (every entry off until ticked).</summary>
    public string? Config { get; init; }
}
