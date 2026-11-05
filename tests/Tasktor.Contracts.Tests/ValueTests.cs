using System.Text.Json;
using Tasktor.Contracts.Cards;
using Tasktor.Contracts.Rules;

namespace Tasktor.Contracts.Tests;

public sealed class ValueTests
{
    private static Value Read(string json) => JsonSerializer.Deserialize(json, ContractsJsonContext.Default.Block) is MetricBlock m
        ? m.Value
        : throw new InvalidOperationException();

    [Fact]
    public void Binding_is_recognised()
    {
        var v = Read("""{"kind":"Metric","label":"CPU","value":{"bind":"/state/system/cpu_pct"}}""");
        Assert.True(v.IsBinding);
        Assert.Equal("/state/system/cpu_pct", v.Bind);
    }

    [Fact]
    public void Object_with_more_than_bind_is_a_literal()
    {
        var v = Read("""{"kind":"Metric","label":"x","value":{"bind":"/a","x":1}}""");
        Assert.False(v.IsBinding);
    }

    [Theory]
    [InlineData("12", "12")]
    [InlineData("\"hi\"", "\"hi\"")]
    [InlineData("true", "true")]
    [InlineData("[1,2]", "[1,2]")]
    public void Literals_keep_their_json(string json, string raw)
    {
        var v = Read("{\"kind\":\"Metric\",\"label\":\"x\",\"value\":" + json + "}");
        Assert.Equal(raw, v.Raw);
    }

    [Fact]
    public void Literals_compare_by_value()
    {
        Assert.Equal(Value.Literal(3.5), Value.Literal(3.5));
        Assert.Equal(Value.Binding("/state/x"), Value.Binding("/state/x"));
        Assert.NotEqual(Value.Literal("3.5"), Value.Literal(3.5));
    }

    [Fact]
    public void Binding_must_be_a_json_pointer() => Assert.Throws<ArgumentException>(() => Value.Binding("state/x"));

    [Fact]
    public void Rules_accept_comments_and_trailing_commas()
    {
        const string text = """
            // user rules
            {
              "schema_version": 1,
              "rules": [
                { "id": "r1", /* inline */ "when": { "state": "on_battery" }, "then": [ { "pause": "refits" }, ], },
              ],
            }
            """;
        var file = JsonSerializer.Deserialize(text, ContractsJsonContext.Default.RulesFile);
        Assert.NotNull(file);
        Assert.Equal("r1", file.Rules![0].Id);
        Assert.Equal("refits", file.Rules[0].Then[0].Pause);
    }

    [Fact]
    public void Unknown_fields_are_ignored()
    {
        const string text = """{ "schema": 1, "id": "a", "updated": "2026-11-04T14:02:11Z", "writer_extra": { "x": 1 } }""";
        var p = JsonSerializer.Deserialize(text, ContractsJsonContext.Default.ProgressFile);
        Assert.Equal("a", p!.Id);
    }

    [Fact]
    public void Timestamps_are_written_in_utc_with_z()
    {
        const string text = """{ "schema": 1, "id": "a", "updated": "2026-11-04T16:02:11+02:00" }""";
        var p = JsonSerializer.Deserialize(text, ContractsJsonContext.Default.ProgressFile);
        var json = JsonSerializer.Serialize(p!, ContractsJsonContext.Default.ProgressFile);
        Assert.Contains("\"updated\":\"2026-11-04T14:02:11Z\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_block_kind_is_rejected()
    {
        const string text = """{"kind":"Media","priority":1}""";
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize(text, ContractsJsonContext.Default.Block));
    }
}
