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

    [Fact]
    public void Native_source_events_are_queued_on_the_session_loop()
    {
        _session.Tick();
        _session.PostVisible(new VisibleSet(T0, 100, [new VisibleWindow(1, "synthetic.exe", 100, true)]));
        Assert.Null(_session.Deep.Tracks.Foreground);
        _session.Loop.RunPending();
        Assert.Equal("synthetic.exe", _session.Deep.Tracks.Foreground);

        _session.PostMedia(Sensitive.Of(new MediaState("synthetic.exe", null, null, MediaStatus.Playing, 0, false)));
        Assert.DoesNotContain(Records(), r => r.Kind == RecordKind.Media);
        _session.Loop.RunPending();
        Assert.Contains(Records(), r => r.Kind == RecordKind.Media);

        var walk = new UiaWalk(new ViewKey(1, 1), [new SnapLine(1, 0, 1, 0, -1)], ["synthetic text"]);
        _session.PostUiaWalk(Sensitive.Of(walk), "synthetic.exe", null);
        Assert.DoesNotContain(Records(), r => r.Kind == RecordKind.ViewDiff);
        _session.Loop.RunPending();
        Assert.Contains(Records(), r => r.Kind == RecordKind.ViewDiff && r.Body.Contains("synthetic text", StringComparison.Ordinal));
    }
    [Fact]
    public void Session_notice_during_a_session_query_cannot_reopen_the_gate_with_a_stale_result()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blocks = new CaptureBlocks(Path.Combine(_dir, "race-blocks.json"), TimeZoneInfo.Utc);
        var vault = new RawVault(Path.Combine(_dir, "race-raw"), new byte[32]);
        using var session = new CaptureSession(new CaptureRecorder(vault, blocks, () => _clock.UtcNow),
            new BrowserEndpoint(blocks, () => _clock.UtcNow), _clock, _probe, () =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
                return SessionAvailability.UnlockedConnected;
            });
        var tick = Task.Run(session.Tick);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        session.SessionChanged();
        release.Set();
        Assert.True(tick.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(session.SecureDesktop);
        session.Tick();
        Assert.False(session.SecureDesktop); // only a fresh query may reopen
    }
    [Fact]
    public void Indicator_notification_closes_the_session_gate_synchronously()
    {
        _session.Tick();
        Assert.False(_session.SecureDesktop);
        var indicator = new Tasktor.Capture.Indicator.Win32RecordingIndicator();
        indicator.SessionChanged += _session.SessionChanged;
        Assert.True(indicator.ForwardSessionNotice(default));
        Assert.True(_session.SecureDesktop);
    }
    [Fact]
    public void Media_metadata_and_audio_peaks_are_combined_on_the_session_loop()
    {
        _session.Tick();
        _session.PostMediaMetadata("synthetic.exe", null, null, MediaStatus.Playing, 100);
        _session.PostMediaPeak("synthetic.exe", T0, 0.5f);
        _session.PostMediaPeak("synthetic.exe", T0.AddSeconds(1), 0.5f);
        _session.PostMediaPeak("synthetic.exe", T0.AddSeconds(2), 0.5f);
        Assert.DoesNotContain(Records(), r => r.Kind == RecordKind.Media);
        _session.Loop.RunPending();
        Assert.Contains(Records(), r => r.Kind == RecordKind.Media && r.Body.Contains("\"audible\":true", StringComparison.Ordinal));
    }
    [Fact]
    public void A_queued_source_event_from_before_a_notice_is_discarded_after_reopening()
    {
        _session.Tick();
        _session.PostMedia(Sensitive.Of(new MediaState("synthetic.exe", "old synthetic title", null, MediaStatus.Playing, 0, false)));
        _session.SessionChanged();
        _session.Tick();
        _session.Loop.RunPending();
        Assert.DoesNotContain(Records(), r => r.Kind == RecordKind.Media);
    }
    [Fact]
    public void Stop_drains_source_events_accepted_before_shutdown()
    {
        _session.Tick();
        _session.PostMedia(Sensitive.Of(new MediaState("synthetic.exe", null, null, MediaStatus.Playing, 0, false)));
        Assert.True(_session.Stop());
        Assert.Contains(Records(), r => r.Kind == RecordKind.Media);
    }
    private void Browse()
    {
        _session.Tick(); // the first check proves the user's desktop and opens the write gate
        _endpoint.Handle("s1", new BrowserHello { At = T0, Contract = 1, Client = "nmh", Browser = "chrome", ExtensionVersion = "1.0.0" });
        _endpoint.Handle("s1", new BrowserTab { At = T0, Tab = 4, Window = 1, Host = "docs.example.com", Active = true, Audible = false, Url = "https://docs.example.com/guide", Title = "Guide" });
    }

    [Fact]
    public void Stop_reports_the_logic_thread_gone_so_the_indicator_may_go()
    {
        _session.Start();
        Assert.True(_session.Stop());
        Assert.True(_session.Loop.Completion.IsCompleted);
        Assert.True(_session.Stop()); // idempotent, same answer
    }

    [Fact]
    public void History_lines_appended_while_the_gate_is_closed_are_dropped_not_held()
    {
        var history = Path.Combine(_dir, "secure_history.txt");
        File.WriteAllText(history, string.Empty);
        var blocks = new CaptureBlocks(Path.Combine(_dir, "blocks3.json"), TimeZoneInfo.Utc);
        var vault = new RawVault(Path.Combine(_dir, "raw3"), new byte[32]);
        using var session = new CaptureSession(new CaptureRecorder(vault, blocks, () => _clock.UtcNow), new BrowserEndpoint(blocks, () => _clock.UtcNow), _clock, _probe, () => SessionAvailability.UnlockedConnected)
        {
            History = new Commands.HistoryTail(history),
        };

        _probe.Next = DesktopObservation.OtherDesktop; // a secure desktop: the gate stays closed
        session.Tick();
        File.AppendAllText(history, "while-locked-command\n");
        session.Tick();
        Assert.Equal(0, session.Deep.Commands.Pending);

        _probe.Next = DesktopObservation.UserDesktop;
        _clock.UtcNow = T0.AddSeconds(5);
        session.Tick();
        _clock.UtcNow = T0.AddSeconds(10);
        session.Tick();
        Assert.DoesNotContain(vault.Read(T0.AddDays(-1), T0.AddDays(1)), r => r.Kind == RecordKind.Command);
    }

    [Fact]
    public void New_shell_history_lines_become_command_records_once_their_second_has_passed()
    {
        var history = Path.Combine(_dir, "ConsoleHost_history.txt");
        File.WriteAllText(history, "from before capture\n");
        var blocks = new CaptureBlocks(Path.Combine(_dir, "blocks2.json"), TimeZoneInfo.Utc);
        var vault = new RawVault(Path.Combine(_dir, "raw2"), new byte[32]);
        using var session = new CaptureSession(new CaptureRecorder(vault, blocks, () => _clock.UtcNow), new BrowserEndpoint(blocks, () => _clock.UtcNow), _clock, _probe, () => SessionAvailability.UnlockedConnected)
        {
            History = new Commands.HistoryTail(history),
        };

        session.Tick();
        File.AppendAllText(history, "dotnet build\n");
        session.Tick();
        Assert.Equal(1, session.Deep.Commands.Pending);
        _clock.UtcNow = T0.AddSeconds(2);
        session.Tick();

        var bodies = vault.Read(T0.AddDays(-1), T0.AddDays(1)).Where(r => r.Kind == RecordKind.Command).Select(r => Encoding.UTF8.GetString(r.Body)).ToList();
        var only = Assert.Single(bodies);
        Assert.Contains("dotnet build", only, StringComparison.Ordinal);
        Assert.DoesNotContain(bodies, b => b.Contains("from before capture", StringComparison.Ordinal));
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

    [Fact]
    public void A_persons_search_goes_through_the_control_and_finds_the_activity()
    {
        Browse();
        _endpoint.Handle("s1", new BrowserText { At = T0, Tab = 4, Lines = ["Quarterly revenue review"] });
        _session.Loop.RunPending();
        _session.Stop();
        var control = new CaptureControl(new CaptureBlocks(Path.Combine(_dir, "b2.json"), TimeZoneInfo.Utc), () => T0) { Search = _session.Search };
        var found = control.Handle(new CaptureCommand { Verb = CaptureVerb.Search, ByUser = true, Query = "revenue" });
        Assert.Null(found.Refusal);
        Assert.Contains("[revenue]", Assert.Single(found.Hits!).Snippet, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("invalid", control.Handle(new CaptureCommand { Verb = CaptureVerb.Search, ByUser = false, Query = "revenue" }).Refusal); // never a rule
        Assert.Equal("no_index", new CaptureControl(new CaptureBlocks(Path.Combine(_dir, "b3.json"), TimeZoneInfo.Utc), () => T0).Handle(new CaptureCommand { Verb = CaptureVerb.Search, ByUser = true, Query = "x" }).Refusal);
    }

    [Fact]
    public void Nothing_is_written_before_the_first_check_or_after_a_session_notice()
    {
        _endpoint.Handle("s1", new BrowserHello { At = T0, Contract = 1, Client = "nmh", Browser = "chrome", ExtensionVersion = "1.0.0" });
        _endpoint.Handle("s1", new BrowserTab { At = T0, Tab = 4, Window = 1, Host = "docs.example.com", Active = true, Audible = false });
        _endpoint.Handle("s1", new BrowserText { At = T0, Tab = 4, Lines = ["before any check"] });
        _session.Loop.RunPending();
        Assert.Empty(Records()); // the gate starts closed

        _session.Tick();
        _session.SessionChanged(); // e.g. the PC locks: closed on the notice's own thread, before the loop hears of it
        _endpoint.Handle("s1", new BrowserText { At = T0, Tab = 4, Lines = ["while locked"] });
        _session.Loop.RunPending();
        Assert.DoesNotContain(Records(), r => r.Body.Contains("while locked", StringComparison.Ordinal));

        _session.Tick(); // the next check proves the user's desktop again
        _endpoint.Handle("s1", new BrowserText { At = T0, Tab = 4, Lines = ["while locked", "after unlock"] });
        _session.Loop.RunPending();
        Assert.Contains(Records(), r => r.Body.Contains("after unlock", StringComparison.Ordinal));
    }
}

