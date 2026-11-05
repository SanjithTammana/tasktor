using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using Tasktor.Capture.Deep;
using Tasktor.Tests.Shared;

namespace Tasktor.Capture.Tests;

/// <summary>Synthetic key streams: what a hook would record for scripted typing (no real text ever).</summary>
internal static class Keys
{
    public static IEnumerable<RawInput> Type(string text, long start, int stepMs = 120, uint flags = 0)
    {
        var t = start;
        foreach (var ch in text)
        {
            var (vk, shift) = Vk(ch);
            if (shift)
            {
                yield return new RawInput(KeyDecoder.VkLShift, 0, flags, t, false);
            }

            yield return new RawInput(vk, 0, flags, t, false);
            yield return new RawInput(vk, 0, flags, t + 30, true);
            if (shift)
            {
                yield return new RawInput(KeyDecoder.VkLShift, 0, flags, t + 40, true);
            }

            t += stepMs;
        }
    }

    public static RawInput Down(ushort vk, long t, uint flags = 0) => new(vk, 0, flags, t, false);

    public static RawInput Up(ushort vk, long t, uint flags = 0) => new(vk, 0, flags, t, true);

    private static (ushort, bool) Vk(char c) => c switch
    {
        >= 'a' and <= 'z' => ((ushort)(0x41 + (c - 'a')), false),
        >= 'A' and <= 'Z' => ((ushort)(0x41 + (c - 'A')), true),
        >= '0' and <= '9' => (c, false),
        ' ' => (0x20, false),
        '.' => (0xBE, false),
        '-' => (0xBD, false),
        '_' => (0xBD, true),
        '!' => (0x31, true),
        '@' => (0x32, true),
        _ => throw new ArgumentException("not in the synthetic keyboard: " + c),
    };
}

public sealed class KeyDecodingTests
{
    [Fact]
    public void Letters_shift_caps_digits_and_punctuation_decode_on_the_us_layout()
    {
        var d = new KeyDecoder();
        var text = new StringBuilder();
        foreach (var k in Keys.Type("Hello World 42!", 0))
        {
            if (d.Decode(k) is { Action: KeyAction.Text, Text: { } s })
            {
                text.Append(s);
            }
        }

        Assert.Equal("Hello World 42!", text.ToString());
        d.Decode(Keys.Down(KeyDecoder.VkCapital, 0));
        d.Decode(Keys.Up(KeyDecoder.VkCapital, 1));
        Assert.Equal("A", d.Decode(Keys.Down(0x41, 2)).Text);
    }

    [Fact]
    public void Ctrl_alt_and_win_make_chords_never_text_and_altgr_goes_through_the_layout()
    {
        var d = new KeyDecoder { Layout = (vk, _, _, _, altGr) => altGr && vk == 0x45 ? "€" : null };
        d.Decode(Keys.Down(KeyDecoder.VkLControl, 0));
        var chord = d.Decode(Keys.Down(0x53, 1));
        Assert.Equal((KeyAction.Chord, "Ctrl+S"), (chord.Action, chord.Text));
        d.Decode(Keys.Down(KeyDecoder.VkLMenu, 2));
        Assert.Equal("€", d.Decode(Keys.Down(0x45, 3)).Text); // Ctrl+Alt = AltGr
        d.Decode(Keys.Up(KeyDecoder.VkLMenu, 4));
        d.Decode(Keys.Down(KeyDecoder.VkLShift, 5));
        Assert.Equal("Ctrl+Shift+F5", d.Decode(Keys.Down(0x74, 6)).Text);
    }

    [Fact]
    public void Injected_flags_survive_decoding()
    {
        var k = new KeyDecoder().Decode(new RawInput(0x41, 0, RawInput.Injected | RawInput.LowerIlInjected, 0, false));
        Assert.True(k.Injected);
        Assert.True(k.LowerIlInjected);
    }
}

public sealed class KeyRunTests
{
    private readonly CaptureBlocks _blocks = new(local: TimeZoneInfo.Utc);
    private readonly KeyRunBuilder _runs;
    private string? _activeAgent;

    public KeyRunTests()
    {
        var actors = new ActorAttributor(() => ["claude.exe", "codex.exe"]) { ActiveAgent = () => _activeAgent };
        _runs = new KeyRunBuilder(new KeyDecoder(), actors, new SecretRedactor(), new Suppressor(_blocks).Check);
    }

