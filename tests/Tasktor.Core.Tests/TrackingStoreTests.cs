using System.Text;
using System.Text.Json;
using Tasktor.Contracts;
using Tasktor.Contracts.Workflow;
using Tasktor.Core.Learning;
using Tasktor.Core.Storage;
using Tasktor.Core.Workflow;
using Tasktor.Tests.Shared;

namespace Tasktor.Core.Tests;

/// <summary>C1 / C3 / C4 stores on the real SQLite engine: the gate before every write, retention, forget, the extract.</summary>
public sealed class TrackingStoreTests : IDisposable
{
    private const long Base = 1_793_000_000_000;
    private readonly TempStore _store = new();
    private readonly StoreSet _set;
    private readonly StoreWriter _writer;
    private readonly TrackingPipeline _pipe;
    private long _t;

    public TrackingStoreTests()
    {
        _set = new StoreSet(new EventStore(_store.Layout.EventsDb), new EpisodeStore(_store.Layout.EpisodesDb));
        _writer = StoreWriter.Inline(_set);
        var hasher = new KeyedHasher(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray(), "tok");
        _pipe = new TrackingPipeline(new PrivacyGate(new Redactor(["Alex Example", "alexex"])), new TokenBuckets(hasher), _writer, new PreregFile());
    }

    public void Dispose()
    {
        _writer.Dispose();
        _store.Dispose();
    }

    private void Fg(string app, string? title, double atS, int win = 1, string? domain = null)
    {
        _t = (long)(atS * 1000);
        _pipe.On(new CaptureEvent { Kind = CaptureKind.Foreground, MonoMs = _t, TsUtcMs = Base + _t, App = app, Win = win, Title = title, Domain = domain });
    }

