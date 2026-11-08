using System.Text.Json;

namespace Tasktor.Core.Storage;

/// <summary>Consents (U1 D-H7, D-C22, U3 OQ-W2): modules, T1 capture, plugins pinned to hashes, arrange rules. Loop-only.</summary>
public sealed class ConsentStore
{
    private readonly StoreLayout _layout;
    private readonly Action<string, byte[]> _write;
    private ConsentsFile _file = new();

    public ConsentStore(StoreLayout layout, Action<string, byte[]>? write = null)
    {
        _layout = layout;
        _write = write ?? ((p, b) => AtomicFile.Write(p, b));
    }

    public ConsentsFile Current => _file;

    public void Load()
    {
        try
        {
            _file = File.Exists(_layout.Consents)
                ? JsonSerializer.Deserialize(File.ReadAllText(_layout.Consents), CoreJsonContext.Default.ConsentsFile) ?? new()
                : new();
        }
        catch (JsonException)
        {
            // A corrupt consent file means "no consents": every Module and plugin asks again.
            _file = new();
        }
    }

    public bool HasModule(string id) => _file.Modules.ContainsKey(id);

    public void GiveModule(string id, DateTimeOffset at)
    {
        _file.Modules[id] = at;
        Save();
    }

    public void RevokeModule(string id)
    {
        if (_file.Modules.Remove(id))
        {
            Save();
        }
    }

    /// <summary>True only when a consent exists and both pinned hashes still match (any change asks again).</summary>
    public bool PluginAllowed(string id, string manifestSha256, string commandSha256) =>
        _file.Plugins.TryGetValue(id, out var c)
        && string.Equals(c.ManifestSha256, manifestSha256, StringComparison.OrdinalIgnoreCase)
        && string.Equals(c.CommandSha256, commandSha256, StringComparison.OrdinalIgnoreCase);

    public void GivePlugin(string id, PluginConsent consent)
    {
        _file.Plugins[id] = consent;
        Save();
    }

    public void RevokePlugin(string id)
    {
        if (_file.Plugins.Remove(id))
        {
            Save();
        }
    }

    public bool HasArrangeConsent(string ruleId) => _file.ArrangeRules.ContainsKey(ruleId);

    public void GiveArrange(string ruleId, DateTimeOffset at)
    {
        _file.ArrangeRules[ruleId] = at;
        Save();
    }

    private void Save() => _write(_layout.Consents, JsonSerializer.SerializeToUtf8Bytes(_file, CoreJsonContext.Default.ConsentsFile));
}
