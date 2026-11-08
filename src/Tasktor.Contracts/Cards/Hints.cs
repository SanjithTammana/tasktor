using System.Text.Json.Serialization;

namespace Tasktor.Contracts.Cards;

/// <summary>Semantic severity hint (A D-A6). Shown by icon + word, never colour alone (U4 D-G1).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Severity>))]
public enum Severity
{
    [JsonStringEnumMemberName("normal")] Normal,
    [JsonStringEnumMemberName("warn")] Warn,
    [JsonStringEnumMemberName("critical")] Critical,
}

[JsonConverter(typeof(JsonStringEnumConverter<Trend>))]
public enum Trend
{
    [JsonStringEnumMemberName("flat")] Flat,
    [JsonStringEnumMemberName("up")] Up,
    [JsonStringEnumMemberName("down")] Down,
}

/// <summary>An accent role, not a colour (A D-A6). Renderers map roles to theme tokens.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AccentRole>))]
public enum AccentRole
{
    [JsonStringEnumMemberName("neutral")] Neutral,
    [JsonStringEnumMemberName("primary")] Primary,
    [JsonStringEnumMemberName("positive")] Positive,
    [JsonStringEnumMemberName("caution")] Caution,
    [JsonStringEnumMemberName("negative")] Negative,
    [JsonStringEnumMemberName("info")] Info,
}

/// <summary>The fixed icon set (A D-A6). Plugins choose from the same set.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<IconKey>))]
public enum IconKey
{
    [JsonStringEnumMemberName("none")] None,
    [JsonStringEnumMemberName("cpu")] Cpu,
    [JsonStringEnumMemberName("ram")] Ram,
    [JsonStringEnumMemberName("gpu")] Gpu,
    [JsonStringEnumMemberName("disk")] Disk,
    [JsonStringEnumMemberName("network")] Network,
    [JsonStringEnumMemberName("battery")] Battery,
    [JsonStringEnumMemberName("apps")] Apps,
    [JsonStringEnumMemberName("jobs")] Jobs,
    [JsonStringEnumMemberName("workflow")] Workflow,
    [JsonStringEnumMemberName("list")] List,
    [JsonStringEnumMemberName("calendar")] Calendar,
    [JsonStringEnumMemberName("clock")] Clock,
    [JsonStringEnumMemberName("alert")] Alert,
    [JsonStringEnumMemberName("plugin")] Plugin,
    [JsonStringEnumMemberName("folder")] Folder,
    [JsonStringEnumMemberName("git")] Git,
    [JsonStringEnumMemberName("focus")] Focus,
    [JsonStringEnumMemberName("windows")] Windows,
    [JsonStringEnumMemberName("capture")] Capture,
    [JsonStringEnumMemberName("shield")] Shield,
    [JsonStringEnumMemberName("power")] Power,
    [JsonStringEnumMemberName("ports")] Ports,
    [JsonStringEnumMemberName("agent")] Agent,
    [JsonStringEnumMemberName("note")] Note,
    [JsonStringEnumMemberName("info")] Info,
}
