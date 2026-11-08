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
    private readonly CaptureRecorder _recorder;
    private readonly InputRing _ring = new(1024);
    private readonly DeepCapture _deep;
    private long _ms;

    public DeepCapturePipelineTests()
    {
        Directory.CreateDirectory(_dir);
        _blocks = new CaptureBlocks(Path.Combine(_dir, "blocks.json"), TimeZoneInfo.Utc);
        _vault = new RawVault(Path.Combine(_dir, "raw"), new byte[32]);
        var recorder = _recorder = new CaptureRecorder(_vault, _blocks, Now);
        var runs = new KeyRunBuilder(new KeyDecoder(), new ActorAttributor(() => []), new SecretRedactor(), (new Suppressor(_blocks) { SecureDesktop = () => false }).Check, ms => T0.AddMilliseconds(ms));
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
    public void Pointer_events_flow_through_recorder_and_click_documents_with_blocks_and_secure_gate()
    {
        var asked = new List<(long Id, int X, int Y)>();
        _deep.ClickTargetRequested = (id, x, y) => asked.Add((id, x, y));
        _deep.Focus(new FocusTarget("w", "e", "synthetic.exe") { IsPassword = false });
        _ring.Write(new RawInput(0, 0, 0, 0, false, RawInputKind.Mouse, 1, 2, 0x0201));
        _deep.Pump();
        Assert.Single(Records(), r => r.Kind == RecordKind.Pointer); // the click is recorded before its target is known
        var (id, x, y) = Assert.Single(asked);
        Assert.Equal((1, 2), (x, y));
        _ms = 40;
        Assert.True(_deep.ClickTarget(id, "button: synthetic"));
        Assert.Equal(2, Records().Count(r => r.Kind == RecordKind.Pointer));
        _deep.CloseActivity();
        Assert.Contains(Records(), r => r.Kind == RecordKind.Document && r.Body.Contains("button: synthetic", StringComparison.Ordinal));
        var count = Records().Count;
        _recorder.SetSecure(true);
        _ring.Write(new RawInput(0, 0, 0, 200, false, RawInputKind.Mouse, 1, 2, 0x0201));
        _deep.Pump();
        _deep.CloseActivity();
        Assert.Equal(count, Records().Count);
        Assert.Single(asked); // nothing is asked about while the gate is closed
        _recorder.SetSecure(false);
        _blocks.Add(new CaptureBlock { Id = "", Kind = BlockKind.App, Value = "synthetic.exe", AddedBy = "user" });
        _ring.Write(new RawInput(0, 0, 0, 400, false, RawInputKind.Mouse, 1, 2, 0x0201));
        _deep.Pump();
        _deep.CloseActivity();
        Assert.Equal(count, Records().Count);
    }

    [Fact]
    public void A_click_target_counts_only_within_its_budget_and_while_the_gate_is_open()
    {
        var asked = new List<long>();
        _deep.ClickTargetRequested = (id, _, _) => asked.Add(id);
        _deep.Focus(new FocusTarget("w", "e", "synthetic.exe") { IsPassword = false });

        _ms = 1000;
        _ring.Write(new RawInput(0, 0, 0, 1000, false, RawInputKind.Mouse, 1, 2, 0x0201));
        _deep.Pump();
        _ms = 1000 + DeepCapture.ClickTargetBudgetMs + 1;
        Assert.False(_deep.ClickTarget(asked[^1], "button: late"));

        _ring.Write(new RawInput(0, 0, 0, _ms, false, RawInputKind.Mouse, 1, 2, 0x0201));
        _deep.Pump();
        _recorder.SetSecure(true);
        Assert.False(_deep.ClickTarget(asked[^1], "button: behind the gate"));
        _recorder.SetSecure(false);

        _ms += 500;
        _ring.Write(new RawInput(0, 0, 0, _ms, false, RawInputKind.Mouse, 1, 2, 0x0201));
        _deep.Pump();
        _ms += DeepCapture.ClickTargetBudgetMs + 50;
        _deep.Pump(); // the budget expires on the loop too: nothing waits forever
        Assert.Equal(0, _deep.PendingClicks);

        _ring.Write(new RawInput(0, 0, 0, _ms, false, RawInputKind.Mouse, 1, 2, 0x0201));
        _deep.Pump();
        Assert.True(_deep.ClickTarget(asked[^1], "card 4111 1111 1111 1111 button"));
        Assert.False(_deep.ClickTarget(asked[^1], "button: twice"));
        _deep.CloseActivity();
        var records = Records();
        Assert.DoesNotContain(records, r => r.Body.Contains("late", StringComparison.Ordinal) || r.Body.Contains("behind the gate", StringComparison.Ordinal) || r.Body.Contains("4111 1111", StringComparison.Ordinal));
        Assert.Contains(records, r => r.Kind == RecordKind.Document && r.Body.Contains("redacted:card", StringComparison.Ordinal));
    }

    [Fact]
    public void A_target_for_a_closed_activity_is_recorded_but_stays_out_of_the_next_document()
    {
        var asked = new List<long>();
        _deep.ClickTargetRequested = (id, _, _) => asked.Add(id);
        _deep.Focus(new FocusTarget("w", "e", "synthetic.exe") { IsPassword = false });
        _ring.Write(new RawInput(0, 0, 0, 0, false, RawInputKind.Mouse, 1, 2, 0x0201));
        _deep.Pump();
        _ms = 10;
        _deep.CloseActivity();
        _deep.Walk(Walk(9, "Next activity"), "synthetic.exe", null);
        _ms = 30;
        Assert.True(_deep.ClickTarget(asked[^1], "button: synthetic"));
        _deep.CloseActivity();
        var docs = Records().Where(r => r.Kind == RecordKind.Document).ToList();
        Assert.Equal(2, docs.Count);
        Assert.DoesNotContain(docs, d => d.Body.Contains("button: synthetic", StringComparison.Ordinal));
        Assert.Contains(Records(), r => r.Kind == RecordKind.Pointer && r.Body.Contains("button: synthetic", StringComparison.Ordinal));
    }

    [Fact]
    public void A_closed_document_carries_its_pointer_features()
    {
        _deep.Focus(new FocusTarget("w", "e", "synthetic.exe") { IsPassword = false });
        for (var i = 0; i < 1000; i++)
        {
            _ring.Write(new RawInput(0, 0, 0, i, false, RawInputKind.Mouse, i, 0, 0x0200)); // 1000 Hz input
        }

        _ms = 1000;
        _deep.Pump();
        Assert.Equal(10, Records().Count(r => r.Kind == RecordKind.Pointer)); // shaped to 10 samples a second
        _deep.CloseActivity();
        var doc = Assert.Single(Records(), r => r.Kind == RecordKind.Document);
        Assert.Contains("\"pointer\":{\"path_length_px\":900", doc.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Typing_flows_from_the_ring_into_the_vault_and_the_activity_document()
    {
        _deep.Focus(new FocusTarget("w1", "e1", "notepad.exe") { IsPassword = false });
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
        _deep.Focus(new FocusTarget("w2", "e1", "KeePassXC.exe") { IsPassword = false });
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
        _deep.Focus(new FocusTarget("w1", "subject", "mail.exe") { IsPassword = false }, value: "Re: ");
        Type("q"); // the user typed one key, then autocomplete filled the rest
        _deep.Focus(new FocusTarget("w1", "body", "mail.exe") { IsPassword = false }, previousValue: "Re: quarterly numbers"); // the key is still in the ring
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
        _deep.Focus(new FocusTarget("w1", "e1", "notepad.exe") { IsPassword = false });
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




