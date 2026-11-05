using System.Text.Json;

namespace Tasktor.Core.Storage;

/// <summary>H14: local-only counts of Tasktor's own use (cards opened, actions used, rules fired). Numbers only; never sent anywhere.</summary>
public sealed class SelfUseCounter
{
    private readonly StoreLayout _layout;
    private readonly Action<string, byte[]> _write;
    private SelfUseFile _file = new();

    public SelfUseCounter(StoreLayout layout, Action<string, byte[]>? write = null)
    {
        _layout = layout;
        _write = write ?? ((p, b) => AtomicFile.Write(p, b));
    }

    public bool Dirty { get; private set; }

    public IReadOnlyDictionary<string, long> Counts => _file.Counts;

    public DateTimeOffset? Since => _file.Since;

    public void Load()
    {
        try
        {
            _file = File.Exists(_layout.SelfUse) ? JsonSerializer.Deserialize(File.ReadAllText(_layout.SelfUse), CoreJsonContext.Default.SelfUseFile) ?? new() : new();
        }
        catch (JsonException)
        {
            _file = new();
        }
    }

    public void Count(string feature, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(feature);
        if (_file.Since is null)
        {
            _file = _file with { Since = now };
        }

        _file.Counts[feature] = _file.Counts.GetValueOrDefault(feature) + 1;
        Dirty = true;
    }

    /// <summary>Write if changed (the agent calls this at most once a minute).</summary>
    public void Flush()
    {
        if (!Dirty)
        {
            return;
        }

        Dirty = false;
        _write(_layout.SelfUse, JsonSerializer.SerializeToUtf8Bytes(_file, CoreJsonContext.Default.SelfUseFile));
    }

    public void Clear()
    {
        _file = new();
        Dirty = false;
    }
}
