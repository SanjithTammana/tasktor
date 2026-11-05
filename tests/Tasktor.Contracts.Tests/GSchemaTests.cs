using System.Text.Json;
using System.Text.Json.Nodes;
using Tasktor.SchemaGen;
using Tasktor.Tests.Shared;

namespace Tasktor.Contracts.Tests;

/// <summary>
/// G-schema (U1 D-H11), C# half: every fixture parses into its record and serialises back to the same JSON,
/// so the JSON that Python validates (worker/tests/test_gschema.py) is exactly what C# writes; and the
/// committed schemas equal what SchemaGen generates from the records now.
/// </summary>
public sealed class GSchemaTests
{
    public static TheoryData<string, string> Fixtures()
    {
        var data = new TheoryData<string, string>();
        foreach (var entry in SchemaCatalog.Entries)
        {
            var dir = Path.Combine(RepoPaths.Fixtures, Path.GetFileNameWithoutExtension(entry.File));
            foreach (var file in Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.Ordinal))
            {
                data.Add(entry.File, Path.GetFileName(file));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixture_round_trips_through_its_record(string schemaFile, string fixture)
    {
        var entry = SchemaCatalog.Entries.Single(e => e.File == schemaFile);
        var path = Path.Combine(RepoPaths.Fixtures, Path.GetFileNameWithoutExtension(schemaFile), fixture);
        var text = File.ReadAllText(path);

        var record = JsonSerializer.Deserialize(text, entry.TypeInfo);
        Assert.NotNull(record);
        var written = JsonSerializer.Serialize(record, entry.TypeInfo);

        var expected = JsonNode.Parse(text);
        var actual = JsonNode.Parse(written);
        Assert.True(JsonNode.DeepEquals(expected, actual), $"{fixture}: C# wrote\n{written}\nexpected\n{text}");

        var again = JsonSerializer.Serialize(JsonSerializer.Deserialize(written, entry.TypeInfo), entry.TypeInfo);
        Assert.Equal(written, again);
    }

    [Fact]
    public void Every_public_contract_has_a_fixture()
    {
        foreach (var entry in SchemaCatalog.Entries)
        {
            var dir = Path.Combine(RepoPaths.Fixtures, Path.GetFileNameWithoutExtension(entry.File));
            Assert.True(Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.json").Any(), "no fixture for " + entry.File);
        }
    }

    [Fact]
    public void Committed_schemas_are_current()
    {
        foreach (var entry in SchemaCatalog.Entries)
        {
            var path = Path.Combine(RepoPaths.Schemas, entry.File);
            Assert.True(File.Exists(path), "missing " + path + " (run: dotnet run --project tools/Tasktor.SchemaGen)");
            var committed = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert.True(committed == SchemaCatalog.Generate(entry), entry.File + " is stale (run: dotnet run --project tools/Tasktor.SchemaGen)");
        }
    }

    [Fact]
    public void No_stray_schema_files()
    {
        var known = SchemaCatalog.Entries.Select(e => e.File).ToHashSet(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(RepoPaths.Schemas, "*.json"))
        {
            Assert.Contains(Path.GetFileName(file), known);
        }
    }

    [Theory]
    [InlineData("tasktor.job-1.json", "missing-schema.json")]
    [InlineData("tasktor.snapshot-1.json", "no-generated-at.json")]
    [InlineData("tasktor.rules-1.json", "no-when.json")]
    [InlineData("tasktor.agent-1.json", "bad-state.json")]
    [InlineData("tasktor.job-1.json", "bad-state.json")]
    public void Invalid_fixture_is_rejected_by_csharp_too(string schemaFile, string fixture)
    {
        var entry = SchemaCatalog.Entries.Single(e => e.File == schemaFile);
        var text = File.ReadAllText(Path.Combine(RepoPaths.Fixtures, "invalid", Path.GetFileNameWithoutExtension(schemaFile), fixture));
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize(text, entry.TypeInfo));
    }
}
