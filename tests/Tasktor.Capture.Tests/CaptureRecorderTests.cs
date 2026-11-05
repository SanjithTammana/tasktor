using System.Text;
using Tasktor.Capture.Browser;
using Tasktor.Capture.Deep;
using Tasktor.Capture.Recording;
using Tasktor.Contracts.Capture;
using Tasktor.Contracts.Platform;

namespace Tasktor.Capture.Tests;

/// <summary>M12 capture host: the recorder writes host-only context into the vault and honours every block at write time.</summary>
public sealed class CaptureRecorderTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tasktor-rec-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = T0;

    public CaptureRecorderTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private (CaptureBlocks Blocks, RawVault Vault, CaptureRecorder Recorder) World()
    {
        var blocks = new CaptureBlocks(Path.Combine(_dir, "blocks.json"));
        var vault = new RawVault(Path.Combine(_dir, "raw"), new byte[32]);
        return (blocks, vault, new CaptureRecorder(vault, blocks, () => _now));
    }

    private static TabContext Ctx(string? host, bool secure = false) => new(host is null ? null : Sensitive.Of(host), secure, Visible: true, Active: true);

    [Fact]
    public void Writes_host_only_context_and_counts_today()
    {
        var (_, vault, rec) = World();
        Assert.True(rec.Browser("chrome", 7, Ctx("docs.example.org")));
        _now = T0.AddMinutes(1);
        Assert.True(rec.Browser("edge", 3, Ctx("mail.example.com", secure: true), queued: true));
        Assert.Equal(2, rec.RecordsToday);

        var rows = vault.Read(T0.AddDays(-1), T0.AddDays(1)).ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(RecordKind.BrowserContext, r.Kind));
        var first = Encoding.UTF8.GetString(rows[0].Body);
        Assert.Contains("\"host\":\"docs.example.org\"", first, StringComparison.Ordinal);
        Assert.Contains("\"field\":\"other\"", first, StringComparison.Ordinal);
        var second = Encoding.UTF8.GetString(rows[1].Body);
        Assert.Contains("\"field\":\"secure\"", second, StringComparison.Ordinal);
        Assert.Contains("\"queued\":true", second, StringComparison.Ordinal);

        // A new recorder over the same vault starts from today's count; tomorrow it rolls to zero.
        var again = new CaptureRecorder(vault, new CaptureBlocks(), () => _now);
        Assert.Equal(2, again.RecordsToday);
        _now = T0.AddDays(1);
        Assert.Equal(0, again.RecordsToday);
    }

    [Fact]
    public void Every_block_kind_stops_the_write()
    {
        var (blocks, vault, rec) = World();
        blocks.Add(new CaptureBlock { Id = "", Kind = BlockKind.Host, Value = "bank.example.com", AddedBy = "user" });
        blocks.Add(new CaptureBlock { Id = "", Kind = BlockKind.App, Value = "msedge.exe", AddedBy = "user" });
        blocks.Add(new CaptureBlock { Id = "", Kind = BlockKind.Tab, Value = BrowserEndpoint.TabKey("chrome", 9), AddedBy = "user" });

        Assert.False(rec.Browser("chrome", 1, Ctx("login.bank.example.com"))); // subdomain of an excluded site
        Assert.False(rec.Browser("edge", 1, Ctx("docs.example.org")));         // excluded browser app
        Assert.False(rec.Browser("chrome", 9, Ctx("docs.example.org")));       // paused tab
        Assert.True(rec.Browser("chrome", 2, Ctx("docs.example.org")));
        Assert.Equal(3, rec.Skipped);
        Assert.Equal(1, rec.RecordsToday);

        var pause = blocks.Add(new CaptureBlock { Id = "", Kind = BlockKind.Pause, Until = T0.AddMinutes(15), AddedBy = "user" });
        Assert.False(rec.Browser("chrome", 2, Ctx("docs.example.org")));
        _now = T0.AddMinutes(16); // the pause expired by itself
        Assert.True(rec.Browser("chrome", 2, Ctx("docs.example.org")));
        Assert.DoesNotContain(blocks.Blocks, b => b.Id == pause.Id);
        Assert.Equal(2, vault.Read(T0.AddDays(-1), T0.AddDays(1)).Count());
    }

    [Fact]
    public void Forget_removes_the_range_and_lowers_the_count()
    {
        var (_, vault, rec) = World();
        rec.Browser("chrome", 1, Ctx("a.example.org"));
        _now = T0.AddMinutes(30);
        rec.Browser("chrome", 1, Ctx("b.example.org"));
        _now = T0.AddMinutes(40);
        rec.Browser("chrome", 1, Ctx("c.example.org"));

        Assert.Equal(2, rec.ForgetSince(T0.AddMinutes(20)));
        Assert.Equal(1, rec.RecordsToday);
        var left = Assert.Single(vault.Read(T0.AddDays(-1), T0.AddDays(1)));
        Assert.Contains("a.example.org", Encoding.UTF8.GetString(left.Body), StringComparison.Ordinal);
    }

    [Fact]
    public void Endpoint_context_changes_reach_the_recorder_and_forget_flows_through_control()
    {
        var (blocks, _, rec) = World();
        var ep = new BrowserEndpoint(blocks, () => _now);
        ep.ContextChanged += (b, t, c) => rec.Browser(b, t, c);
        var ctl = new CaptureControl(blocks, () => _now) { ResolveTab = ep.ResolveTab, RecordsToday = () => rec.RecordsToday, ForgetSince = rec.ForgetSince };

        ep.Handle("s1", new BrowserHello { At = T0, Contract = 1, Client = "nmh", Browser = "chrome", ExtensionVersion = "1.0.0" });
        ep.Handle("s1", new BrowserTab { At = T0, Tab = 4, Window = 1, Host = "docs.example.org", Active = true, Audible = false });
        ep.Handle("s1", new BrowserFocus { At = T0, Tab = 4, Field = "text" });
        Assert.Equal(2, ctl.Status().RecordsToday);

        var status = ctl.Handle(new CaptureCommand { Verb = CaptureVerb.Forget, Since = T0.AddMinutes(-5), ByUser = true });
        Assert.Equal(0, status.RecordsToday);
    }
}
