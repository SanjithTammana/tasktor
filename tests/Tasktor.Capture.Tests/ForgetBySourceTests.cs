using System.Text;
using Tasktor.Capture.Browser;
using Tasktor.Capture.Deep;
using Tasktor.Capture.Index;
using Tasktor.Capture.Recording;
using Tasktor.Contracts.Capture;
using Tasktor.Core.Storage;

namespace Tasktor.Capture.Tests;

/// <summary>C21.4: forget by app or site removes vault records and index rows together; activities that included it go whole.</summary>
public sealed class ForgetBySourceTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 9, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tasktor-forget-" + Guid.NewGuid().ToString("N")[..8]);

    public ForgetBySourceTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ViewDiff Diff(string view, string line, DateTimeOffset at) =>
        new(new ViewKey(1, (ulong)view[0]), at, 1, 0, [new DiffLine(1, 0, 0, null, line)], [], DiffSource.Uia);

    [Fact]
    public void Records_carry_their_source_and_forgetting_an_app_or_site_removes_them_and_documents_that_included_it()
    {
        var now = T0;
        var blocks = new CaptureBlocks(Path.Combine(_dir, "blocks.json"), TimeZoneInfo.Utc);
        var vault = new RawVault(Path.Combine(_dir, "raw"), new byte[32]);
        var recorder = new CaptureRecorder(vault, blocks, () => now);
        var documents = new ActivityDocumentBuilder();

        Assert.True(recorder.View(Diff("a", "quarterly numbers", now), "excel.exe", null));
        documents.Add(1, Diff("a", "quarterly numbers", now));
        documents.Note(1, "excel.exe", null);
        Assert.True(recorder.Document(documents.Close(1, now)!));

        now = T0.AddMinutes(5);
        Assert.True(recorder.View(Diff("b", "release notes", now), "chrome.exe", "docs.example.com"));
        documents.Add(1, Diff("b", "release notes", now));
        documents.Note(1, "chrome.exe", "docs.example.com");
        Assert.True(recorder.Document(documents.Close(1, now)!));
        Assert.True(recorder.View(Diff("c", "an unrelated page", now), "chrome.exe", "news.example.org"));

        Assert.Equal(2, recorder.ForgetMatching(app: "excel.exe", host: null)); // its view diff and the document that included it
        Assert.Equal(2, recorder.ForgetMatching(app: null, host: "example.com")); // subdomains too, like a site block
        var left = vault.Read(T0.AddDays(-1), T0.AddDays(1)).Select(r => Encoding.UTF8.GetString(r.Body)).ToList();
        var only = Assert.Single(left);
        Assert.Contains("an unrelated page", only, StringComparison.Ordinal);
        Assert.Equal(1, recorder.RecordsToday);
        Assert.Equal(0, recorder.ForgetMatching(app: null, host: null)); // nothing named: nothing forgotten
    }

    [Fact]
    public void The_index_forgets_activities_that_included_the_app_or_site()
    {
        using var index = ContentIndex.Open(SqliteDb.Open(":memory:"));
        index.Add(new ActivityDocument(1, T0, T0.AddMinutes(5), [new DocSection("typed", "budget spreadsheet for the offsite")]) { Apps = ["EXCEL.EXE"] });
        index.Add(new ActivityDocument(1, T0.AddMinutes(10), T0.AddMinutes(15), [new DocSection("view", "deployment guide for the cluster")]) { Apps = ["chrome.exe"], Hosts = ["docs.example.com"] });
        index.Add(new ActivityDocument(1, T0.AddMinutes(20), T0.AddMinutes(25), [new DocSection("view", "weather forecast for the weekend")]) { Apps = ["chrome.exe"], Hosts = ["weather.example.org"] });

        Assert.Equal(1, index.Forget(app: "excel.exe", host: null));
        Assert.Empty(index.Search("budget"));
        Assert.Equal(1, index.Forget(app: null, host: "example.com"));
        Assert.Empty(index.Search("deployment"));
        Assert.Single(index.Search("weather"));
        Assert.Equal(0, index.Forget(app: "notepad.exe", host: null));
    }

    [Fact]
    public void Only_a_person_can_forget_an_app_or_site_over_the_control_pipe()
    {
        var blocks = new CaptureBlocks(Path.Combine(_dir, "blocks.json"), TimeZoneInfo.Utc);
        (string? App, string? Host)? asked = null;
        var control = new CaptureControl(blocks, () => T0) { ForgetMatching = (app, host) => { asked = (app, host); return 3; } };

        var byRule = control.Handle(new CaptureCommand { Verb = CaptureVerb.Forget, ByUser = false, App = "excel.exe" });
        Assert.Equal("invalid", byRule.Refusal);
        Assert.Null(asked);

        var byUser = control.Handle(new CaptureCommand { Verb = CaptureVerb.Forget, ByUser = true, Site = "example.com" });
        Assert.Null(byUser.Refusal);
        Assert.Equal(3, byUser.Forgotten);
        Assert.Equal((null, "example.com"), asked);
    }
}
