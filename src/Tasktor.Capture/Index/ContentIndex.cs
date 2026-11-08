using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tasktor.Capture.Deep;
using Tasktor.Capture.Recording;
using Tasktor.Core.Storage;

namespace Tasktor.Capture.Index;

/// <summary>One activity that matched: when it was, which track, a snippet with the matched words in brackets.</summary>
public sealed record SearchHit(DateTimeOffset Start, DateTimeOffset End, int Track, string Snippet, double Score)
{
    /// <summary>The activity's web pages (scrubbed addresses), first seen first: "reopen" opens one.</summary>
    public IReadOnlyList<string> Links { get; init; } = [];
}

/// <summary>
/// C21 / C §9 content index over closed activity documents (already redacted and block-checked when they were recorded).
/// Documents become 1–2 KB chunks with ~15% overlap; a chunk within SimHash distance 3 of one of the last 500 is dropped
/// (screen text repeats). Two FTS5 tables: words (<c>unicode61</c>, diacritics folded) and trigrams (parts of words,
/// identifiers). Search fuses both rankings with reciprocal rank (k = 60), groups by activity and returns snippets.
/// Embeddings (and gate G8) join the fusion later. On disk the index exists only encrypted (<see cref="OpenEncrypted"/>).
/// Single-threaded: the capture logic loop owns it.
/// </summary>
public sealed partial class ContentIndex : IDisposable
{
    public const int ChunkTarget = 1536;
    public const int ChunkMax = 2048;
    public const double Overlap = 0.15;
    public const int DedupeWindow = 500;
    public const int DedupeDistance = 3;
    public const int RrfK = 60;
    public const int PerList = 100;

    private readonly SqliteDb _db;
    private readonly Queue<ulong> _recent = new();

    private ContentIndex(SqliteDb db)
    {
        _db = db;
        _db.Exec("""
            CREATE TABLE IF NOT EXISTS chunk(id INTEGER PRIMARY KEY, start_ms INTEGER NOT NULL, end_ms INTEGER NOT NULL, track INTEGER NOT NULL, simhash INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS chunk_start ON chunk(start_ms);
            CREATE VIRTUAL TABLE IF NOT EXISTS fts USING fts5(text, tokenize='unicode61 remove_diacritics 2');
            CREATE VIRTUAL TABLE IF NOT EXISTS fts_tri USING fts5(text, tokenize='trigram');
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS link(start_ms INTEGER NOT NULL, track INTEGER NOT NULL, ord INTEGER NOT NULL, url TEXT NOT NULL, PRIMARY KEY(start_ms, track, ord));
            """);
        if (_db.Scalar("SELECT count(*) FROM pragma_table_info('chunk') WHERE name = 'sources'") == 0)
        {
            _db.Exec("ALTER TABLE chunk ADD COLUMN sources TEXT NOT NULL DEFAULT ''"); // an index made before C21.4
        }

        LoadRecent();
    }

    private void LoadRecent()
    {
        _recent.Clear();
        using var s = _db.Prepare("SELECT simhash FROM chunk ORDER BY id DESC LIMIT " + DedupeWindow.ToString(CultureInfo.InvariantCulture));
        var newestFirst = new List<ulong>();
        while (s.Step())
        {
            newestFirst.Add(unchecked((ulong)s.Int64(0)));
        }

        for (var i = newestFirst.Count - 1; i >= 0; i--)
        {
            _recent.Enqueue(newestFirst[i]);
        }
    }

    /// <summary>An index over an open database (tests use <c>:memory:</c>).</summary>
    public static ContentIndex Open(SqliteDb db) => new(db ?? throw new ArgumentNullException(nameof(db)));

    /// <summary>The index file, encrypted with <paramref name="key"/>; null when this SQLite engine cannot encrypt (OQ-C22) or the file is not readable with this key.</summary>
    public static ContentIndex? OpenEncrypted(string path, byte[] key)
    {
        SqliteDb? db = null;
        try
        {
            db = SqliteDb.Open(path, key: key);
            return new ContentIndex(db);
        }
        catch (NotSupportedException)
        {
            return null; // this engine cannot encrypt: no index rather than a plain one
        }
        catch (SqliteException)
        {
            db?.Dispose();
            return null; // not readable with this key (another install key): left as it is, not indexed into
        }
    }

