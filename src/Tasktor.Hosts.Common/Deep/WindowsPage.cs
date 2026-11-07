using System.Globalization;
using System.Text.Json.Nodes;
using Tasktor.Contracts.Cards;
using Tasktor.Hosts.Common.Render;

namespace Tasktor.Hosts.Common.Deep;

/// <summary>
/// U4 D-G3 Windows page (U3 D-W5): the agent's <c>presets</c> pull as one section per preset with Arrange (moves open
/// windows only), Apply (also starts the apps the preset is set to start; people only, never a rule) and Undo (only while
/// the agent holds an undo for that preset). Editing a preset and the preset review screen are not here yet.
/// </summary>
public static class WindowsPage
{
    public static IReadOnlyList<CardRows> Sections(JsonNode? pulled)
    {
        if (pulled is not JsonObject root)
        {
            return [];
        }

        var sections = new List<CardRows>();
        foreach (var p in (root["presets"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (Str(p, "name") is not { } name)
            {
                continue;
            }

            var rows = new List<CardRow> { new(RowKind.Value, "Windows placed", Count(p, "slots")) };
            if (Num(p, "launches") is > 0 and var launches)
            {
                rows.Add(new CardRow(RowKind.Value, "Starts apps", launches.ToString(CultureInfo.InvariantCulture) + " of them"));
            }

            if (Num(p, "layouts") is > 0 and var layouts)
            {
                rows.Add(new CardRow(RowKind.Value, "FancyZones layouts", layouts.ToString(CultureInfo.InvariantCulture)));
            }

            if (Str(p, "hotkey") is { } hotkey)
            {
                rows.Add(new CardRow(RowKind.Value, "Hotkey", hotkey));
            }

            var target = new Dictionary<string, string> { ["preset"] = name };
            var actions = new List<CardAction>
            {
                new() { Id = "arrange", Label = "Arrange", Action = "preset.arrange", Target = target },
                new() { Id = "apply", Label = "Apply", Action = "preset.apply", Target = target },
            };
            if (p["can_undo"] is JsonValue u && u.TryGetValue<bool>(out var canUndo) && canUndo)
            {
                actions.Add(new CardAction { Id = "undo", Label = "Undo", Action = "preset.undo", Target = target });
            }

            sections.Add(new CardRows("windows.preset." + name, name, Severity.Normal, null, null, rows, actions, []));
        }

        if (sections.Count == 0)
        {
            sections.Add(new CardRows("windows.none", "Presets", Severity.Normal, null, null,
                [new CardRow(RowKind.Text, "No presets yet. Save the windows you have open as a preset and it shows up here.")], [], []));
        }

        return sections;
    }

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    private static long? Num(JsonObject o, string key) => BindingResolver.AsDouble(o[key]) is { } d ? (long)d : null;

    private static string Count(JsonObject o, string key) => Num(o, key)?.ToString(CultureInfo.InvariantCulture) ?? BindingResolver.Missing;

    /// <summary>
    /// "Save open windows as a preset" (U4 preset review): one row per open window, and the exact program and arguments
    /// "open it if it is not running" would run, quoted as typed, so the user sees it before saving.
    /// </summary>
    public static IReadOnlyList<SaveCandidate> SaveCandidates(Contracts.Presets.OpenWindowsReport? report) =>
    [
        .. (report?.Windows ?? []).Select(w => new SaveCandidate(
            w.Id,
            w.App + (string.IsNullOrWhiteSpace(w.Title) ? string.Empty : " · " + w.Title),
            (w.Monitor > 0 ? "Monitor " + w.Monitor.ToString(CultureInfo.InvariantCulture) + " · " : string.Empty) + w.Width.ToString(CultureInfo.InvariantCulture) + " × " + w.Height.ToString(CultureInfo.InvariantCulture),
            w.Launch is { } l ? "Open it if it is not running: " + string.Join(' ', new[] { l.Exe }.Concat(l.Args ?? []).Select(QuoteArg)) : null)),
    ];

    public static CardAction Save(string name, IEnumerable<string> windows, IEnumerable<string> launch) => new()
    {
        Id = "save",
        Label = "Save",
        Action = "preset.save",
        Args = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["name"] = (name ?? string.Empty).Trim(),
            ["windows"] = string.Join(',', windows ?? []),
            ["launch"] = string.Join(',', launch ?? []),
        },
    };

    private static string QuoteArg(string a) => a.Length == 0 || a.Any(char.IsWhiteSpace) || a.Contains('"', StringComparison.Ordinal) ? "\"" + a.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : a;
}

/// <summary>An open window offered for a new preset; <see cref="LaunchText"/> is null when it cannot be relaunched.</summary>
public sealed record SaveCandidate(string Id, string Label, string Detail, string? LaunchText);

