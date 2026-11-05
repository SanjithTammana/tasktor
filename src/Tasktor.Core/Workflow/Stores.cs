using System.Security.Cryptography;
using System.Text.Json;
using Tasktor.Contracts;
using Tasktor.Contracts.Workflow;
using Tasktor.Core.Learning;
using Tasktor.Core.Storage;

namespace Tasktor.Core.Workflow;

/// <summary>Token → bucket (C5: a keyed hash into 2^18 buckets; the extract and models hold only buckets).</summary>
public sealed class TokenBuckets(KeyedHasher hasher, int buckets = 262_144)
{
    public int Count => buckets;

    public int Of(string token) => (int)(hasher.Hash(token).Lo % (ulong)buckets);
}

/// <summary>Local day number (days since 1970-01-01 in the capture's own UTC offset).</summary>
public static class Days
{
    public static int Of(long utcMs, int tzMin) => (int)Math.Floor((utcMs + (tzMin * 60_000L)) / 86_400_000.0);
}

/// <summary>
/// <c>events.db</c> (D-C2): the gated event stream, 30 days, plus the term candidates (raw side). One connection, owned by
/// the store writer thread. Titles here are already redacted; nothing else holds text.
/// </summary>
public sealed class EventStore : IDisposable
{
    private readonly SqliteDb _db;
    private readonly Dictionary<string, long> _apps = new(StringComparer.Ordinal);

    public EventStore(string path)
    {
        _db = SqliteDb.Open(path);
        _db.Exec("""
            CREATE TABLE IF NOT EXISTS app(id INTEGER PRIMARY KEY, key TEXT UNIQUE NOT NULL);
            CREATE TABLE IF NOT EXISTS event(id INTEGER PRIMARY KEY, ts_utc_ms INTEGER NOT NULL, tz_min INTEGER NOT NULL, mono_ms INTEGER NOT NULL,
              kind INTEGER NOT NULL, app INTEGER, win INTEGER, title TEXT, domain TEXT, idle_kind INTEGER);
            CREATE INDEX IF NOT EXISTS event_ts ON event(ts_utc_ms);
            CREATE TABLE IF NOT EXISTS term_candidate(token TEXT PRIMARY KEY, bucket INTEGER NOT NULL, activities INTEGER NOT NULL,
              days TEXT NOT NULL, last_day INTEGER NOT NULL);
            """);
    }

    public SqliteDb Db => _db;

    private long AppId(string key)
    {
        if (_apps.TryGetValue(key, out var id))
        {
            return id;
        }

        _db.Run("INSERT OR IGNORE INTO app(key) VALUES(?)", key);
        id = _db.Scalar("SELECT id FROM app WHERE key = ?", key);
        _apps[key] = id;
        return id;
    }

    public void Append(IReadOnlyList<CaptureEvent> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        _db.Transaction(() =>
        {
            using var s = _db.Prepare("INSERT INTO event(ts_utc_ms, tz_min, mono_ms, kind, app, win, title, domain, idle_kind) VALUES(?,?,?,?,?,?,?,?,?)");
            foreach (var e in batch)
            {
                s.BindAll(e.TsUtcMs, e.TzMin, e.MonoMs, (int)e.Kind, e.App is null ? null : AppId(e.App), e.Win, e.Title, e.Domain, e.Idle is null ? null : (int)e.Idle);
                s.Execute();
            }
        });
    }

    public long Count => _db.Scalar("SELECT COUNT(*) FROM event");

    public long? OldestUtcMs => Count == 0 ? null : _db.Scalar("SELECT MIN(ts_utc_ms) FROM event");

    /// <summary>Retention (C3.4): drop raw events older than the cutoff and term candidates unused for 30 days.</summary>
    public int Purge(long olderThanUtcMs, int today)
    {
        _db.Run("DELETE FROM event WHERE ts_utc_ms < ?", olderThanUtcMs);
        var n = _db.Changes;
        _db.Run("DELETE FROM term_candidate WHERE last_day < ?", today - 30);
        return n;
    }

