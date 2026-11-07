using System.Text.Json.Serialization;

namespace Tasktor.Contracts.Presets;

/// <summary><c>tasktor.presets/1</c>: <c>presets.json</c>, user content (U3 D-W5).</summary>
public sealed record PresetsFile
{
    [JsonPropertyName("$schema")]
    public string? SchemaRef { get; init; }

    public string Schema { get; init; } = ContractIds.Presets;

    public required IReadOnlyList<Preset> Presets { get; init; }
}

public sealed record Preset
{
    public required string Name { get; init; }

    public string? Hotkey { get; init; }

    public required IReadOnlyList<PresetSlot> Slots { get; init; }

    /// <summary>Optional FancyZones layouts per monitor, applied through the FancyZones CLI.</summary>
    public IReadOnlyList<FancyZonesLayout>? Fancyzones { get; init; }

    /// <summary>The workflow the preset was built from, if any (ids only).</summary>
    public string? Workflow { get; init; }
}

public sealed record PresetSlot
{
    public required PresetApp App { get; init; }

    public PresetMatch? Match { get; init; }

    public required PresetPlace Place { get; init; }

    /// <summary>Off unless the user ticked it and confirmed the arguments on the review screen.</summary>
    public PresetLaunch? Launch { get; init; }

    /// <summary>Always <c>current</c> (D-W7).</summary>
    public string Desktop { get; init; } = "current";

    /// <summary>The one slot that may take focus after a restore.</summary>
    public bool? Focus { get; init; }
}

public sealed record PresetApp
{
    public required string AppId { get; init; }

    public string? Exe { get; init; }
}

public sealed record PresetMatch
{
    public string? Class { get; init; }

    public int Nth { get; init; }

    /// <summary>A title pattern the user typed; titles are never captured automatically.</summary>
    public string? TitlePattern { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<ShowState>))]
public enum ShowState
{
    [JsonStringEnumMemberName("normal")] Normal,
    [JsonStringEnumMemberName("maximized")] Maximized,
    [JsonStringEnumMemberName("minimized")] Minimized,
}

/// <summary>Placement: monitor device path + rect in DIPs relative to that monitor's work area (D-W8).</summary>
public sealed record PresetPlace
{
    public required string Monitor { get; init; }

    public required DipRect Rect { get; init; }

    public ShowState Show { get; init; }
}

public sealed record DipRect
{
    public double X { get; init; }

    public double Y { get; init; }

    public double W { get; init; }

    public double H { get; init; }
}

public sealed record PresetLaunch
{
    public required string Exe { get; init; }

    public IReadOnlyList<string>? Args { get; init; }
}

public sealed record FancyZonesLayout
{
    public required string Monitor { get; init; }

    public required string Layout { get; init; }
}

/// <summary>
/// U4 preset review (U3 I5): the windows open now, for "Save open windows as a preset". Personal data (titles, paths,
/// launch arguments) shown to the user in their own window over the current-user pipe (approved by the owner); never logged.
/// </summary>
public sealed record OpenWindowsReport
{
    public IReadOnlyList<OpenWindow> Windows { get; set; } = [];
}

public sealed record OpenWindow
{
    /// <summary>The window handle, as text: what <c>preset.save</c> ticks.</summary>
    public required string Id { get; init; }

    public required string App { get; init; }

    public string? Title { get; init; }

    /// <summary>1-based monitor number in the order Windows lists them, and the window's size in pixels.</summary>
    public int Monitor { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>What "open it if it is not running" would run: exactly this program and these arguments. Null if unknown.</summary>
    public PresetLaunch? Launch { get; init; }
}
