using Tasktor.Core.Storage;
using Tasktor.Tests.Shared;

namespace Tasktor.Core.Tests;

/// <summary>consents.json written by hand or by an older build may lack sections; a missing section means "none".</summary>
public sealed class ConsentStoreTests : IDisposable
{
    private readonly TempStore _store = new();

    public void Dispose() => _store.Dispose();

    private ConsentStore Load(string json)
    {
        Directory.CreateDirectory(_store.Layout.StateDir);
        File.WriteAllText(_store.Layout.Consents, json);
        var c = new ConsentStore(_store.Layout);
        c.Load();
        return c;
    }

    [Theory]
    [InlineData("""{ "schema_version": 1, "modules": { "capture": "2026-11-06T10:00:00Z" } }""")]
    [InlineData("""{ "schema_version": 1 }""")]
    [InlineData("""{ "modules": null, "plugins": null, "arrange_rules": null }""")]
    public void Missing_or_null_sections_read_as_empty(string json)
    {
        var c = Load(json);
        Assert.False(c.PluginAllowed("weather", "a", "b"));
        Assert.Empty(c.Current.Plugins);
        Assert.NotNull(c.Current.ArrangeRules);
        c.GiveModule("focus", DateTimeOffset.UnixEpoch); // and it can still be written to
        Assert.True(c.HasModule("focus"));
    }

    [Fact]
    public void Present_sections_are_kept()
    {
        var c = Load("""{ "schema_version": 1, "modules": { "capture": "2026-11-06T10:00:00Z" } }""");
        Assert.True(c.HasModule("capture"));
    }
}