    public long ChunkCount => _db.Scalar("SELECT count(*) FROM chunk");

    public long LinkCount => _db.Scalar("SELECT count(*) FROM link");

    /// <summary>Index one closed document; returns the chunks added (near-duplicates of recent ones are not).</summary>
    public int Add(ActivityDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var text = string.Join('\n', doc.Sections.Where(s => s.Text.Length > 0).Select(s => s.Heading + ": " + s.Text));
        var sources = string.Join('\n', doc.Apps.Select(x => "app:" + x).Concat(doc.Hosts.Select(x => "host:" + x)));
        var added = 0;
        _db.Transaction(() =>
        {
            foreach (var chunk in Chunks(text))
            {
                var hash = SimHash(chunk);
                if (_recent.Any(h => ulong.PopCount(h ^ hash) <= DedupeDistance))
                {
                    continue;
                }

                _db.Run("INSERT INTO chunk(start_ms, end_ms, track, simhash, sources) VALUES (?, ?, ?, ?, ?)", doc.Start.ToUnixTimeMilliseconds(), doc.End.ToUnixTimeMilliseconds(), doc.Track, unchecked((long)hash), sources);
                var id = _db.LastInsertRowId;
                _db.Run("INSERT INTO fts(rowid, text) VALUES (?, ?)", id, chunk);
                _db.Run("INSERT INTO fts_tri(rowid, text) VALUES (?, ?)", id, chunk);
                _recent.Enqueue(hash);
                if (_recent.Count > DedupeWindow)
                {
                    _recent.Dequeue();
                }

                added++;
            }

            if (added > 0)
            {
                // the activity's pages, kept beside its chunks (C §9 link table): a hit can reopen them
                for (var i = 0; i < doc.Links.Count && i < ActivityDocumentBuilder.MaxLinks; i++)
                {
                    if (ActivityDocumentBuilder.IsWebLink(doc.Links[i]))
                    {
                        _db.Run("INSERT OR IGNORE INTO link(start_ms, track, ord, url) VALUES (?, ?, ?, ?)", doc.Start.ToUnixTimeMilliseconds(), doc.Track, i, doc.Links[i]);
                    }
                }
            }
        });
        return added;
    }

    /// <summary>
    /// Index the vault's closed documents recorded after the last ingest, up to <paramref name="until"/> (the content
    /// worker's batch, D-C21). Returns the documents read.
    /// </summary>
    public int Ingest(RawVault vault, DateTimeOffset until)
    {
        ArgumentNullException.ThrowIfNull(vault);
        var last = _db.Scalar("SELECT value FROM meta WHERE key = 'ingested_ms'");
        var from = last == 0 ? DateTimeOffset.UnixEpoch : DateTimeOffset.FromUnixTimeMilliseconds(last);
        var read = 0;
        foreach (var r in vault.Read(from, until).Where(r => r.Kind == RecordKind.Document && r.At.ToUnixTimeMilliseconds() > last))
        {
            if (JsonSerializer.Deserialize(r.Body, RecordJsonContext.Default.ActivityDocument) is { } doc)
            {
                Add(doc);
                read++;
            }

            last = Math.Max(last, r.At.ToUnixTimeMilliseconds());
        }

        _db.Run("INSERT INTO meta(key, value) VALUES ('ingested_ms', ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value", last);
        return read;
    }