    public int Forget(long fromUtcMs, long toUtcMs)
    {
        _db.Run("DELETE FROM event WHERE ts_utc_ms >= ? AND ts_utc_ms < ?", fromUtcMs, toUtcMs);
        return _db.Changes;
    }

    /// <summary>
    /// D-C4: count each token once per activity and its days; returns the tokens that just reached ≥ minActivities on
    /// ≥ minDays days (to promote into <c>episodes.db</c>).
    /// </summary>
    public List<(string Token, int Bucket)> ObserveTerms(IEnumerable<(string Token, int Bucket)> tokens, int day, int minActivities, int minDays)
    {
        var promoted = new List<(string, int)>();
        _db.Transaction(() =>
        {
            using var get = _db.Prepare("SELECT activities, days FROM term_candidate WHERE token = ?");
            using var put = _db.Prepare("INSERT OR REPLACE INTO term_candidate(token, bucket, activities, days, last_day) VALUES(?,?,?,?,?)");
            foreach (var (token, bucket) in tokens)
            {
                get.Bind(1, token);
                int count = 0;
                var days = new SortedSet<int>();
                if (get.Step())
                {
                    count = (int)get.Int64(0);
                    foreach (var d in (get.Text(1) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        days.Add(int.Parse(d, System.Globalization.CultureInfo.InvariantCulture));
                    }
                }

                get.Reset();
                var before = count >= minActivities && days.Count >= minDays;
                count++;
                days.Add(day);
                while (days.Count > 31)
                {
                    days.Remove(days.Min);
                }

                put.BindAll(token, bucket, count, string.Join(',', days), day);
                put.Execute();
                if (!before && count >= minActivities && days.Count >= minDays)
                {
                    promoted.Add((token, bucket));
                }
            }
        });
        return promoted;
    }

    public void Dispose() => _db.Dispose();
}

/// <summary>
/// <c>episodes.db</c> (D-C2): activities with hashed tokens, workflow windows, sessions, promoted terms and assignments;
/// kept (no raw text except promoted, unhidden frequent terms). Forget removes derived rows in one transaction.
/// </summary>
public sealed class EpisodeStore : IDisposable
{
    private readonly SqliteDb _db;

    public EpisodeStore(string path)
    {
        _db = SqliteDb.Open(path);
        _db.Exec("""
            CREATE TABLE IF NOT EXISTS activity(id INTEGER PRIMARY KEY, app TEXT NOT NULL, start_utc_ms INTEGER NOT NULL, end_utc_ms INTEGER NOT NULL,
              tz_min INTEGER NOT NULL, day INTEGER NOT NULL, active_ms INTEGER NOT NULL, idle_ms INTEGER NOT NULL, glances INTEGER NOT NULL,
              title_changes INTEGER NOT NULL, prev_app TEXT, next_app TEXT, job_frac REAL NOT NULL);
            CREATE INDEX IF NOT EXISTS activity_start ON activity(start_utc_ms);
            CREATE TABLE IF NOT EXISTS activity_token(activity INTEGER NOT NULL, bucket INTEGER NOT NULL, weight_ms INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS activity_token_a ON activity_token(activity);
            CREATE TABLE IF NOT EXISTS window(id INTEGER PRIMARY KEY, start_utc_ms INTEGER NOT NULL, end_utc_ms INTEGER NOT NULL, active_ms INTEGER NOT NULL, day INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS window_dwell(window INTEGER NOT NULL, activity INTEGER NOT NULL, dwell_ms INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS session(id INTEGER PRIMARY KEY, start_utc_ms INTEGER NOT NULL, end_utc_ms INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS session_activity(session INTEGER NOT NULL, activity INTEGER NOT NULL, ord INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS term(bucket INTEGER PRIMARY KEY, text TEXT NOT NULL, hidden INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS assignment(activity INTEGER PRIMARY KEY, model TEXT, cluster TEXT, confidence REAL, level TEXT);
            """);
    }

    public SqliteDb Db => _db;

    /// <summary>Activity ids of a run are <c>base + builder index</c>, so windows and sessions can refer to them before they close.</summary>
    public long NextBase() => _db.Scalar("SELECT COALESCE(MAX(id), 0) FROM activity") + 1_000;