    // SQLite holds the files open: on Windows only a share-read-write open may read them alongside.
    private static byte[] Read(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    private byte[] DbBytes() => [.. Read(_store.Layout.EventsDb), .. Read(_store.Layout.EpisodesDb), .. Read(_store.Layout.EventsDb + "-wal"), .. Read(_store.Layout.EpisodesDb + "-wal")];

    private bool Contains(string text) => Encoding.UTF8.GetString(DbBytes()).Contains(text, StringComparison.Ordinal);

    [Fact]
    public void Titles_hitting_each_rule_strength_never_reach_either_database()
    {
        Fg("code.exe", "parser.py - Visual Studio Code", 0);
        Fg("keepassxc.exe", "Vault FixtureSecretVault", 60);
        Fg("slack.exe", "FixtureDmPartner direct message", 120);
        Fg("msedge.exe", "FixtureIncognitoPage - InPrivate - Microsoft Edge", 180, 3, "secret.example.com");
        Fg("searchhost.exe", "FixtureSearchQuery", 240);
        Fg("chrome.exe", "Mail from alex@example.com about 123456 for Alex Example in C:\\Users\\alexex\\doc.txt", 300, 4, "mail.example.com");
        _pipe.Flush(400_000);
        foreach (var secret in new[] { "FixtureSecretVault", "FixtureDmPartner", "FixtureIncognitoPage", "secret.example.com", "FixtureSearchQuery", "alex@example.com", "123456", "Alex Example", "alexex" })
        {
            Assert.False(Contains(secret), secret + " reached a database");
        }

        Assert.True(Contains("parser.py"));
        Assert.True(Contains("mail.example.com")); // a hostname from a normal window is kept
        var kinds = new List<long>();
        using (var s = _set.Events.Db.Prepare("SELECT kind FROM event ORDER BY id"))
        {
            while (s.Step())
            {
                kinds.Add(s.Int64(0));
            }
        }

        Assert.Contains((long)CaptureKind.ExcludedStart, kinds);
        Assert.Contains((long)CaptureKind.ExcludedEnd, kinds);
    }

    [Fact]
    public void While_paused_only_pause_and_unpause_rows_are_written()
    {
        _pipe.Gate.Paused = true;
        _pipe.On(new CaptureEvent { Kind = CaptureKind.Pause, MonoMs = 0, TsUtcMs = Base });
        Fg("code.exe", "FixturePausedTitle", 10);
        _pipe.Gate.Paused = false;
        _pipe.On(new CaptureEvent { Kind = CaptureKind.Unpause, MonoMs = 20_000, TsUtcMs = Base + 20_000 });
        _pipe.Flush(30_000);
        Assert.Equal(2, _set.Events.Count);
        Assert.False(Contains("FixturePausedTitle"));
    }

    [Fact]
    public void Retention_purges_raw_events_and_forget_removes_every_derived_row_in_the_range()
    {
        Fg("code.exe", "a.py", 0);
        Fg("chrome.exe", "Docs", 600);
        Fg("code.exe", "b.py", 1200);
        _pipe.Flush(1_800_000);
        Assert.Equal(3, _set.Episodes.Activities);
        Assert.Equal(1, _set.Episodes.Db.Scalar("SELECT COUNT(*) FROM session"));
        Assert.True(_set.Episodes.Db.Scalar("SELECT COUNT(*) FROM activity_token") > 0);

        _set.Episodes.Forget(Base + 650_000, Base + 1_150_000);
        Assert.Equal(2, _set.Episodes.Activities); // the range overlaps only the middle activity (600–1200 s)
        Assert.Equal(0, _set.Episodes.Db.Scalar("SELECT COUNT(*) FROM activity_token WHERE activity NOT IN (SELECT id FROM activity)"));
        Assert.Equal(0, _set.Episodes.Db.Scalar("SELECT COUNT(*) FROM window_dwell WHERE activity NOT IN (SELECT id FROM activity)"));

        var removed = _set.Events.Purge(Base + 700_000, 0);
        Assert.Equal(2, removed);
        Assert.Equal(Base + 1_200_000, _set.Events.OldestUtcMs);
    }

    [Fact]
    public void Terms_are_promoted_after_5_activities_on_3_days()
    {
        for (var day = 0; day < 3; day++)
        {
            for (var k = 0; k < 2; k++)
            {
                var at = (day * 86_400.0) + (k * 1000);
                Fg("code.exe", "tasktor parser", at);
                Fg("chrome.exe", "other" + day + k, at + 120);
                Fg("code.exe", "tasktor parser", at + 240);
                _t = (long)((at + 400) * 1000);
                _pipe.On(new CaptureEvent { Kind = CaptureKind.Lock, MonoMs = _t, TsUtcMs = Base + _t });
                _pipe.On(new CaptureEvent { Kind = CaptureKind.Unlock, MonoMs = _t + 1, TsUtcMs = Base + _t + 1 });
            }
        }

        _pipe.Flush((long)(3 * 86_400_000.0));
        var terms = new List<string>();
        using var s = _set.Episodes.Db.Prepare("SELECT text FROM term ORDER BY text");
        while (s.Step())
        {
            terms.Add(s.Text(0)!);
        }

        Assert.Equal(["parser", "tasktor"], terms);
    }

    [Fact]
    public void The_extract_holds_numbers_and_frequent_terms_only_with_hashes()
    {
        Fg("code.exe", "FixtureRareTitleWord parser", 0);
        Fg("chrome.exe", "Docs", 600, 2, "docs.example.org");
        _pipe.Flush(1_300_000);
        var dir = Path.Combine(_store.Root, "train", "v1");
        var (_, hash, count) = TrainingExtract.Write(_set.Episodes, dir, "v1", DateTimeOffset.FromUnixTimeMilliseconds(Base + 2_000_000));
        Assert.Equal(2, count);
        Assert.Equal(64, hash.Length);
        var meta = JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(dir, "meta.json")), ContractsJsonContext.Default.ExtractMeta)!;
        Assert.Equal(["chrome.exe", "code.exe"], meta.Apps);
        Assert.Equal(TrainingExtract.DenseColumns, meta.Dense);
        Assert.Equal(2 * 7 * 4, new FileInfo(Path.Combine(dir, "dense.f32")).Length);
        foreach (var f in Directory.GetFiles(dir))
        {
            Assert.DoesNotContain("FixtureRareTitleWord", File.ReadAllText(f), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("docs.example.org", File.ReadAllText(f), StringComparison.Ordinal);
        }

        foreach (var (name, sha) in meta.Files)
        {
            Assert.Equal(sha, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, name)))));
        }
    }
}

public sealed class PreregTests
{
    [Fact]
    public void The_workers_prereg_json_holds_exactly_the_csharp_defaults()
    {
        var file = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(RepoPaths.Root, "worker", "prereg.json")), ContractsJsonContext.Default.PreregFile)!;
        Assert.Equal(new PreregFile().Episodes, file.Episodes);
        Assert.Equal(new PreregFile().Features, file.Features);
        Assert.Equal(new PreregFile().Clustering, file.Clustering);
        Assert.Equal(new PreregFile().Gates, file.Gates);
        Assert.Equal(new PreregFile().Assignment, file.Assignment);
    }
}
