using System.Text;
using Tasktor.Capture.Deep;
using Tasktor.Capture.Recording;

namespace Tasktor.Capture.Tests;

/// <summary>C §6: ring → runs → recorder → documents, view diffs redacted, blocks honoured end to end, side-track documents.</summary>
public sealed class DeepCapturePipelineTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tasktor-deep-" + Guid.NewGuid().ToString("N"));
    private readonly CaptureBlocks _blocks;
    private readonly RawVault _vault;
    private readonly InputRing _ring = new(1024);
    private readonly DeepCapture _deep;
    private long _ms;

    public DeepCapturePipelineTests()
    {
        Directory.CreateDirectory(_dir);
        _blocks = new CaptureBlocks(Path.Combine(_dir, "blocks.json"), TimeZoneInfo.Utc);
        _vault = new RawVault(Path.Combine(_dir, "raw"), new byte[32]);
        var recorder = new CaptureRecorder(_vault, _blocks, Now);
        var runs = new KeyRunBuilder(new KeyDecoder(), new ActorAttributor(() => []), new SecretRedactor(), new Suppressor(_blocks).Check);
        _deep = new DeepCapture(_ring, runs, new SnapshotDiffer(), new SecretRedactor(), recorder, Now, () => _ms);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private DateTimeOffset Now() => T0.AddMilliseconds(_ms);

    private void Type(string text)
    {
        foreach (var k in Keys.Type(text, _ms))
        {
            _ring.Write(k);
            _ms = Math.Max(_ms, k.TimeMs);
        }
    }

    private void Wait(TimeSpan by, TimeSpan? step = null)
    {
        var end = _ms + (long)by.TotalMilliseconds;
        var s = (long)(step ?? by).TotalMilliseconds;
        while (_ms < end)
        {
            _ms = Math.Min(end, _ms + s);
            _deep.Pump();
        }
    }

    private List<(byte Kind, string Body)> Records() =>
        [.. _vault.Read(T0.AddDays(-1), T0.AddDays(1)).Select(r => (r.Kind, Encoding.UTF8.GetString(r.Body)))];

    private static UiaWalk Walk(long window, params string[] names) =>
        new(new ViewKey(window, 1), [.. names.Select((n, i) => new SnapLine((ulong)(i + 1), 1, 2, i, -1))], names);

    [Fact]
    public void Typing_flows_from_the_ring_into_the_vault_and_the_activity_document()
    {
        _deep.Focus(new FocusTarget("w1", "e1", "notepad.exe"));
        Type("meeting notes");
        Wait(TimeSpan.FromSeconds(4), TimeSpan.FromMilliseconds(500)); // the 3 s pause closes the run
        var run = Assert.Single(Records(), r => r.Kind == RecordKind.KeyRun);
        Assert.Contains("\"text\":\"meeting notes\"", run.Body, StringComparison.Ordinal);

        _deep.CloseActivity();
        var doc = Assert.Single(Records(), r => r.Kind == RecordKind.Document);
        Assert.Contains("meeting notes", doc.Body, StringComparison.Ordinal);
        Assert.Equal(1, _deep.DocumentsWritten);
        Assert.Equal(0, _deep.Dropped);
    }

    [Fact]
    public void A_blocked_app_reaches_neither_the_vault_nor_a_document()
    {
        _blocks.Add(new CaptureBlock { Id = "", Kind = BlockKind.App, Value = "keepass*.exe", AddedBy = "user" });
        _deep.Focus(new FocusTarget("w2", "e1", "KeePassXC.exe"));
        Type("hunter2");
        Wait(TimeSpan.FromSeconds(4), TimeSpan.FromMilliseconds(500));
        _deep.Walk(Walk(2, "Vault entries", "bank login"), "KeePassXC.exe", null);
        _deep.CloseActivity();
        Assert.DoesNotContain(Records(), r => r.Body.Contains("hunter2", StringComparison.Ordinal) || r.Body.Contains("bank login", StringComparison.Ordinal));
    }

    [Fact]
    public void View_lines_are_redacted_before_the_vault_and_the_document()
    {
        _deep.Foreground("mail.exe");
        _deep.Walk(Walk(3, "Inbox", "card 4111 1111 1111 1111 on file"), "mail.exe", null);
        _deep.CloseActivity();
        var records = Records();
        Assert.DoesNotContain(records, r => r.Body.Contains("4111 1111", StringComparison.Ordinal));
        Assert.Contains(records, r => r.Kind == RecordKind.ViewDiff && r.Body.Contains("redacted:card", StringComparison.Ordinal));
        Assert.Contains(records, r => r.Kind == RecordKind.Document && r.Body.Contains("Inbox", StringComparison.Ordinal));
    }

    [Fact]
    public void The_field_value_diff_is_the_stored_text_not_the_keys()
    {
        _deep.Focus(new FocusTarget("w1", "subject", "mail.exe"), value: "Re: ");
        Type("q"); // the user typed one key, then autocomplete filled the rest
        _deep.Focus(new FocusTarget("w1", "body", "mail.exe"), previousValue: "Re: quarterly numbers"); // the key is still in the ring
        var run = Assert.Single(Records(), r => r.Kind == RecordKind.KeyRun);
        Assert.Contains("\"text\":\"quarterly numbers\"", run.Body, StringComparison.Ordinal);
        Assert.Contains("\"element_ref\":\"subject\"", run.Body, StringComparison.Ordinal); // attributed to the old field
        Wait(TimeSpan.FromSeconds(4), TimeSpan.FromMilliseconds(500));
        Assert.Single(Records(), r => r.Kind == RecordKind.KeyRun); // nothing leaked into the new field
    }

    [Fact]
    public void Youtube_behind_chess_gets_its_own_document_when_its_side_track_closes()
    {
        _deep.Foreground("chess.exe");
        _deep.Media(new MediaState("chrome.exe", "Endgame lecture", "A channel", MediaStatus.Playing, 0, true));
        Wait(TimeSpan.FromSeconds(61), TimeSpan.FromSeconds(1));
        Assert.Equal("chrome.exe", _deep.Tracks.SideTracks[2]);
        _deep.Media(new MediaState("chrome.exe", "Next lecture", "A channel", MediaStatus.Playing, 0, true));
        _deep.Media(new MediaState("chrome.exe", "Next lecture", "A channel", MediaStatus.Paused, 0, false));
        Wait(TimeSpan.FromSeconds(31), TimeSpan.FromSeconds(1));
        Assert.Empty(_deep.Tracks.SideTracks);
        var doc = Assert.Single(Records(), r => r.Kind == RecordKind.Document);
        Assert.Contains("\"track\":2", doc.Body, StringComparison.Ordinal);
        Assert.Contains("Next lecture", doc.Body, StringComparison.Ordinal);
        Assert.Equal(3, Records().Count(r => r.Kind == RecordKind.Media));
    }

    [Fact]
    public void An_excluded_apps_volatile_values_are_not_recorded_under_the_focused_app_at_activity_close()
    {
        _blocks.Add(new CaptureBlock { Id = "", Kind = BlockKind.App, Value = "bank*.exe", AddedBy = "user" });
        _deep.Focus(new FocusTarget("w1", "e1", "notepad.exe"));
        // A ticking balance in a 10-line view of the excluded app: small diffs (not re-baselines), so it turns volatile.
        string[] Screen(int s) => [.. Enumerable.Range(1, 9).Select(i => "Accounts row " + i), "balance 10" + s.ToString("00", System.Globalization.CultureInfo.InvariantCulture)];
        for (var s = 0; s <= 12; s++)
        {
            _deep.Walk(Walk(9, Screen(s)), "bankclient.exe", null);
            _ms += 1000;
        }

        _deep.CloseActivity();
        Assert.DoesNotContain(Records(), r => r.Body.Contains("balance", StringComparison.Ordinal) || r.Body.Contains("Accounts", StringComparison.Ordinal));
    }
}