    private static FocusTarget Field(string app = "notepad.exe", string view = "w1", string el = "e1") => new(view, el, app);

    private void Feed(IEnumerable<RawInput> keys)
    {
        foreach (var k in keys)
        {
            _runs.Key(k);
        }
    }

    [Fact]
    public void A_run_ends_at_a_3_second_pause_and_backspace_edits_the_text()
    {
        _runs.Focus(Field(), 0);
        Feed(Keys.Type("helo", 0));
        Feed([Keys.Down(KeyDecoder.VkBack, 600), Keys.Up(KeyDecoder.VkBack, 630)]);
        Feed(Keys.Type("lo world", 700));
        _runs.Tick(700 + (8 * 120) + 3_100);
        var run = Assert.Single(_runs.Closed);
        Assert.Equal("hello world", run.Text);
        Assert.Equal(1, run.Backspaces);
        Assert.Equal(RunEnd.Pause, run.End);
        Assert.Equal(13, run.Keys);
        Assert.Equal(ActorKind.Human, run.Actor.Kind);
    }

    [Fact]
    public void Focus_change_and_enter_close_runs_and_enter_is_remembered_for_cli_attribution()
    {
        _runs.Focus(Field(view: "term", el: "console"), 0);
        Feed(Keys.Type("cargo test", 0));
        Feed([Keys.Down(KeyDecoder.VkReturn, 1300)]);
        _runs.Focus(Field(), 1400);
        Feed(Keys.Type("notes", 1500));
        _runs.Focus(Field(el: "e2"), 2400);
        Assert.Equal([RunEnd.Enter, RunEnd.FocusOut], _runs.Closed.Select(r => r.End));
        Assert.Equal(1300, _runs.LastEnter["term"]);
        Assert.Equal("cargo test", _runs.Closed[0].Text);
    }

    [Fact]
    public void Password_fields_secure_fields_own_windows_and_blocked_apps_keep_only_length_and_duration()
    {
        foreach (var (target, why) in new[]
        {
            (Field() with { IsPassword = true }, Suppression.Password),
            (Field() with { SecureField = true }, Suppression.SecureField),
            (Field("tasktor-flyout.exe"), Suppression.OwnWindow),
            (Field("keepass.exe"), Suppression.Excluded),
        })
        {
            if (why == Suppression.Excluded)
            {
                _blocks.Add(new CaptureBlock { Id = "", Kind = BlockKind.App, Value = "keepass*.exe", AddedBy = "user" });
            }

            _runs.Focus(target, 0);
            Feed(Keys.Type("FixtureTypedSecret", 0));
            var run = _runs.Close(RunEnd.Flush, 5000)!;
            Assert.Null(run.Text);
            Assert.Empty(run.Chords);
            Assert.Equal(why, run.Suppressed);
            Assert.True(run.Keys > 0);
        }
    }

    [Fact]
    public void A_focus_change_into_a_password_field_mid_run_suppresses_the_whole_run()
    {
        _runs.Focus(Field(), 0);
        Feed(Keys.Type("user", 0));
        _runs.Focus(Field() with { IsPassword = true }, 500); // same element turns into a password field
        Feed(Keys.Type("hunter", 600));
        Assert.Null(_runs.Close(RunEnd.Flush, 2000)!.Text);
    }

    [Fact]
    public void The_field_value_diff_is_the_truth_and_text_is_redacted_before_it_leaves()
    {
        _runs.Focus(Field(), 0);
        Feed(Keys.Type("pay ", 0));
        var card = "4111" + " 1111" + " 1111" + " 1111"; // Luhn-valid test number, built at run time
        var run = _runs.Close(RunEnd.FocusOut, 2000, "Note: ", "Note: pay " + card + " today")!;
        Assert.Equal("pay " + SecretRedactor.Mark("card") + " today", run.Text);
        Assert.Equal(1, run.Redactions["card"]);
    }

