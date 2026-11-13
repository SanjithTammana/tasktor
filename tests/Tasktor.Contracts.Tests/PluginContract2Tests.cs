using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tasktor.Contracts.Capture;
using Tasktor.Contracts.Plugins;
using Tasktor.Tests.Shared;

namespace Tasktor.Contracts.Tests;

/// <summary>
/// Plugin contract 2 (signed 2026-11-13). The fixtures round-trip in GSchemaTests; here: strict reading at every depth,
/// "did you mean", contract 1 left as it was, and the frozen browser-1 schema.
/// </summary>
public sealed class PluginContract2Tests
{
    private static readonly ContractsJsonContext Strict = ContractsJsonContext.Strict;

    private const string Manifest = """
        { "contract": 2, "id": "x", "name": "X", "version": "1.0.0", "command": ["x.exe"], "timeout": "5s",
          "reads": [], "network": [] %EXTRA% }
        """;

    private static string With(string extra) => Manifest.Replace("%EXTRA%", extra, StringComparison.Ordinal);

    [Fact]
    public void Every_capability_name_parses_and_names_one_capability()
    {
        var names = string.Join(", ", PluginCapabilities.Names.Select(n => "\"" + n + "\""));
        var m = JsonSerializer.Deserialize(With(", \"capabilities\": [" + names + "]"), Strict.PluginManifest2)!;

        Assert.Equal(Enum.GetValues<PluginCapability>(), m.Capabilities);
    }

    [Theory]
    [InlineData(", \"capabilites\": [\"notify\"]")] // top-level typo
    [InlineData(", \"capabilities\": [\"notfy\"]")] // capability typo
    [InlineData(", \"settings\": [{ \"key\": \"k\", \"label\": \"K\", \"type\": \"string\", \"defualt\": \"1\" }]")] // nested, contract-1 type
    public void Strict_refuses_unknown_names_at_every_depth(string extra)
    {
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize(With(extra), Strict.PluginManifest2));
    }

    [Fact]
    public void Unknown_request_kind_is_refused()
    {
        Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize("""{ "contract": 2, "requests": [{ "kind": "proc.end", "id": "r" }] }""", Strict.PluginOutput2));
    }

    [Fact]
    public void Contract_1_still_ignores_unknown_fields()
    {
        const string v1 = """
            { "contract": 1, "id": "x", "name": "X", "version": "1.0.0", "command": ["x.exe"], "interval": "60s",
              "timeout": "5s", "reads": [], "network": [], "someday": true }
            """;

        var m = JsonSerializer.Deserialize(v1, ContractsJsonContext.Default.PluginManifest)!;

        Assert.Equal("x", m.Id);
    }

    [Theory]
    [InlineData("capabilites", "capabilities")]
    [InlineData("watch-exes", "watch_exes")]
    [InlineData("min_taskor", "min_tasktor")]
    [InlineData("Timeout", "timeout")]
    public void A_misspelled_field_names_the_closest_one(string typo, string expected)
    {
        Assert.Equal(expected, ContractSpelling.Closest(typo, ContractSpelling.FieldNames(Strict.PluginManifest2)));
    }

    [Theory]
    [InlineData("notfy", "notify")]
    [InlineData("brwoser:block", "browser:block")]
    [InlineData("capture:browsers", "capture:browser")]
    [InlineData("act:jobs", "act:job")]
    public void A_misspelled_capability_names_the_closest_one(string typo, string expected)
    {
        Assert.Equal(expected, ContractSpelling.Closest(typo, PluginCapabilities.Names));
    }

    [Theory]
    [InlineData("process:end")] // not a typo of anything: a capability that does not exist
    [InlineData("toast")]
    [InlineData("x")]
    public void Nothing_close_suggests_nothing(string unknown)
    {
        Assert.Null(ContractSpelling.Closest(unknown, PluginCapabilities.Names.Concat(ContractSpelling.FieldNames(Strict.PluginOutput2))));
    }

    [Fact]
    public void A_tie_suggests_nothing()
    {
        Assert.Null(ContractSpelling.Closest("cat", ["cap", "car"]));
    }

    [Fact]
    public void The_refusal_reads_plainly()
    {
        Assert.Equal("Unknown field 'capabilites'. Did you mean 'capabilities'?",
            ContractSpelling.Refusal("field", "capabilites", ContractSpelling.FieldNames(Strict.PluginManifest2)));
        Assert.Equal("Unknown capability 'process:end'.", ContractSpelling.Refusal("capability", "process:end", PluginCapabilities.Names));
    }

    [Theory]
    [InlineData("YouTube.com")]
    [InlineData("https://youtube.com")]
    [InlineData("")]
    public void Site_blocks_take_bare_lowercase_hosts(string host)
    {
        var json = $$"""{ "type": "site_blocks", "at": "2026-11-13T20:00:00Z", "blocks": [{ "plugin": "p", "plugin_name": "P", "hosts": ["{{host}}"], "until": "2026-11-13T21:00:00Z" }] }""";

        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize(json, ContractsJsonContext.Default.BrowserMessage));
    }

    [Fact]
    public void Site_blocks_take_at_most_50_hosts()
    {
        var hosts = string.Join(", ", Enumerable.Range(0, 51).Select(i => $"\"h{i}.com\""));
        var json = $$"""{ "type": "site_blocks", "at": "2026-11-13T20:00:00Z", "blocks": [{ "plugin": "p", "plugin_name": "P", "hosts": [{{hosts}}], "until": "2026-11-13T21:00:00Z" }] }""";

        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize(json, ContractsJsonContext.Default.BrowserMessage));
    }

    [Fact]
    public void Browser_1_schema_is_frozen_as_published()
    {
        var bytes = File.ReadAllText(Path.Combine(RepoPaths.Schemas, "tasktor.browser-1.json")).Replace("\r\n", "\n", StringComparison.Ordinal);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(bytes)));

        Assert.Equal("702e8252f63736ff24278301ec3cb92d0870efa6c3241bc823778a3d471488db", hash);
    }

    [Fact]
    public void Browser_2_reads_every_browser_1_message()
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoPaths.Fixtures, "tasktor.browser-1"), "*.json"))
        {
            Assert.IsAssignableFrom<BrowserMessage>(JsonSerializer.Deserialize(File.ReadAllText(file), ContractsJsonContext.Default.BrowserMessage));
        }
    }
}
