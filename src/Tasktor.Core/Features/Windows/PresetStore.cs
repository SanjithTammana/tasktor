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
        catch (IOException)
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
        if (file.Schema != ContractIds.Presets || file.Presets.Any(p => string.IsNullOrWhiteSpace(p.Name) || p.Slots.Count(s => s.Focus == true) > 1 ||
            p.Slots.Any(s => s.Desktop != "current" || string.IsNullOrWhiteSpace(s.App.AppId) || string.IsNullOrWhiteSpace(s.Place.Monitor) || s.Place.Rect.W <= 0 || s.Place.Rect.H <= 0)) ||
            file.Presets.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != file.Presets.Count)
        {
            throw new JsonException("Invalid preset");
        }
    }
}
