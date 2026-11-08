using Tasktor.Capture.Deep;
using Tasktor.Capture.Index;
using Tasktor.Capture.Recording;
using Tasktor.Core.Storage;

namespace Tasktor.Capture.Tests;

/// <summary>C21 "reopen from a hit": a page's address rides with its activity into the index and back out with the hit.</summary>
public sealed class ReopenLinkTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 9, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tasktor-reopen-" + Guid.NewGuid().ToString("N")[..8]);

    public ReopenLinkTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void A_documents_links_are_its_pages_addresses_scrubbed_web_only_and_bounded()
    {
        var b = new ActivityDocumentBuilder();
        b.Add(1, new ViewDiff(new ViewKey(1, 1), T0, 1, 0, [new DiffLine(1, 0, 0, null, "text")], [], DiffSource.Extension));
        b.Link(1, "https://docs.example.com/guide?id=7");
        b.Link(1, "https://docs.example.com/guide?id=7"); // once
        b.Link(1, "file:///C:/secret.txt"); // not a web page: never a link
        b.Link(1, "javascript:alert(1)");
        for (var i = 0; i < 20; i++)
        {
            b.Link(1, "https://example.org/p" + i);
        }

        var doc = b.Close(1, T0)!;
        Assert.Equal("https://docs.example.com/guide?id=7", doc.Links[0]);
        Assert.Equal(ActivityDocumentBuilder.MaxLinks, doc.Links.Count);
        Assert.All(doc.Links, l => Assert.StartsWith("https://", l, StringComparison.Ordinal));
    }

    [Fact]
    public void Page_text_records_its_address_with_secrets_scrubbed()
    {
        var now = T0;
        var blocks = new CaptureBlocks(Path.Combine(_dir, "blocks.json"), TimeZoneInfo.Utc);
        var vault = new RawVault(Path.Combine(_dir, "raw"), new byte[32]);
        var recorder = new CaptureRecorder(vault, blocks, () => now);
        var runs = new KeyRunBuilder(new KeyDecoder(), new ActorAttributor(() => []), new SecretRedactor(), (new Suppressor(blocks) { SecureDesktop = () => false }).Check, ms => T0.AddMilliseconds(ms));
        var deep = new DeepCapture(new InputRing(), runs, new SnapshotDiffer(), new SecretRedactor(), recorder, () => now, () => 0);
        var walk = new UiaWalk(new ViewKey(7, 1), [new SnapLine(1, 0, 0, 0, -1)], ["Release notes for the new version"]);

        deep.Walk(walk, "chrome.exe", "docs.example.com", DiffSource.Extension, link: "https://docs.example.com/notes?token=eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.c2lnbmF0dXJlLXZhbHVlLWhlcmU");
        deep.CloseActivity();

        var docs = vault.Read(T0.AddDays(-1), T0.AddDays(1)).Where(r => r.Kind == RecordKind.Document).ToList();
        var doc = System.Text.Json.JsonSerializer.Deserialize(Assert.Single(docs).Body, RecordJsonContext.Default.ActivityDocument)!;
        var link = Assert.Single(doc.Links);
        Assert.StartsWith("https://docs.example.com/notes?", link, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", link, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hit_carries_its_activitys_links_and_forgetting_drops_them()
    {
        using var index = ContentIndex.Open(SqliteDb.Open(":memory:"));
        index.Add(new ActivityDocument(1, T0, T0.AddMinutes(5), [new DocSection("view", "kubernetes deployment guide")]) { Hosts = ["docs.example.com"], Links = ["https://docs.example.com/k8s"] });
        index.Add(new ActivityDocument(1, T0.AddHours(1), T0.AddHours(1).AddMinutes(5), [new DocSection("typed", "kubernetes notes typed locally")]) { Apps = ["notepad.exe"] });

        var hits = index.Search("kubernetes");
        Assert.Equal(["https://docs.example.com/k8s"], hits.Single(h => h.Start == T0).Links);
        Assert.Empty(hits.Single(h => h.Start != T0).Links);

        index.Forget(app: null, host: "example.com");
        Assert.Equal(0, index.LinkCount);
        index.Add(new ActivityDocument(1, T0.AddHours(2), T0.AddHours(2).AddMinutes(1), [new DocSection("view", "another page")]) { Links = ["https://example.org/a"] });
        index.Forget(T0.AddHours(2), T0.AddHours(3));
        Assert.Equal(0, index.LinkCount);
    }
}