    /// <summary>
    /// Activities matching every word of <paramref name="query"/> (or its ≥ 3-letter parts, by trigram), best first.
    /// Query text is never interpreted as FTS syntax: each word is quoted.
    /// </summary>
    public IReadOnlyList<SearchHit> Search(string query, DateTimeOffset? from = null, DateTimeOffset? to = null, int limit = 20)
    {
        var words = Words().Matches(query ?? string.Empty).Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (words.Count == 0)
        {
            return [];
        }

        var wordQuery = string.Join(' ', words.Select(Quote));
        var triWords = words.Where(w => w.Length >= 3).ToList();
        var scores = new Dictionary<long, double>();
        void Fuse(string sql, string match)
        {
            using var s = _db.Prepare(sql);
            s.BindAll(match);
            var rank = 0;
            while (s.Step())
            {
                var id = s.Int64(0);
                scores[id] = scores.GetValueOrDefault(id) + 1.0 / (RrfK + ++rank);
            }
        }

        Fuse("SELECT rowid FROM fts WHERE fts MATCH ? ORDER BY bm25(fts) LIMIT " + PerList.ToString(CultureInfo.InvariantCulture), wordQuery);
        if (triWords.Count > 0)
        {
            Fuse("SELECT rowid FROM fts_tri WHERE fts_tri MATCH ? ORDER BY bm25(fts_tri) LIMIT " + PerList.ToString(CultureInfo.InvariantCulture), string.Join(' ', triWords.Select(Quote)));
        }

        var fromMs = from?.ToUnixTimeMilliseconds() ?? long.MinValue;
        var toMs = to?.ToUnixTimeMilliseconds() ?? long.MaxValue;
        var best = new Dictionary<(long Start, int Track), (long Chunk, long End, double Score)>();
        foreach (var (id, score) in scores)
        {
            using var s = _db.Prepare("SELECT start_ms, end_ms, track FROM chunk WHERE id = ?");
            s.BindAll(id);
            if (!s.Step())
            {
                continue;
            }

            var (start, end, track) = (s.Int64(0), s.Int64(1), (int)s.Int64(2));
            if (start < fromMs || start >= toMs)
            {
                continue;
            }

            if (!best.TryGetValue((start, track), out var b) || score > b.Score)
            {
                best[(start, track)] = (id, end, score);
            }
        }

        return
        [
            .. best.OrderByDescending(kv => kv.Value.Score).ThenByDescending(kv => kv.Key.Start).Take(limit).Select(kv => new SearchHit(
                DateTimeOffset.FromUnixTimeMilliseconds(kv.Key.Start),
                DateTimeOffset.FromUnixTimeMilliseconds(kv.Value.End),
                kv.Key.Track,
                Snippet(kv.Value.Chunk, wordQuery, triWords),
                kv.Value.Score) { Links = LinksOf(kv.Key.Start, kv.Key.Track) }),
        ];
    }

    private List<string> LinksOf(long start, int track)
    {
        var links = new List<string>();
        using var s = _db.Prepare("SELECT url FROM link WHERE start_ms = ? AND track = ? ORDER BY ord");
        s.BindAll(start, track);
        while (s.Step())
        {
            links.Add(s.Text(0) ?? string.Empty);
        }

        return links;
    }

    /// <summary>C21.4: forget activities that started in [from, to); returns how many. The vault forgets its records separately.</summary>
    public int Forget(DateTimeOffset from, DateTimeOffset to)
    {
        var (a, b) = (from.ToUnixTimeMilliseconds(), to.ToUnixTimeMilliseconds());
        var activities = (int)_db.Scalar("SELECT count(DISTINCT start_ms || ':' || track) FROM chunk WHERE start_ms >= ? AND start_ms < ?", a, b);
        _db.Transaction(() =>
        {
            _db.Run("DELETE FROM fts WHERE rowid IN (SELECT id FROM chunk WHERE start_ms >= ? AND start_ms < ?)", a, b);
            _db.Run("DELETE FROM fts_tri WHERE rowid IN (SELECT id FROM chunk WHERE start_ms >= ? AND start_ms < ?)", a, b);
            _db.Run("DELETE FROM chunk WHERE start_ms >= ? AND start_ms < ?", a, b);
            _db.Run("DELETE FROM link WHERE start_ms >= ? AND start_ms < ?", a, b);
        });
        LoadRecent(); // a forgotten chunk must not suppress new text
        return activities;
    }

