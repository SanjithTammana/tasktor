using System.Text;
using Tasktor.Capture.Browser;
using Tasktor.Capture.Deep;
using Tasktor.Capture.Host;
using Tasktor.Capture.Index;
using Tasktor.Capture.Recording;
using Tasktor.Capture.Security;
using Tasktor.Contracts.Platform;
using Tasktor.Core.Storage;

namespace Tasktor.Capture.Tests;

/// <summary>C21.2 in the capture host: batch embedding off the loop, the query's vector, G8 gating, worker output parsing.</summary>
public sealed class EmbedderTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 9, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tasktor-embed-" + Guid.NewGuid().ToString("N")[..8]);

    public EmbedderTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>A stand-in encoder: "car" words and "dog" words get one direction each.</summary>
    private sealed class Fake : IChunkEmbedder
    {
        public bool Fail { get; set; }

        public int Calls { get; private set; }

        public Task<IReadOnlyList<(long Id, sbyte[] Q, float S)>?> EmbedAsync(IReadOnlyList<(long Id, string Text)> items, CancellationToken cancel)
        {
            Calls++;
            if (Fail) return Task.FromResult<IReadOnlyList<(long, sbyte[], float)>?>(null);
            IReadOnlyList<(long, sbyte[], float)> v = [.. items.Select(i => (i.Id, Vec(i.Text), 1f / 127))];
            return Task.FromResult<IReadOnlyList<(long, sbyte[], float)>?>(v);
        }

        private static sbyte[] Vec(string text)
        {
            var q = new sbyte[4];
            if (text.Contains("automobile", StringComparison.OrdinalIgnoreCase) || text.Contains("car", StringComparison.OrdinalIgnoreCase)) q[0] = 127;
            if (text.Contains("puppy", StringComparison.OrdinalIgnoreCase) || text.Contains("dog", StringComparison.OrdinalIgnoreCase)) q[1] = 127;
            return q;
        }
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => T0;
    }

    private sealed class Probe : IDesktopProbe
    {
        public DesktopObservation Read() => DesktopObservation.UserDesktop;
    }

    [Fact]
    public async Task New_chunks_are_embedded_off_the_loop_and_a_paraphrase_query_finds_them()
    {
        var fake = new Fake();
        var index = ContentIndex.Open(SqliteDb.Open(":memory:"));
        var blocks = new CaptureBlocks(Path.Combine(_dir, "b.json"), TimeZoneInfo.Utc);
        using var session = new CaptureSession(new CaptureRecorder(new RawVault(Path.Combine(_dir, "raw"), new byte[32]), blocks, () => T0), new BrowserEndpoint(blocks, () => T0), new Clock(), new Probe(), () => SessionAvailability.UnlockedConnected)
        {
            Index = index,
            Embedder = fake,
        };
        index.Add(new ActivityDocument(1, T0, T0.AddMinutes(5), [new DocSection("typed", "renew the automobile insurance")]));
        index.Add(new ActivityDocument(1, T0.AddMinutes(30), T0.AddMinutes(35), [new DocSection("typed", "walk the puppy")]));

        await session.EmbedPending(index)!;
        Assert.Equal(2, index.EmbeddingCount);
        Assert.Null(session.EmbedPending(index)); // nothing left
        var hit = Assert.Single(session.Search("car cover", null)!);
        Assert.Contains("automobile", hit.Snippet, StringComparison.Ordinal);

        fake.Fail = true; // no vector for the query: words only
        Assert.Empty(session.Search("car cover", null)!);
        Assert.Single(session.Search("puppy", null)!);
    }

    [Fact]
    public void Without_a_passing_g8_report_there_is_no_embedder()
    {
        var model = Path.Combine(_dir, "model");
        var worker = Path.Combine(_dir, "worker");
        Directory.CreateDirectory(model);
        Directory.CreateDirectory(Path.Combine(worker, "tasktor_worker"));
        var python = Path.Combine(_dir, "python");
        File.WriteAllText(python, string.Empty);
        File.WriteAllText(Path.Combine(model, "model.onnx"), string.Empty);
        File.WriteAllText(Path.Combine(model, "vocab.txt"), string.Empty);
        Assert.Null(WorkerEmbedder.TryCreate(python, worker, model)); // no report
        File.WriteAllText(Path.Combine(model, "g8.json"), """{"gate": "G8", "pass": false, "gain": 0.01}""");
        Assert.Null(WorkerEmbedder.TryCreate(python, worker, model));
        File.WriteAllText(Path.Combine(model, "g8.json"), "not json");
        Assert.Null(WorkerEmbedder.TryCreate(python, worker, model));
        File.WriteAllText(Path.Combine(model, "g8.json"), """{"gate": "G8", "pass": true, "gain": 0.09}""");
        Assert.NotNull(WorkerEmbedder.TryCreate(python, worker, model));
    }

    [Fact]
    public void Worker_output_is_parsed_and_bad_lines_are_skipped()
    {
        var q = Convert.ToBase64String([1, 255, 127]);
        var out_ = new StringBuilder()
            .AppendLine("{\"id\": 4, \"q\": \"" + q + "\", \"s\": 0.01}")
            .AppendLine("not json")
            .AppendLine("{\"id\": 5, \"q\": \"" + q + "\", \"s\": -1}")
            .AppendLine("{\"id\": 6, \"q\": \"%%%\", \"s\": 0.5}")
            .ToString();
        var (id, v, s) = Assert.Single(WorkerEmbedder.Parse(out_));
        Assert.Equal(4, id);
        Assert.Equal([1, -1, 127], v);
        Assert.Equal(0.01f, s);
    }
}
