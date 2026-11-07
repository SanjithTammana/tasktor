using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Tasktor.Contracts;
using Tasktor.Contracts.Config;
using Tasktor.Core.Storage;
using Tasktor.Tests.Shared;

namespace Tasktor.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly TempStore _store = new();

    public void Dispose() => _store.Dispose();

    [Fact]
    public void Missing_file_means_defaults()
    {
        var s = new SettingsStore(_store.Layout);
        s.Load();
        Assert.False(s.AllowDestructiveCli);
        Assert.Equal(80, s.ThresholdFor("cpu").Warn);
        Assert.Equal(3, s.FocusPresets.Count);
        Assert.True(s.JobLearning);
    }

    [Fact]
    public void Comments_and_trailing_commas_load()
    {
        File.WriteAllText(_store.Layout.Config, """
            // my settings
            { "schema_version": 1, "cli": { "allow_destructive": true, }, /* on */ "modules": { "ports": true } }
            """);
        var s = new SettingsStore(_store.Layout);
        s.Load();
        Assert.True(s.AllowDestructiveCli);
        Assert.True(s.ModuleOn("ports"));
        Assert.False(s.ModuleOn("capture"));
    }

    [Fact]
    public void Old_version_migrates_forward_and_keeps_a_bak()
    {
        File.WriteAllText(_store.Layout.Config, """{ "schema_version": 0, "update_check": true }""");
        var s = new SettingsStore(_store.Layout);
        s.Load();
        Assert.Equal(1, s.Current.SchemaVersion);
        Assert.True(File.Exists(_store.Layout.Config + ".bak"));
        Assert.Contains("\"schema_version\": 1", File.ReadAllText(_store.Layout.Config), StringComparison.Ordinal);
    }

    [Fact]
    public void A_broken_file_is_left_alone_and_reported()
    {
        File.WriteAllText(_store.Layout.Config, "{ \"cli\": ");
        var s = new SettingsStore(_store.Layout);
        s.Load();
        Assert.NotNull(s.Error);
        Assert.Equal("{ \"cli\": ", File.ReadAllText(_store.Layout.Config));
        Assert.False(s.AllowDestructiveCli);
    }

    [Fact]
    public void Newer_version_is_not_guessed()
    {
        File.WriteAllText(_store.Layout.Config, """{ "schema_version": 9, "cli": { "allow_destructive": true } }""");
        var s = new SettingsStore(_store.Layout);
        s.Load();
        Assert.Contains("newer", s.Error, StringComparison.Ordinal);
        Assert.False(s.AllowDestructiveCli);
    }

    [Fact]
    public void Save_round_trips()
    {
        var s = new SettingsStore(_store.Layout);
        s.Save(new ConfigFile { Jobs = new JobSettings { Learning = false } });
        var t = new SettingsStore(_store.Layout);
        t.Load();
        Assert.False(t.JobLearning);
    }
}

public sealed class ConsentAndKeyTests : IDisposable
{
    private readonly TempStore _store = new();

    public void Dispose() => _store.Dispose();

    [Fact]
    public void Plugin_consent_is_pinned_to_both_hashes()
    {
        var c = new ConsentStore(_store.Layout);
        c.GivePlugin("example.todo-count", new PluginConsent { ManifestSha256 = "aa", CommandSha256 = "bb", GivenAt = DateTimeOffset.UnixEpoch });
        var reloaded = new ConsentStore(_store.Layout);
        reloaded.Load();
        Assert.True(reloaded.PluginAllowed("example.todo-count", "AA", "bb"));
        Assert.False(reloaded.PluginAllowed("example.todo-count", "aa", "cc")); // command changed: ask again
        Assert.False(reloaded.PluginAllowed("example.todo-count", "ab", "bb")); // manifest changed: ask again
    }

    [Fact]
    public void Corrupt_consents_mean_none()
    {
        Directory.CreateDirectory(_store.Layout.StateDir);
        File.WriteAllText(_store.Layout.Consents, "garbage");
        var c = new ConsentStore(_store.Layout);
        c.Load();
        Assert.False(c.HasModule("tracking"));
    }

    [Fact]
    public void Install_key_is_created_protected_and_reloaded()
    {
        var protect = new KeyProtection(b => [.. b.Select(x => (byte)(x ^ 0x5A))], b => [.. b.Select(x => (byte)(x ^ 0x5A))], "xor-test");
        var k1 = InstallKey.LoadOrCreate(_store.Layout, protect);
        Assert.True(k1.Created);
        var k2 = InstallKey.LoadOrCreate(_store.Layout, protect);
        Assert.False(k2.Created);
        Assert.Equal(k1.Fingerprint(), k2.Fingerprint());
        Assert.Equal(k1.Hasher("job.kind").Hash("x"), k2.Hasher("job.kind").Hash("x"));
        Assert.NotEqual(File.ReadAllBytes(_store.Layout.InstallKey), protect.Unprotect(File.ReadAllBytes(_store.Layout.InstallKey)));
    }