    public void AddActivity(long id, ActivityRecord a, long endUtcMs, IReadOnlyList<(int Bucket, long WeightMs)> buckets)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(buckets);
        _db.Transaction(() =>
        {
            _db.Run(
                "INSERT INTO activity VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)",
                id, a.App, a.StartUtcMs, endUtcMs, a.TzMin, Days.Of(a.StartUtcMs, a.TzMin), a.ActiveMs, a.IdleMs, a.Glances, a.TitleChanges, a.PrevApp, a.NextApp, a.JobFrac);
            using var t = _db.Prepare("INSERT INTO activity_token VALUES(?,?,?)");
            foreach (var (bucket, w) in buckets)
            {
                t.BindAll(id, bucket, w);
                t.Execute();
            }
        });
    }

    public void AddWindow(long id, long startUtc, long endUtc, long activeMs, int day, IReadOnlyList<(long Activity, long DwellMs)> dwell)
    {
        ArgumentNullException.ThrowIfNull(dwell);
        _db.Transaction(() =>
        {
            _db.Run("INSERT INTO window VALUES(?,?,?,?,?)", id, startUtc, endUtc, activeMs, day);
            using var d = _db.Prepare("INSERT INTO window_dwell VALUES(?,?,?)");
            foreach (var (act, ms) in dwell)
            {
                d.BindAll(id, act, ms);
                d.Execute();
            }
        });
    }

    public void AddSession(long id, long startUtc, long endUtc, IReadOnlyList<long> activities)
    {
        ArgumentNullException.ThrowIfNull(activities);
        _db.Transaction(() =>
        {
            _db.Run("INSERT INTO session VALUES(?,?,?)", id, startUtc, endUtc);
            using var s = _db.Prepare("INSERT INTO session_activity VALUES(?,?,?)");
            for (var i = 0; i < activities.Count; i++)
            {
                s.BindAll(id, activities[i], i);
                s.Execute();
            }
        });
    }

    public void Promote(IEnumerable<(string Token, int Bucket)> terms)
    {
        foreach (var (token, bucket) in terms)
        {
            _db.Run("INSERT OR IGNORE INTO term(bucket, text) VALUES(?,?)", bucket, token);
        }
    }

    public void HideTerm(string text) => _db.Run("UPDATE term SET hidden = 1 WHERE text = ?", text);

    public void Assign(long activity, string? model, string? cluster, double confidence, string level) =>
        _db.Run("INSERT OR REPLACE INTO assignment VALUES(?,?,?,?,?)", activity, model, cluster, confidence, level);

    public long Activities => _db.Scalar("SELECT COUNT(*) FROM activity");

    /// <summary>C3.4: forget a range everywhere derived from it (activities, tokens, windows, sessions, assignments).</summary>
    public int Forget(long fromUtcMs, long toUtcMs)
    {
        var n = 0;
        _db.Transaction(() =>
        {
            const string Ids = "SELECT id FROM activity WHERE start_utc_ms < ?2 AND end_utc_ms > ?1";
            _db.Run("DELETE FROM activity_token WHERE activity IN (" + Ids + ")", fromUtcMs, toUtcMs);
            _db.Run("DELETE FROM assignment WHERE activity IN (" + Ids + ")", fromUtcMs, toUtcMs);
            _db.Run("DELETE FROM window_dwell WHERE window IN (SELECT id FROM window WHERE start_utc_ms < ?2 AND end_utc_ms > ?1) OR activity IN (" + Ids + ")", fromUtcMs, toUtcMs);
            _db.Run("DELETE FROM window WHERE start_utc_ms < ?2 AND end_utc_ms > ?1", fromUtcMs, toUtcMs);
            _db.Run("DELETE FROM session_activity WHERE activity IN (" + Ids + ")", fromUtcMs, toUtcMs);
            _db.Run("DELETE FROM session WHERE start_utc_ms < ?2 AND end_utc_ms > ?1", fromUtcMs, toUtcMs);
            _db.Run("DELETE FROM activity WHERE start_utc_ms < ?2 AND end_utc_ms > ?1", fromUtcMs, toUtcMs);
            n = _db.Changes;
        });
        return n;
    }