    /// <summary>
    /// C21.4: forget every activity that included <paramref name="app"/> (an exe glob) or <paramref name="host"/> (and its
    /// subdomains), whole; returns how many. The vault forgets its records separately.
    /// </summary>
    public int Forget(string? app, string? host)
    {
        if (app is null && host is null)
        {
            return 0;
        }

        var ids = new List<long>();
        var activities = new HashSet<(long, long)>();
        using (var s = _db.Prepare("SELECT id, start_ms, track, sources FROM chunk WHERE sources <> ''"))
        {
            while (s.Step())
            {
                var hit = false;
                foreach (var source in (s.Text(3) ?? string.Empty).Split('\n'))
                {
                    hit |= (app is not null && source.StartsWith("app:", StringComparison.Ordinal) && Core.Apps.Glob.IsMatch(app, source[4..]))
                        || (host is not null && source.StartsWith("host:", StringComparison.Ordinal) && Recording.RecordSource.HostMatches(host, source[5..]));
                }

                if (hit)
                {
                    ids.Add(s.Int64(0));
                    activities.Add((s.Int64(1), s.Int64(2)));
                }
            }
        }

        _db.Transaction(() =>
        {
            foreach (var id in ids)
            {
                _db.Run("DELETE FROM fts WHERE rowid = ?", id);
                _db.Run("DELETE FROM fts_tri WHERE rowid = ?", id);
                _db.Run("DELETE FROM chunk WHERE id = ?", id);
            }

            foreach (var (start, track) in activities)
            {
                _db.Run("DELETE FROM link WHERE start_ms = ? AND track = ?", start, track);
            }
        });
        LoadRecent();
        return activities.Count;
    }

    public void Dispose() => _db.Dispose();

    private string Snippet(long chunk, string wordQuery, IReadOnlyList<string> triWords)
    {
        using (var s = _db.Prepare("SELECT snippet(fts, 0, '[', ']', '…', 12) FROM fts WHERE fts MATCH ? AND rowid = ?"))
        {
            s.BindAll(wordQuery, chunk);
            if (s.Step() && s.Text(0) is { Length: > 0 } snip)
            {
                return snip;
            }
        }

        // Matched by part of a word only: cut around the first part, with it bracketed.
        using var t = _db.Prepare("SELECT text FROM fts WHERE rowid = ?");
        t.BindAll(chunk);
        var text = t.Step() ? t.Text(0) ?? string.Empty : string.Empty;
        var at = triWords.Select(w => text.IndexOf(w, StringComparison.OrdinalIgnoreCase)).Where(i => i >= 0).DefaultIfEmpty(-1).Min();
        if (at < 0)
        {
            return text.Length <= 120 ? text : text[..120] + "…";
        }

        var len = triWords.First(w => text.IndexOf(w, StringComparison.OrdinalIgnoreCase) == at).Length;
        var start = Math.Max(0, at - 60);
        var end = Math.Min(text.Length, at + len + 60);
        return (start > 0 ? "…" : string.Empty) + text[start..at] + "[" + text[at..(at + len)] + "]" + text[(at + len)..end] + (end < text.Length ? "…" : string.Empty);
    }

    /// <summary>~<see cref="ChunkTarget"/>-character chunks ending at whitespace, each starting ~15% back into the previous one.</summary>
    internal static IEnumerable<string> Chunks(string text)
    {
        var start = 0;
        while (start < text.Length)
        {
            if (text.Length - start <= ChunkMax)
            {
                yield return text[start..].Trim();
                yield break;
            }

            var end = text.LastIndexOfAny([' ', '\n'], start + ChunkTarget);
            if (end <= start + ChunkTarget / 2)
            {
                end = Math.Min(text.Length, start + ChunkMax); // no break in sight: cut hard
            }

            yield return text[start..end].Trim();
            var back = end - (int)((end - start) * Overlap);
            var next = text.IndexOfAny([' ', '\n'], back);
            start = next < 0 || next >= end ? end : next + 1;
        }
    }

    /// <summary>SimHash-64 over the chunk's lower-cased words.</summary>
    internal static ulong SimHash(string text)
    {
        Span<int> votes = stackalloc int[64];
        foreach (Match m in Words().Matches(text))
        {
            var h = XxHash64.Hash(m.Value.ToLowerInvariant());
            for (var b = 0; b < 64; b++)
            {
                votes[b] += ((h >> b) & 1) == 1 ? 1 : -1;
            }
        }

        ulong result = 0;
        for (var b = 0; b < 64; b++)
        {
            if (votes[b] > 0)
            {
                result |= 1UL << b;
            }
        }

        return result;
    }

    private static string Quote(string word) => "\"" + word.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    [GeneratedRegex(@"[\p{L}\p{N}_]+", RegexOptions.CultureInvariant)]
    private static partial Regex Words();
}
