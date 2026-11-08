using Tasktor.Capture.Deep;
using Tasktor.Capture.Index;
using Tasktor.Core.Storage;

namespace Tasktor.Capture.Tests;

/// <summary>C21.2: chunk embeddings beside the FTS lists, fused by RRF; forget removes them; no vector, FTS only.</summary>
public sealed class EmbeddingIndexTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 7, 9, 0, 0, TimeSpan.Zero);
    private readonly ContentIndex _index = ContentIndex.Open(SqliteDb.Open(":memory:"));

    public void Dispose() => _index.Dispose();

    private static ActivityDocument Doc(int minutes, string text) =>
        new(1, T0.AddMinutes(minutes), T0.AddMinutes(minutes + 10), [new DocSection("typed", text)]);

    private static (sbyte[] Q, float S) Vec(params int[] hot)
    {
        var q = new sbyte[8];
        foreach (var h in hot)
        {
            q[h] = 127;
        }

        return (q, 1f / 127);
    }

    [Fact]
    public void A_paraphrase_finds_an_activity_through_its_embedding()
    {
        _index.Add(Doc(0, "renew the automobile insurance policy"));
        _index.Add(Doc(30, "walk the puppy around the park"));
        var pending = _index.Unembedded(10);
        Assert.Equal(2, pending.Count);
        _index.SetEmbedding(pending[0].Id, Vec(0).Q, Vec(0).S);
        _index.SetEmbedding(pending[1].Id, Vec(1).Q, Vec(1).S);
        Assert.Empty(_index.Unembedded(10));

        Assert.Empty(_index.Search("car cover")); // no shared word: FTS alone finds nothing
        var hit = Assert.Single(_index.Search("car cover", query: Vec(0)), h => h.Score >= 1.0 / (ContentIndex.RrfK + 1));
        Assert.Equal(T0, hit.Start);
        Assert.Contains("automobile", hit.Snippet, StringComparison.Ordinal);
    }

    [Fact]
    public void Words_and_vectors_fuse_and_the_date_range_applies_to_both()
    {
        _index.Add(Doc(0, "quarterly budget review"));
        _index.Add(Doc(60, "budget"));
        foreach (var (id, _) in _index.Unembedded(10))
        {
            _index.SetEmbedding(id, Vec(2).Q, Vec(2).S);
        }

        var both = _index.Search("budget", query: Vec(2));
        Assert.Equal(2, both.Count);
        Assert.Equal([T0.AddMinutes(60)], _index.Search("budget", from: T0.AddMinutes(30), query: Vec(2)).Select(h => h.Start));
    }

    [Fact]
    public void Forget_removes_embeddings_and_a_vanished_chunk_takes_none()
    {
        _index.Add(Doc(0, "draft the launch notes"));
        var (id, _) = Assert.Single(_index.Unembedded(10));
        _index.SetEmbedding(id, Vec(3).Q, Vec(3).S);
        Assert.Equal(1, _index.EmbeddingCount);
        _index.Forget(T0.AddMinutes(-1), T0.AddMinutes(1));
        Assert.Equal(0, _index.EmbeddingCount);
        _index.SetEmbedding(id, Vec(3).Q, Vec(3).S); // the chunk is gone: nothing stored
        Assert.Equal(0, _index.EmbeddingCount);
    }
}
