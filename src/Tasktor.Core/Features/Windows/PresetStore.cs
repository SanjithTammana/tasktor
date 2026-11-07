using System.Text.Json;
using Tasktor.Contracts;
using Tasktor.Contracts.Presets;
using Tasktor.Core.Windows;

namespace Tasktor.Core.Features.Windows;

/// <summary>The user's presets file; invalid files are left untouched.</summary>
public sealed class PresetStore(FeatureContext context)
{
    public PresetsFile Current { get; private set; } = new() { Presets = [] };

    public string? Error { get; private set; }

    public PresetsFile Load()
    {
        Error = null;
        if (!File.Exists(context.Store.Presets))
        {
            Current = new PresetsFile { Presets = [] };
            return Current;
        }

        try
        {
            var file = JsonSerializer.Deserialize(File.ReadAllText(context.Store.Presets), ContractsJsonContext.Default.PresetsFile) ?? throw new JsonException("empty presets file");
            Validate(file);
            Current = file;
        }
        catch (JsonException ex)
        {
            Error = "presets.json line " + ((ex.LineNumber ?? 0) + 1) + ": " + ex.Message.Split('\n')[0];
            Current = new PresetsFile { Presets = [] };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = "Unable to read presets.json";
            Current = new PresetsFile { Presets = [] };
        }

        return Current;
    }

    public void Save(PresetsFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        Validate(file);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(file, ContractsJsonContext.Indented.PresetsFile);
        Current = file;
        context.Persist(context.Store.Presets, bytes);
    }

    public Preset SaveOpen(string name, IReadOnlyList<WindowInfo> windows, IReadOnlySet<long> ticked, MonitorMap monitors)
    {
        var preset = PresetBuilder.FromOpen(name, windows, ticked, monitors);
        Save(Current with { Presets = [.. Current.Presets.Where(p => !string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase)), preset] });
        return preset;
    }

    private static void Validate(PresetsFile file)
    {
        if (file.Schema != ContractIds.Presets || file.Presets is null) throw new JsonException("Invalid preset file");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var preset in file.Presets)
        {
            if (preset is null || string.IsNullOrWhiteSpace(preset.Name) || !names.Add(preset.Name) || preset.Slots is null)
                throw new JsonException("Invalid preset");
            if (preset.Fancyzones?.Any(layout => layout is null || string.IsNullOrWhiteSpace(layout.Monitor) || string.IsNullOrWhiteSpace(layout.Layout)) == true)
                throw new JsonException("Invalid FancyZones layout");
            var focused = 0;
            foreach (var slot in preset.Slots)
            {
                if (slot is null || slot.App is null || slot.Place is null || slot.Place.Rect is null || slot.Desktop != "current" ||
                    string.IsNullOrWhiteSpace(slot.App.AppId) || string.IsNullOrWhiteSpace(slot.Place.Monitor) || slot.Place.Rect.W <= 0 || slot.Place.Rect.H <= 0 ||
                    (slot.Launch is { } launch && (string.IsNullOrWhiteSpace(launch.Exe) || launch.Args?.Any(a => a is null) == true)))
                    throw new JsonException("Invalid preset slot");
                if (slot.Focus == true && ++focused > 1) throw new JsonException("More than one focused slot");
            }
        }
    }
}
