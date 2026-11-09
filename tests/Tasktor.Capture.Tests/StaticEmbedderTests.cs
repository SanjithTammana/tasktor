using System.Text.Json;
using Tasktor.Capture.Index;
using Tasktor.Tests.Shared;

namespace Tasktor.Capture.Tests;

/// <summary>C21.2 in a 1 MB budget: the in-process static embedder reproduces the Python encoder's vectors (fixture).</summary>
public sealed class StaticEmbedderTests
{
    private static readonly string Fixture = Path.Combine(RepoPaths.Fixtures, "embedding-static");

    [Fact]
    public void Vectors_match_the_python_encoder()
    {
        var e = StaticEmbedder.Load(Fixture)!;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Fixture, "expected.json")));
        var texts = doc.RootElement.GetProperty("texts").EnumerateArray().Select(t => t.GetString()!).ToList();
        var vectors = doc.RootElement.GetProperty("vectors").EnumerateArray().Select(v => v.EnumerateArray().Select(x => x.GetSingle()).ToArray()).ToList();
        for (var i = 0; i < texts.Count; i++)
        {
            var got = e.Vector(texts[i]);
            for (var d = 0; d < got.Length; d++) Assert.Equal(vectors[i][d], got[d], 5);
        }
    }

    [Fact]
    public async Task Paraphrases_land_closer_than_other_topics_and_unknown_text_gets_no_vector()
    {
        var e = StaticEmbedder.Load(Fixture)!;
        var v = await e.EmbedAsync([(1, "renew the automobile insurance"), (2, "car"), (3, "walk the puppy"), (4, "qqq")], CancellationToken.None);
        Assert.NotNull(v);
        Assert.Equal([1L, 2, 3, 4], v.Select(x => x.Id)); // "qqq" splits into known characters, so it still has a vector
        static double Cos((long, sbyte[] Q, float S) a, (long, sbyte[] Q, float S) b) => a.Q.Zip(b.Q, (x, y) => (double)x * y).Sum() * a.S * b.S;
        Assert.True(Cos(v[0], v[1]) > Cos(v[1], v[2]));
        Assert.Empty((await e.EmbedAsync([(9, "")], CancellationToken.None))!);
    }

    [Fact]
    public void Missing_or_malformed_tables_and_a_missing_g8_report_give_no_embedder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tasktor-static-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Null(StaticEmbedder.Load(dir));
            File.WriteAllBytes(Path.Combine(dir, "static.i8"), [(byte)'T', (byte)'S', (byte)'E', (byte)'1', 9, 0, 0, 0, 8, 0, 0, 0]);
            File.WriteAllText(Path.Combine(dir, "vocab.txt"), "a\n");
            Assert.Null(StaticEmbedder.Load(dir)); // truncated
            foreach (var f in new[] { "static.i8", "vocab.txt", "expected.json" }) File.Copy(Path.Combine(Fixture, f), Path.Combine(dir, f), overwrite: true);
            Assert.NotNull(StaticEmbedder.Load(dir));
            Assert.Null(StaticEmbedder.TryCreate(dir)); // no G8 report
            File.WriteAllText(Path.Combine(dir, "g8.json"), """{"gate": "G8", "pass": true}""");
            Assert.NotNull(StaticEmbedder.TryCreate(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