    [Fact]
    public void Injected_keys_while_an_agent_is_busy_are_the_agent_and_otherwise_injected_never_a_name()
    {
        _activeAgent = "claude.exe";
        _runs.Focus(Field(), 0);
        Feed(Keys.Type("refactor", 0, flags: RawInput.Injected));
        var run = _runs.Close(RunEnd.Flush, 2000)!;
        Assert.Equal("agent:claude.exe", run.Actor.ToString());
        Assert.True(run.Actor.Evidence.HasFlag(ActorEvidence.InjectedFlag));
        _activeAgent = null;
        _runs.Focus(Field(), 3000);
        Feed(Keys.Type("abc", 3000, flags: RawInput.Injected));
        Assert.Equal("injected", _runs.Close(RunEnd.Flush, 5000)!.Actor.ToString());
    }
}

public sealed class SecretRedactorTests
{
    private readonly SecretRedactor _r = new(["Project-[A-Z]{4}"]);

    // Every secret is assembled at run time from harmless pieces, so the repository holds no token-shaped literal.
    public static TheoryData<string, string> Secrets() => new()
    {
        { "card " + "5555" + "5555" + "5555" + "4444", "card" },
        { "IBAN GB82" + "WEST" + "1234" + "5698" + "7654" + "32", "iban" },
        { "ssn 123-" + "45-6789", "ssn" },
        { "key " + "gh" + "p_" + new string('Q', 20) + "abcdefghij1234567890", "api_key" },
        { "aws " + "AK" + "IA" + "QWERTYUIOPASDFGH", "api_key" },
        { "jwt " + "ey" + "JhbGciOiJIUzI1NiJ9" + "." + "eyJzdWIiOiIxMjM0NTY3ODkwIn0" + "." + "SflKxwRJ" + "SMeKKF2Q" + "T4fwpMeJ" + "f36POk6y", "jwt" },
        { "-----BEGIN " + "PRIVATE KEY-----\nMIIBVQIBADANBgkqhkiG9w0BAQEFAASCAT8wggE7\n-----END " + "PRIVATE KEY-----", "private_key" },
        { "token " + "Zk3pQ9xL" + "2vB7nM4c" + "T8rY1wE6" + "uI0oA5sD" + "Fg", "token" },
        { "codename Project-" + "ABCD", "user_rule" },
    };

    [Theory]
    [MemberData(nameof(Secrets))]
    public void Each_detector_blanks_its_kind(string text, string kind)
    {
        var (scrubbed, hits) = _r.Scrub(text);
        Assert.Contains(SecretRedactor.Mark(kind), scrubbed, StringComparison.Ordinal);
        Assert.Equal(1, hits[kind]);
        Assert.Equal(scrubbed, _r.Scrub(scrubbed).Text); // idempotent
    }

    [Theory]
    [InlineData("card 4111 1111 1111 1112")] // fails Luhn
    [InlineData("ssn 000-12-3456 and 666-12-3456")]
    [InlineData("the build took 12345 ms on 2026-11-05 at 14:02")]
    [InlineData("internationalization_localization_globalization_words")]
    [InlineData("see https://example.com/docs/getting-started/installation-guide")]
    public void Look_alikes_are_left_alone(string text) => Assert.Equal(text, _r.Scrub(text).Text);

    [Fact]
    public void Prose_has_almost_no_false_positives()
    {
        var words = "the quick brown fox jumps over a lazy dog while reading notes about linear algebra and parser design for the engine".Split(' ');
        var rng = new Random(3);
        var hits = 0;
        for (var i = 0; i < 500; i++)
        {
            var line = string.Join(' ', Enumerable.Range(0, 12).Select(_ => words[rng.Next(words.Length)])) + " " + rng.Next(0, 999).ToString(System.Globalization.CultureInfo.InvariantCulture);
            hits += _r.Scrub(line).Hits.Count;
        }

        Assert.True(hits <= 5, hits + " false positives in 500 lines");
    }
}