    [Fact]
    public void An_unreadable_key_is_replaced()
    {
        Directory.CreateDirectory(_store.Layout.StateDir);
        File.WriteAllBytes(_store.Layout.InstallKey, [1, 2, 3]);
        var k = InstallKey.LoadOrCreate(_store.Layout, KeyProtection.None);
        Assert.True(k.Created);
        Assert.Equal(32, File.ReadAllBytes(_store.Layout.InstallKey).Length);
    }

    [Fact]
    public void Encryption_keys_never_equal_hash_keys()
    {
        var k = InstallKey.FromBytes(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        var vault = k.EncryptionKey("vault");
        Assert.Equal(32, vault.Length);
        Assert.NotEqual(vault, k.EncryptionKey("index"));
        Assert.NotEqual(Convert.ToHexString(vault), k.Hasher("vault").Hash("x").ToString());
    }
}

public sealed class ForgetterTests : IDisposable
{
    private readonly TempStore _store = new();

    public void Dispose() => _store.Dispose();

    private void Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
    }

    private void Fill()
    {
        var l = _store.Layout;
        foreach (var p in new[] { l.Config, l.Rules, l.Presets, l.InstallKey, l.Consents, l.Frozen, l.JobKinds, l.Focus, l.EventsDb, l.EpisodesDb, l.VaultDb, l.SelfUse, l.Snapshot, l.JobFile("a"), l.AgentFile("s"), Path.Combine(l.LogDir, "tasktor-20261104.log"), Path.Combine(l.NotesDir, "n.txt"), Path.Combine(l.PluginDir("p"), "plugin.json") })
        {
            Touch(p);
        }
    }

    [Fact]
    public async Task Forget_learned_removes_only_learned_state()
    {
        Fill();
        await new Forgetter(_store.Layout).ForgetAsync(ForgetScope.Learned);
        Assert.False(File.Exists(_store.Layout.JobKinds));
        Assert.True(File.Exists(_store.Layout.EventsDb));
    }

    [Fact]
    public async Task Forget_tracking_clears_track_and_focus_and_keeps_presets()
    {
        Fill();
        await new Forgetter(_store.Layout).ForgetAsync(ForgetScope.Tracking);
        Assert.False(Directory.Exists(_store.Layout.TrackDir));
        Assert.False(File.Exists(_store.Layout.Focus));
        Assert.True(File.Exists(_store.Layout.Presets));
        Assert.True(File.Exists(_store.Layout.VaultDb));
    }

    [Fact]
    public async Task Forget_all_keeps_settings_rules_key_consents_notes_presets_plugins_and_frozen_recovery()
    {
        Fill();
        var stopped = new List<string>();
        var f = new Forgetter(_store.Layout);
        f.Before[ForgetScope.Tracking].Add(() => { stopped.Add("tracking"); return Task.CompletedTask; });
        f.Before[ForgetScope.Capture].Add(() => { stopped.Add("capture"); return Task.CompletedTask; });
        await f.ForgetAsync(ForgetScope.All);
        var l = _store.Layout;
        foreach (var gone in new[] { l.JobKinds, l.EventsDb, l.VaultDb, l.SelfUse, l.Snapshot, l.JobFile("a"), l.AgentFile("s") })
        {
            Assert.False(File.Exists(gone), gone);
        }

        Assert.False(Directory.Exists(l.LogDir));
        foreach (var kept in new[] { l.Config, l.Rules, l.Presets, l.InstallKey, l.Consents, l.Frozen, Path.Combine(l.NotesDir, "n.txt"), Path.Combine(l.PluginDir("p"), "plugin.json") })
        {
            Assert.True(File.Exists(kept), kept);
        }

        Assert.Equal(["tracking", "capture"], stopped); // stores closed before deletion
    }

    [Fact]
    public async Task Forget_capture_since_calls_the_capture_range_delete()
    {
        var f = new Forgetter(_store.Layout) { ForgetCaptureSince = _ => Task.FromResult(12) };
        var r = await f.ForgetAsync(ForgetScope.Capture, DateTimeOffset.UnixEpoch);
        Assert.Equal(12, r.CaptureRecords);
    }
}

public sealed class DataExporterTests : IDisposable
{
    private readonly TempStore _store = new();

    public void Dispose() => _store.Dispose();

