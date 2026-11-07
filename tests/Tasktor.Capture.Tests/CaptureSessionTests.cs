using System.Text;
using Tasktor.Capture.Browser;
using Tasktor.Capture.Deep;
using Tasktor.Capture.Host;
using Tasktor.Capture.Index;
using Tasktor.Core.Storage;
using Tasktor.Capture.Recording;
using Tasktor.Capture.Security;
using Tasktor.Contracts.Capture;
using Tasktor.Contracts.Platform;

namespace Tasktor.Capture.Tests;

/// <summary>Step 6: the capture host's logic loop joins the browser events, DeepCapture and the secure-desktop check.</summary>
public sealed class CaptureSessionTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tasktor-session-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly Clock _clock = new();
    private readonly RawVault _vault;
    private readonly BrowserEndpoint _endpoint;
    private readonly CaptureSession _session;
    private readonly Probe _probe = new();
    private readonly ContentIndex _index;

    public CaptureSessionTests()
    {
        Directory.CreateDirectory(_dir);
        var blocks = new CaptureBlocks(Path.Combine(_dir, "blocks.json"), TimeZoneInfo.Utc);
        _vault = new RawVault(Path.Combine(_dir, "raw"), new byte[32]);
        var recorder = new CaptureRecorder(_vault, blocks, () => _clock.UtcNow);
        _endpoint = new BrowserEndpoint(blocks, () => _clock.UtcNow);
        _index = ContentIndex.Open(SqliteDb.Open(":memory:"));
        _session = new CaptureSession(recorder, _endpoint, _clock, _probe, () => SessionAvailability.UnlockedConnected) { Index = _index, Vault = _vault };
    }

    public void Dispose()
    {
        _session.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = T0;
    }

    private sealed class Probe : IDesktopProbe
    {
        public DesktopObservation Next { get; set; } = DesktopObservation.UserDesktop;

        public bool Throws { get; set; }

        public DesktopObservation Read() => Throws ? throw new InvalidOperationException("probe failed") : Next;
    }

    private List<(byte Kind, string Body)> Records() =>
        [.. _vault.Read(T0.AddDays(-1), T0.AddDays(1)).Select(r => (r.Kind, Encoding.UTF8.GetString(r.Body)))];

    private void Browse()
    {
        _endpoint.Handle("s1", new BrowserHello { At = T0, Contract = 1, Client = "nmh", Browser = "chrome", ExtensionVersion = "1.0.0" });
        _endpoint.Handle("s1", new BrowserTab { At = T0, Tab = 4, Window = 1, Host = "docs.example.com", Active = true, Audible = false, Url = "https://docs.example.com/guide", Title = "Guide" });
    }

    [Fact]
    public void Page_text_from_the_extension_is_diffed_and_recorded_on_the_logic_loop()
    {
        Browse();
        _endpoint.Handle("s1", new BrowserText { At = T0, Tab = 4, Lines = ["Guide", "Step one"] });
        Assert.DoesNotContain(Records(), r => r.Kind == RecordKind.ViewDiff); // posted, not yet run: sources only enqueue
        _session.Loop.RunPending();
        var diff = Assert.Single(Records(), r => r.Kind == RecordKind.ViewDiff);
        Assert.Contains("Step one", diff.Body, StringComparison.Ordinal);

        _endpoint.Handle("s1", new BrowserText { At = T0, Tab = 4, Lines = ["Guide", "Step one", "Step two"] });
        _session.Loop.RunPending();
        var second = Records().Where(r => r.Kind == RecordKind.ViewDiff).Last();
        Assert.Contains("Step two", second.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Step one", second.Body, StringComparison.Ordinal); // only the change
    }

    [Fact]
    public void Video_state_becomes_a_media_record()
    {
        Browse();
        _endpoint.Handle("s1", new BrowserMedia { At = T0, Tab = 4, Playing = true, PositionS = 12.5 });
        _session.Loop.RunPending();
        var media = Assert.Single(Records(), r => r.Kind == RecordKind.Media);
        Assert.Contains("\"position_ms\":12500", media.Body, StringComparison.Ordinal);
        Assert.Contains("chrome.exe", media.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_secure_desktop_check_fails_closed_until_the_probe_proves_the_user_desktop()
    {
        Assert.True(_session.SecureDesktop); // before the first check: suppressed
        _session.Tick();
        Assert.False(_session.SecureDesktop);
        _probe.Next = DesktopObservation.OtherDesktop; // UAC or the lock screen
        _session.Tick();
        Assert.True(_session.SecureDesktop);
        _probe.Next = DesktopObservation.UserDesktop;
        _probe.Throws = true;
        _session.Tick();
        Assert.True(_session.SecureDesktop);
    }

    [Fact]
    public void Stopping_closes_the_open_activity_and_detaches_the_browser()
    {
        Browse();
        _endpoint.Handle("s1", new BrowserText { At = T0, Tab = 4, Lines = ["Guide", "Step one"] });
        _session.Loop.RunPending();
        _session.Stop();
        Assert.Contains(Records(), r => r.Kind == RecordKind.Document); // the activity's document is written before the indicator goes
        var before = Records().Count;
        _endpoint.Handle("s1", new BrowserText { At = T0, Tab = 4, Lines = ["late"] });
        _session.Loop.RunPending();
        Assert.Equal(before, Records().Count);
    }

    [Fact]
    public void Closed_activities_are_indexed_and_forget_clears_the_index_too()
    {
        Browse();
        _endpoint.Handle("s1", new BrowserText { At = T0, Tab = 4, Lines = ["Quarterly revenue review"] });
        _session.Loop.RunPending();
        _session.Stop(); // closes the activity and runs a last ingest
        Assert.Single(_index.Search("revenue"));
        Assert.Equal(1, _session.ForgetIndex(T0.AddHours(-1)));
        Assert.Empty(_index.Search("revenue"));
    }
}