    public void Dispose() => _db.Dispose();
}

/// <summary>
/// The numbers-only training extract (D-C2): <c>meta.json</c>, <c>tokens.csr</c>, <c>dense.f32</c>, <c>days.i32</c>,
/// <c>apps.i32</c>, each with its SHA-256. The worker never sees a database or a title; the only text is the frequent-term
/// dictionary for names.
/// </summary>
public static class TrainingExtract
{
    public static readonly string[] DenseColumns = ["hour", "weekend", "duration_min", "idle_ratio", "glances", "title_rate", "job_frac"];

    public static (string Path, string Hash, int Count) Write(EpisodeStore store, string dir, string version, DateTimeOffset now, int fitDays = 90, int cap = 10_000)
    {
        ArgumentNullException.ThrowIfNull(store);
        Directory.CreateDirectory(dir);
        var db = store.Db;
        var fromUtc = (now - TimeSpan.FromDays(fitDays)).ToUnixTimeMilliseconds();
        var rows = new List<(long Id, string App, long Start, int Tz, int Day, long Active, long Idle, int Glances, int Titles, string? Prev, string? Next, double Job)>();
        using (var s = db.Prepare("SELECT id, app, start_utc_ms, tz_min, day, active_ms, idle_ms, glances, title_changes, prev_app, next_app, job_frac FROM activity WHERE start_utc_ms >= ? ORDER BY id"))
        {
            s.Bind(1, fromUtc);
            while (s.Step())
            {
                rows.Add((s.Int64(0), s.Text(1)!, s.Int64(2), (int)s.Int64(3), (int)s.Int64(4), s.Int64(5), s.Int64(6), (int)s.Int64(7), (int)s.Int64(8), s.Text(9), s.Text(10), s.Double(11)));
            }
        }

        if (rows.Count > cap)
        {
            // Stratified by day: every day keeps the same share, deterministic.
            var keep = new List<(long, string, long, int, int, long, long, int, int, string?, string?, double)>();
            foreach (var g in rows.GroupBy(r => r.Day))
            {
                var take = Math.Max(1, (int)Math.Round(g.Count() * (double)cap / rows.Count));
                var step = (double)g.Count() / take;
                var list = g.ToList();
                for (var i = 0; i < take; i++)
                {
                    keep.Add(list[(int)(i * step)]);
                }
            }

            rows = [.. keep.OrderBy(r => r.Item1)];
        }

        var apps = rows.SelectMany(r => new[] { r.App, r.Prev, r.Next }).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var appIndex = apps.Select((a, i) => (a, i)).ToDictionary(x => x.a, x => x.i, StringComparer.Ordinal);
        var index = rows.Select((r, i) => (r.Id, i)).ToDictionary(x => x.Id, x => x.i);

        var indptr = new List<int> { 0 };
        var bucket = new List<int>();
        var weight = new List<float>();
        using (var t = db.Prepare("SELECT bucket, weight_ms FROM activity_token WHERE activity = ? ORDER BY bucket"))
        {
            foreach (var r in rows)
            {
                t.Bind(1, r.Id);
                var merged = new SortedDictionary<int, long>();
                while (t.Step())
                {
                    var b = (int)t.Int64(0);
                    merged[b] = merged.GetValueOrDefault(b) + t.Int64(1);
                }

                t.Reset();
                foreach (var (b, w) in merged)
                {
                    bucket.Add(b);
                    weight.Add(w);
                }

                indptr.Add(bucket.Count);
            }
        }

        var dense = new float[rows.Count * DenseColumns.Length];
        var days = new int[rows.Count];
        var appIds = new int[rows.Count * 3];
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var d = Dense(r.Start, r.Tz, r.Active, r.Idle, r.Glances, r.Titles, r.Job);
            Array.Copy(d, 0, dense, i * DenseColumns.Length, d.Length);
            days[i] = r.Day;
            appIds[(i * 3) + 0] = appIndex[r.App];
            appIds[(i * 3) + 1] = r.Prev is null ? -1 : appIndex[r.Prev];
            appIds[(i * 3) + 2] = r.Next is null ? -1 : appIndex[r.Next];
        }