public sealed class RawVaultTests : IDisposable
{
    private static readonly byte[] DataKey = [.. Enumerable.Range(10, 32).Select(i => (byte)i)];
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);
    private readonly TempStore _store = new();

    public void Dispose() => _store.Dispose();

    private string Dir => Path.Combine(_store.Root, "capture", "raw");

    [Fact]
    public void Records_round_trip_and_the_files_hold_no_plaintext()
    {
        var v = new RawVault(Dir, DataKey);
        v.Append(T0, 1, Encoding.UTF8.GetBytes("FixtureRecordOne"));
        v.Append(T0.AddMinutes(1), 2, Encoding.UTF8.GetBytes("FixtureRecordTwo"));
        var back = new RawVault(Dir, DataKey).Read(T0.AddHours(-1), T0.AddHours(1)).ToList();
        Assert.Equal(["FixtureRecordOne", "FixtureRecordTwo"], back.Select(r => Encoding.UTF8.GetString(r.Body)));
        Assert.Equal([1, 2], back.Select(r => (int)r.Kind));
        foreach (var f in Directory.GetFiles(Dir))
        {
            Assert.DoesNotContain("Fixture", Encoding.UTF8.GetString(File.ReadAllBytes(f)), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_tampered_record_fails_and_a_truncated_tail_is_ignored()
    {
        var v = new RawVault(Dir, DataKey);
        v.Append(T0, 1, Encoding.UTF8.GetBytes("aaaa"));
        v.Append(T0.AddSeconds(1), 1, Encoding.UTF8.GetBytes("bbbb"));
        var path = Directory.GetFiles(Dir, "*.vault").Single();
        var bytes = File.ReadAllBytes(path);
        bytes[21] ^= 0xFF; // first record's ciphertext
        File.WriteAllBytes(path, [.. bytes, 1, 2, 3]); // plus a torn append
        var reader = new RawVault(Dir, DataKey);
        var back = reader.Read(T0.AddHours(-1), T0.AddHours(1)).ToList();
        Assert.Equal(["bbbb"], back.Select(r => Encoding.UTF8.GetString(r.Body)));
        Assert.Equal(1, reader.Tampered);
        Assert.True(reader.Truncated >= 1);
    }

    [Fact]
    public void A_wrong_data_key_reads_nothing()
    {
        new RawVault(Dir, DataKey).Append(T0, 1, [1, 2, 3]);
        var other = new RawVault(Dir, [.. Enumerable.Repeat((byte)7, 32)]);
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => other.Read(T0.AddHours(-1), T0.AddHours(1)).ToList());
    }

    [Fact]
    public void Retention_drops_days_older_than_7_key_first_and_forget_removes_a_range_with_fresh_nonces()
    {
        var v = new RawVault(Dir, DataKey);
        v.Append(T0.AddDays(-9), 1, [9]);
        v.Append(T0, 1, [1]);
        v.Append(T0.AddMinutes(30), 1, [2]);
        v.Append(T0.AddMinutes(90), 1, [3]);
        Assert.Equal(1, v.Purge(T0));
        Assert.DoesNotContain(Directory.GetFiles(Dir), f => f.Contains("2026-10-27", StringComparison.Ordinal));

        Assert.Equal(1, v.Forget(T0.AddMinutes(20), T0.AddMinutes(60)));
        var left = new RawVault(Dir, DataKey).Read(T0.AddHours(-1), T0.AddHours(3)).Select(r => r.Body[0]).ToList();
        Assert.Equal([1, 3], left.Select(b => (int)b));

        // The rewrite used new sequence numbers: appending again never collides with an old nonce.
        var again = new RawVault(Dir, DataKey);
        again.Append(T0.AddMinutes(100), 1, [4]);
        Assert.Equal(3, new RawVault(Dir, DataKey).Read(T0.AddHours(-1), T0.AddHours(3)).Count());
    }

    [Fact]
    public void Forget_all_removes_keys_and_files()
    {
        var v = new RawVault(Dir, DataKey);
        v.Append(T0, 1, [1]);
        v.ForgetAll();
        Assert.Empty(Directory.GetFiles(Dir));
    }
}

public sealed class CaptureBlockTests : IDisposable
{
    private readonly TempStore _store = new();

    public void Dispose() => _store.Dispose();

    private static readonly DateTimeOffset Wed10 = new(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Pauses_schedules_apps_hosts_and_tabs_block_and_persist()
    {
        var path = Path.Combine(_store.Root, "state", "capture-blocks.json");
        var b = new CaptureBlocks(path, TimeZoneInfo.Utc);
        b.Add(new CaptureBlock { Id = "", Kind = BlockKind.Pause, Until = Wed10.AddMinutes(15), AddedBy = "user" });
        Assert.True(b.Paused(Wed10));
        Assert.False(b.Paused(Wed10.AddMinutes(16)));
        b.Expire(Wed10.AddMinutes(16));
        b.Add(new CaptureBlock { Id = "", Kind = BlockKind.Schedule, Days = "Mon-Fri", From = "09:00", To = "17:00", AddedBy = "user" });
        b.Add(new CaptureBlock { Id = "", Kind = BlockKind.Schedule, Days = "Sat", From = "22:00", To = "02:00", AddedBy = "user" });
        Assert.True(b.Paused(Wed10));
        Assert.False(b.Paused(Wed10.AddHours(8)));
        Assert.True(b.Paused(new DateTimeOffset(2026, 11, 8, 1, 0, 0, TimeSpan.Zero))); // Sunday 01:00, from Saturday's range
        b.Add(new CaptureBlock { Id = "", Kind = BlockKind.Host, Value = "bank.example.com", AddedBy = "user" });
        b.Add(new CaptureBlock { Id = "", Kind = BlockKind.Tab, Value = "tab-7", AddedBy = "user" });
        var evening = Wed10.AddHours(9);
        Assert.Equal(BlockKind.Host, b.Blocking(evening, "chrome.exe", "www.bank.example.com", null));
        Assert.Equal(BlockKind.Tab, b.Blocking(evening, "chrome.exe", "news.example.org", "tab-7"));
        b.TabClosed("tab-7");
        Assert.Null(b.Blocking(evening, "chrome.exe", "news.example.org", "tab-7"));
        Assert.Equal(3, new CaptureBlocks(path, TimeZoneInfo.Utc).Blocks.Count);
    }

    [Fact]
    public void Rules_may_add_blocks_but_only_the_user_lifts_them()
    {
        var b = new CaptureBlocks(local: TimeZoneInfo.Utc);
        var block = b.Add(new CaptureBlock { Id = "", Kind = BlockKind.Pause, AddedBy = "rule:call-quiet" });
        Assert.False(b.Remove(block.Id, byUser: false));
        Assert.True(b.Paused(Wed10));
        Assert.True(b.Remove(block.Id, byUser: true));
        Assert.False(b.Paused(Wed10));
    }
}

public sealed class ActorTests
{
    private readonly ActorAttributor _a = new(() => ["claude.exe", "aider"]);
    private static readonly DateTimeOffset T = DateTimeOffset.UnixEpoch.AddDays(20000);

    [Fact]
    public void Process_starts_are_cli_after_a_typed_enter_agent_under_an_agent_else_app_or_system()
    {
        Assert.Equal("cli:pwsh", _a.ForProcess(["pwsh.exe", "windowsterminal.exe"], T, T.AddSeconds(-1)).ToString());
        Assert.Equal("app", _a.ForProcess(["pwsh.exe", "windowsterminal.exe"], T, T.AddSeconds(-5)).ToString());
        Assert.Equal("agent:claude.exe", _a.ForProcess(["bash.exe", "claude.exe"], T, T.AddSeconds(-1)).ToString());
        Assert.Equal("agent:aider.exe", _a.ForProcess(["aider.exe"], T, null).ToString());
        Assert.Equal("system", _a.ForProcess(["svchost.exe", "services.exe"], T, null).ToString());
    }

    [Fact]
    public void A_run_with_mostly_hardware_keys_is_human_and_a_tie_goes_to_automation()
    {
        var human = new Actor(ActorKind.Human, null, ActorEvidence.Hardware);
        var inj = new Actor(ActorKind.Injected, null, ActorEvidence.InjectedFlag);
        Assert.Equal(ActorKind.Human, ActorAttributor.ForRun([human, human, inj]).Kind);
        Assert.Equal(ActorKind.Injected, ActorAttributor.ForRun([human, inj]).Kind);
    }
}

/// <summary>G-capture-net: the capture process references no networking assembly.</summary>
public sealed class CaptureNetTests
{
    [Fact]
    public void Tasktor_capture_references_no_system_net_assembly()
    {
        using var pe = new PEReader(File.OpenRead(Path.Combine(AppContext.BaseDirectory, "tasktor-capture.dll")));
        var md = pe.GetMetadataReader();
        var refs = md.AssemblyReferences.Select(h => md.GetString(md.GetAssemblyReference(h).Name)).ToList();
        Assert.NotEmpty(refs);
        Assert.DoesNotContain(refs, r => r.StartsWith("System.Net", StringComparison.Ordinal));
    }
}
