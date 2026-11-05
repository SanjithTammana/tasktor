using System.Text.Json;
using Tasktor.Contracts;
using Tasktor.Contracts.Config;

namespace Tasktor.Core.Storage;

/// <summary>
/// <c>config.json</c> (U1 D-H5): load with comments, migrate forward with a <c>.bak</c>, save as a byte copy written off the
/// loop. A file that does not parse is left untouched (the user can fix it) and defaults apply meanwhile.
/// </summary>
public sealed class SettingsStore
{
    public const int CurrentVersion = 1;

    private readonly StoreLayout _layout;
    private readonly Action<string, byte[]> _write;

    /// <param name="write">Writes bytes to a path off the loop (the agent's <c>CoalescingWriter</c>); tests write inline.</param>
    public SettingsStore(StoreLayout layout, Action<string, byte[]>? write = null)
    {
        _layout = layout;
        _write = write ?? ((path, bytes) => AtomicFile.WriteWithBackup(path, bytes));
    }

    public ConfigFile Current { get; private set; } = new();

    /// <summary>The last load problem (shown on an error card), or null.</summary>
    public string? Error { get; private set; }

    public static readonly IReadOnlyDictionary<string, Threshold> DefaultThresholds = new Dictionary<string, Threshold>(StringComparer.Ordinal)
    {
        ["cpu"] = new() { Warn = 80, Critical = 95 },
        ["ram"] = new() { Warn = 85, Critical = 95 },
        ["gpu"] = new() { Warn = 85, Critical = 95 },
        ["disk_active"] = new() { Warn = 80, Critical = 95 },
    };

    public static readonly IReadOnlyList<FocusPreset> DefaultFocusPresets =
    [
        new() { Name = "Pomodoro 25/5", Blocks = [new() { Kind = "focus", Minutes = 25 }, new() { Kind = "break", Minutes = 5 }], LongBreakEvery = 4, LongBreakMinutes = 15 },
        new() { Name = "50/10", Blocks = [new() { Kind = "focus", Minutes = 50 }, new() { Kind = "break", Minutes = 10 }] },
        new() { Name = "90/20", Blocks = [new() { Kind = "focus", Minutes = 90 }, new() { Kind = "break", Minutes = 20 }] },
    ];

    public bool AllowDestructiveCli => Current.Cli?.AllowDestructive == true;

    public bool ModuleOn(string id) => Current.Modules?.GetValueOrDefault(id) == true;

    public Threshold ThresholdFor(string metric) =>
        Current.Thresholds?.GetValueOrDefault(metric) ?? DefaultThresholds.GetValueOrDefault(metric) ?? new Threshold { Warn = 80, Critical = 95 };

    public IReadOnlyList<FocusPreset> FocusPresets => Current.Focus?.Presets is { Count: > 0 } p ? [.. DefaultFocusPresets, .. p] : DefaultFocusPresets;

    public bool JobLearning => Current.Jobs?.Learning != false;

    public ConfigFile Load()
    {
        Error = null;
        if (!File.Exists(_layout.Config))
        {
            Current = new ConfigFile();
            return Current;
        }

        try
        {
            var text = File.ReadAllText(_layout.Config);
            var file = Parse(text);
            if (file.SchemaVersion > CurrentVersion)
            {
                Error = "config.json schema_version " + file.SchemaVersion + " is newer than this Tasktor; using defaults";
                Current = new ConfigFile();
                return Current;
            }

            if (file.SchemaVersion < CurrentVersion)
            {
                file = Migrate(file);
                _write(_layout.Config, Serialize(file));
            }

            Current = file;
        }
        catch (JsonException ex)
        {
            Error = "config.json line " + ((ex.LineNumber ?? 0) + 1) + ": " + ex.Message.Split('\n')[0];
            Current = new ConfigFile();
        }

        return Current;
    }

    /// <summary>Validate text as a config file (import validates before replacing).</summary>
    public static ConfigFile Parse(string text) =>
        JsonSerializer.Deserialize(text, ContractsJsonContext.Default.ConfigFile) ?? throw new JsonException("empty config");

    public void Save(ConfigFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        Current = file with { SchemaVersion = CurrentVersion };
        _write(_layout.Config, Serialize(Current));
    }

    public void Update(Func<ConfigFile, ConfigFile> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Save(change(Current));
    }

    public static byte[] Serialize(ConfigFile file) => JsonSerializer.SerializeToUtf8Bytes(file, ContractsJsonContext.Indented.ConfigFile);

    /// <summary>Forward migration. v0 (no version field) becomes v1 unchanged.</summary>
    private static ConfigFile Migrate(ConfigFile file) => file with { SchemaVersion = CurrentVersion };
}