    internal static DataExporter Sample()
    {
        var e = new DataExporter();
        var t0 = new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);
        e.Register(new ExportTableSource("jobs_runs", ["kind", "ended_at", "duration_s", "exit_code", "good"], _ =>
        [
            ["3f1c9a0b2d4e5f60718293a4b5c6d7e8", t0, 4200.5, 0, true],
            ["3f1c9a0b2d4e5f60718293a4b5c6d7e8", t0.AddHours(3), 3900.0, 1, false],
        ]));
        e.Register(new ExportTableSource("selfuse", ["feature", "count"], _ => [["action.open", 3L], ["card, \"quoted\"", 1L]]));
        e.Register(new ExportTableSource("events_raw", ["at", "title"], _ => [[t0, "Secret window title"]], ExportGate.Raw));
        e.Register(new ExportTableSource("capture_keys", ["at", "text"], _ => [[t0, "typed text"]], ExportGate.Capture));
        return e;
    }

    private static ExportOptions Options(string folder, bool raw = false) => new()
    {
        Folder = folder,
        Now = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero),
        Version = "0.1.0",
        Since = "30d",
        IncludeRaw = raw,
    };

    [Fact]
    public void Writes_csv_jsonl_and_manifest_and_leaves_gated_tables_out()
    {
        var folder = Path.Combine(_store.Root, "export");
        var r = Sample().Export(Options(folder), null);
        Assert.Equal(["jobs_runs", "selfuse"], r.Manifest.Tables.Select(t => t.Name));
        Assert.Equal(2, r.Manifest.LeftOut!.Count);
        var csv = File.ReadAllText(Path.Combine(folder, "selfuse.csv"));
        Assert.Equal("feature,count\r\naction.open,3\r\n\"card, \"\"quoted\"\"\",1\r\n", csv);
        var lines = File.ReadAllLines(Path.Combine(folder, "jobs_runs.jsonl"));
        Assert.Equal("""{"kind":"3f1c9a0b2d4e5f60718293a4b5c6d7e8","ended_at":"2026-11-04T09:00:00Z","duration_s":4200.5,"exit_code":0,"good":true}""", lines[0]);
        Assert.False(File.Exists(Path.Combine(folder, "events_raw.csv")));
        var manifest = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(folder, "manifest.json")), ContractsJsonContext.Default.ExportManifest)!;
        Assert.Equal("tasktor.export/1", manifest.Schema);
    }

    [Fact]
    public void Raw_titles_only_with_the_flag()
    {
        var folder = Path.Combine(_store.Root, "raw");
        Sample().Export(Options(folder, raw: true), null);
        Assert.Contains("Secret window title", File.ReadAllText(Path.Combine(folder, "events_raw.csv")), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(folder, "capture_keys.csv")));
    }

    [Fact]
    public void The_committed_sample_export_is_what_csharp_writes()
    {
        // worker/tests/test_tasktor_data.py loads tests/fixtures/export-sample; this keeps that sample current.
        var folder = Path.Combine(_store.Root, "sample");
        Sample().Export(Options(folder), null);
        var sample = Path.Combine(RepoPaths.Fixtures, "export-sample");
        if (Environment.GetEnvironmentVariable("TASKTOR_REGEN_FIXTURES") == "1")
        {
            Directory.CreateDirectory(sample);
            foreach (var f in Directory.GetFiles(folder))
            {
                File.Copy(f, Path.Combine(sample, Path.GetFileName(f)), overwrite: true);
            }
        }

        foreach (var f in Directory.GetFiles(folder).Select(Path.GetFileName))
        {
            Assert.True(File.Exists(Path.Combine(sample, f!)), "missing in sample: " + f);
            Assert.Equal(File.ReadAllText(Path.Combine(folder, f!)).Replace("\r\n", "\n", StringComparison.Ordinal), File.ReadAllText(Path.Combine(sample, f!)).Replace("\r\n", "\n", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("/home/u/OneDrive/exports", true)]
    [InlineData("/home/u/OneDrive - Contoso/x", true)]
    [InlineData("/home/u/Dropbox/x", true)]
    [InlineData("/home/u/Documents/Tasktor exports", false)]
    public void Cloud_synced_folders_are_detected(string path, bool cloud) => Assert.Equal(cloud, DataExporter.IsCloudSynced(path));
}

public sealed class SettingsArchiveTests : IDisposable
{
    private readonly TempStore _store = new();

    public void Dispose() => _store.Dispose();

    [Fact]
    public void Export_never_includes_the_key_track_or_capture()
    {
        var l = _store.Layout;
        foreach (var p in new[] { l.Config, l.Rules, l.Presets, l.InstallKey, l.Consents, l.JobKinds, l.EventsDb, l.VaultDb, Path.Combine(l.NotesDir, "a.txt") })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, p.EndsWith(".json", StringComparison.Ordinal) ? "{}" : "x");
        }

        Directory.CreateDirectory(l.PluginDir("tasktor.repos"));
        var zip = Path.Combine(_store.Root, "s.zip");
        var a = new SettingsArchive(l);
        var plain = a.Export(zip, includeLearned: false, includeNotes: false);
        Assert.Equal(["config.json", "rules.json", "presets.json", "state/consents.json", "plugins.txt"], plain);
        File.Delete(zip);
        var full = a.Export(zip, includeLearned: true, includeNotes: true);
        Assert.Contains("state/job-kinds.json", full);
        Assert.Contains("notes/a.txt", full);
        Assert.DoesNotContain(full, e => e.Contains("install.key", StringComparison.Ordinal) || e.Contains("track", StringComparison.Ordinal) || e.Contains("vault", StringComparison.Ordinal));
    }

    [Fact]
    public void Import_validates_before_replacing()
    {
        var l = _store.Layout;
        File.WriteAllText(l.Config, """{ "update_check": false }""");
        var bad = Path.Combine(_store.Root, "bad.zip");
        using (var z = ZipFile.Open(bad, ZipArchiveMode.Create))
        {
            Write(z, "config.json", """{ "update_check": true }""");
            Write(z, "rules.json", "{ not json");
        }

        Assert.Throws<InvalidDataException>(() => new SettingsArchive(l).Import(bad));
        Assert.Contains("false", File.ReadAllText(l.Config), StringComparison.Ordinal); // nothing replaced

        var evil = Path.Combine(_store.Root, "evil.zip");
        using (var z = ZipFile.Open(evil, ZipArchiveMode.Create))
        {
            Write(z, "state/install.key", "x");
        }

        Assert.Throws<InvalidDataException>(() => new SettingsArchive(l).Import(evil));

        var good = Path.Combine(_store.Root, "good.zip");
        using (var z = ZipFile.Open(good, ZipArchiveMode.Create))
        {
            Write(z, "config.json", """{ "update_check": true }""");
        }

        new SettingsArchive(l).Import(good);
        Assert.Contains("true", File.ReadAllText(l.Config), StringComparison.Ordinal);
        Assert.True(File.Exists(l.Config + ".bak"));
    }

    [Fact]
    public void Reset_restores_defaults_and_keeps_data()
    {
        var l = _store.Layout;
        File.WriteAllText(l.Config, """{ "update_check": true }""");
        File.WriteAllText(l.Rules, """{ "rules": [] }""");
        Directory.CreateDirectory(l.TrackDir);
        File.WriteAllText(l.EventsDb, "data");
        new SettingsArchive(l).Reset();
        Assert.DoesNotContain("update_check", File.ReadAllText(l.Config), StringComparison.Ordinal);
        Assert.False(File.Exists(l.Rules));
        Assert.True(File.Exists(l.Rules + ".bak"));
        Assert.True(File.Exists(l.EventsDb));
    }

    private static void Write(ZipArchive z, string name, string text)
    {
        using var w = new StreamWriter(z.CreateEntry(name).Open(), new UTF8Encoding(false));
        w.Write(text);
    }
}

