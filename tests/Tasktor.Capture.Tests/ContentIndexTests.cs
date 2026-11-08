using Tasktor.Capture.Deep;
using Tasktor.Capture.Index;
using Tasktor.Core.Storage;

namespace Tasktor.Capture.Tests;

/// <summary>C21 content index (§9): chunks, near-duplicates, word + trigram search, forget, and encryption or nothing.</summary>
public sealed class ContentIndexTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 7, 9, 0, 0, TimeSpan.Zero);
    private readonly ContentIndex _index = ContentIndex.Open(SqliteDb.Open(":memory:"));

    public void Dispose() => _index.Dispose();

    private static ActivityDocument Doc(int minutes, params (string Heading, string Text)[] sections) =>
        new(1, T0.AddMinutes(minutes), T0.AddMinutes(minutes + 10), [.. sections.Select(s => new DocSection(s.Heading, s.Text))]);

    [Fact]
    public void A_phrase_seen_on_screen_is_found_by_words_and_by_part_of_a_word()
    {
        _index.Add(Doc(0, ("first seen view 1", "Quarterly revenue review for the northwind account"), ("typed", "send the deck to finance")));
        _index.Add(Doc(30, ("first seen view 2", "Kubernetes deployment rollout notes")));

        var hit = Assert.Single(_index.Search("revenue northwind"));
        Assert.Equal(T0, hit.Start);
        Assert.Contains("[revenue]", hit.Snippet, StringComparison.Ordinal);

        Assert.Equal(T0.AddMinutes(30), Assert.Single(_index.Search("bernet")).Start); // trigram: part of an identifier
        Assert.Empty(_index.Search("payroll"));
        Assert.Empty(_index.Search("  \"*  ")); // nothing searchable is not an error
    }

    [Fact]
    public void Hits_are_grouped_by_activity_best_first_and_limited_by_date()
    {
        _index.Add(Doc(0, ("typed", "budget budget budget plan")));
        _index.Add(Doc(60, ("typed", "budget once")));
        var hits = _index.Search("budget");
        Assert.Equal([T0, T0.AddMinutes(60)], hits.Select(h => h.Start));
        Assert.Equal([T0.AddMinutes(60)], _index.Search("budget", from: T0.AddMinutes(30)).Select(h => h.Start));
    }

    [Fact]
    public void Long_documents_are_chunked_with_overlap_and_near_duplicates_are_dropped()
    {
        var words = string.Join(' ', Enumerable.Range(0, 900).Select(i => "word" + i));
        var added = _index.Add(Doc(0, ("first seen view 1", words)));
        Assert.True(added >= 3, "a ~7 KB section becomes several 1–2 KB chunks");
        Assert.Single(_index.Search("word450")); // found once per activity, whichever chunk holds it

        Assert.Equal(0, _index.Add(Doc(20, ("first seen view 1", words)))); // the same screen again: nothing new to index
        Assert.Equal(added, _index.ChunkCount);
    }

    [Fact]
    public void Forget_removes_a_time_range_from_both_searches()
    {
        _index.Add(Doc(0, ("typed", "launch checklist alpha")));
        _index.Add(Doc(120, ("typed", "launch checklist beta")));
        Assert.Equal(1, _index.Forget(T0, T0.AddMinutes(60)));
        Assert.Equal([T0.AddMinutes(120)], _index.Search("launch").Select(h => h.Start));
        Assert.Empty(_index.Search("lpha")); // the trigram table forgot it too
    }

    [Fact]
    public void Ingest_reads_closed_documents_from_the_vault_once()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tasktor-index-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var vault = new RawVault(dir, new byte[32]);
            var now = T0;
            var recorder = new Recording.CaptureRecorder(vault, new CaptureBlocks(Path.Combine(dir, "blocks.json"), TimeZoneInfo.Utc), () => now);
            recorder.Document(Doc(0, ("typed", "invoice reconciliation")));
            Assert.Equal(1, _index.Ingest(vault, T0.AddDays(1)));
            Assert.Equal(0, _index.Ingest(vault, T0.AddDays(1))); // resumes after the last record it read
            now = T0.AddMinutes(30);
            recorder.Document(Doc(30, ("typed", "second document")));
            Assert.Equal(1, _index.Ingest(vault, T0.AddDays(1)));
            Assert.Equal(2, _index.Search("invoice").Count + _index.Search("second").Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void On_disk_the_index_is_encrypted_and_opens_only_with_its_key()
    {
        // This test assembly binds SQLite to SQLCipher, as tasktor-capture does at start (OQ-C22).
        var path = Path.Combine(Path.GetTempPath(), "tasktor-index-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        try
        {
            using (var index = ContentIndex.OpenEncrypted(path, key))
            {
                Assert.NotNull(index);
                index!.Add(Doc(0, ("typed", "zebracorn quarterly plaintextcanary")));
            }

            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*"))
            {
                Assert.DoesNotContain("plaintextcanary", System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(file)), StringComparison.Ordinal);
            }

            using (var again = ContentIndex.OpenEncrypted(path, key))
            {
                Assert.Single(again!.Search("zebracorn"));
            }

            var wrong = key.ToArray();
            wrong[0] ^= 0xFF;
            Assert.Null(ContentIndex.OpenEncrypted(path, wrong)); // another key: no index, never a plain one
        }
        finally
        {
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*"))
            {
                File.Delete(file);
            }
        }
    }
}