        var windows = new List<ExtractWindow>();
        using (var w = db.Prepare("SELECT w.id, w.day, d.activity, d.dwell_ms FROM window w JOIN window_dwell d ON d.window = w.id WHERE w.start_utc_ms >= ? ORDER BY w.id, d.activity"))
        {
            w.Bind(1, fromUtc);
            long? cur = null;
            var day = 0;
            var acts = new List<int>();
            var dwell = new List<long>();
            void Emit()
            {
                if (cur is { } id && acts.Count > 0)
                {
                    windows.Add(new ExtractWindow(id, day, [.. acts], [.. dwell]));
                }

                acts.Clear();
                dwell.Clear();
            }

            while (w.Step())
            {
                if (w.Int64(0) != cur)
                {
                    Emit();
                    cur = w.Int64(0);
                    day = (int)w.Int64(1);
                }

                if (index.TryGetValue(w.Int64(2), out var ai))
                {
                    acts.Add(ai);
                    dwell.Add(w.Int64(3));
                }
            }

            Emit();
        }

        var terms = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var q = db.Prepare("SELECT bucket, text FROM term WHERE hidden = 0 ORDER BY bucket"))
        {
            while (q.Step())
            {
                terms[q.Int64(0).ToString(System.Globalization.CultureInfo.InvariantCulture)] = q.Text(1)!;
            }
        }

        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tokens.csr"] = WriteFile(Path.Combine(dir, "tokens.csr"), Csr(rows.Count, indptr, bucket, weight)),
            ["dense.f32"] = WriteFile(Path.Combine(dir, "dense.f32"), Bytes(dense)),
            ["days.i32"] = WriteFile(Path.Combine(dir, "days.i32"), Bytes(days)),
            ["apps.i32"] = WriteFile(Path.Combine(dir, "apps.i32"), Bytes(appIds)),
        };
        var meta = new ExtractMeta
        {
            Version = version,
            CreatedAt = now,
            Activities = rows.Count,
            Apps = apps,
            Terms = terms,
            Dense = DenseColumns,
            Windows = windows,
            ActivityIds = [.. rows.Select(r => r.Id)],
            Files = files,
        };
        var metaBytes = JsonSerializer.SerializeToUtf8Bytes(meta, ContractsJsonContext.Default.ExtractMeta);
        var hash = WriteFile(Path.Combine(dir, "meta.json"), metaBytes);
        return (dir, hash, rows.Count);
    }

    /// <summary>The raw dense columns of one activity (the same numbers the assigner computes online).</summary>
    public static float[] Dense(long startUtcMs, int tzMin, long activeMs, long idleMs, int glances, int titleChanges, double jobFrac)
    {
        var local = DateTimeOffset.FromUnixTimeMilliseconds(startUtcMs + (tzMin * 60_000L)).UtcDateTime;
        var minutes = activeMs / 60_000.0;
        return
        [
            (float)(local.Hour + (local.Minute / 60.0)),
            local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 1f : 0f,
            (float)minutes,
            activeMs + idleMs == 0 ? 0f : (float)((double)idleMs / (activeMs + idleMs)),
            glances,
            (float)(titleChanges / Math.Max(1.0, minutes)),
            (float)jobFrac,
        ];
    }

    private static byte[] Csr(int n, List<int> indptr, List<int> bucket, List<float> weight)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(n);
        w.Write(bucket.Count);
        foreach (var x in indptr)
        {
            w.Write(x);
        }

        foreach (var x in bucket)
        {
            w.Write(x);
        }

        foreach (var x in weight)
        {
            w.Write(x);
        }

        w.Flush();
        return ms.ToArray();
    }

    private static byte[] Bytes<T>(T[] values)
        where T : unmanaged => System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()).ToArray();

    private static string WriteFile(string path, byte[] bytes)
    {
        AtomicFile.Write(path, bytes);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