public sealed class SelfUseAndLogScannerTests : IDisposable
{
    private readonly TempStore _store = new();

    public void Dispose() => _store.Dispose();

    [Fact]
    public void Counts_persist_numbers_only()
    {
        var c = new SelfUseCounter(_store.Layout);
        c.Count("card.core.system", DateTimeOffset.UnixEpoch);
        c.Count("card.core.system", DateTimeOffset.UnixEpoch);
        c.Flush();
        var d = new SelfUseCounter(_store.Layout);
        d.Load();
        Assert.Equal(2, d.Counts["card.core.system"]);
    }

    [Fact]
    public void Log_lines_parse_back_with_quotes()
    {
        var e = LogScanner.Parse("2026-11-04T09:00:00.000Z INFO action action=proc.end target=\"my app.exe\" origin=cli result=ok")!.Value;
        Assert.Equal("action", e.Event);
        Assert.Equal("my app.exe", e.Fields["target"]);
        Assert.Null(LogScanner.Parse("garbage"));
    }

    [Fact]
    public void Store_paths_never_escape_the_root()
    {
        Assert.Equal("_.._evil", StoreLayout.SafeName("/../evil"));
        Assert.StartsWith(_store.Layout.JobsDir, _store.Layout.JobFile("../../x"), StringComparison.Ordinal);
        Assert.False(StoreLayout.IsValidId(".."));
        Assert.True(StoreLayout.IsValidId("embed-2026-11-04"));
    }

    [Fact]
    public void A_key_on_an_engine_without_encryption_is_refused_before_anything_is_written()
    {
        // The system SQLite (and Windows' winsqlite3) has no codec: a store that needs encryption must not land on disk.
        var path = Path.Combine(Path.GetTempPath(), "tasktor-nokey-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        Assert.Throws<NotSupportedException>(() => SqliteDb.Open(path, key: new byte[32]));
        Assert.False(File.Exists(path) && new FileInfo(path).Length > 0);
        File.Delete(path);
    }
}
