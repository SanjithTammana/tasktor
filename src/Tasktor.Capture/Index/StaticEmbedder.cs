using System.Globalization;
using System.Text;

namespace Tasktor.Capture.Index;

/// <summary>
/// C21.2 within the owner's 1 MB model budget (2026-11-09): a static embedding table (Model2Vec-style, pruned and int8 by
/// <c>tasktor_worker convert-static</c>) used in process. A text's vector is the mean of its WordPiece tokens' rows
/// (special tokens skipped), L2-normalised, then int8 with one scale (the index's format). No Python, no ONNX, no
/// network; the table (about 1 MB) is the only memory it holds. Matches <c>tasktor_worker.embed_static.StaticEncoder</c>
/// (parity fixture). Used only with a passing G8 report.
/// </summary>
public sealed class StaticEmbedder : IChunkEmbedder
{
    public const int MaxTokens = 4096;
    private static readonly string[] Specials = ["[PAD]", "[UNK]", "[CLS]", "[SEP]", "[MASK]"];

    // The worker's FOLD table (embed.py): Latin letters U+00C0–U+024F to their base letter, the same characters exactly.
    private const string FoldFrom = "ÀÁÂÃÄÅÇÈÉÊËÌÍÎÏÑÒÓÔÕÖÙÚÛÜÝàáâãäåçèéêëìíîïñòóôõöùúûüýÿĀāĂăĄąĆćĈĉĊċČčĎďĒēĔĕĖėĘęĚěĜĝĞğĠġĢģĤĥĨĩĪīĬĭĮįİĴĵĶķĹĺĻļĽľŃńŅņŇňŌōŎŏŐőŔŕŖŗŘřŚśŜŝŞşŠšŢţŤťŨũŪūŬŭŮůŰűŲųŴŵŶŷŸŹźŻżŽžƠơƯưǍǎǏǐǑǒǓǔǕǖǗǘǙǚǛǜǞǟǠǡǦǧǨǩǪǫǬǭǰǴǵǸǹǺǻȀȁȂȃȄȅȆȇȈȉȊȋȌȍȎȏȐȑȒȓȔȕȖȗȘșȚțȞȟȦȧȨȩȪȫȬȭȮȯȰȱȲȳ";
    private const string FoldTo = "AAAAAACEEEEIIIINOOOOOUUUUYaaaaaaceeeeiiiinooooouuuuyyAaAaAaCcCcCcCcDdEeEeEeEeEeGgGgGgGgHhIiIiIiIiIJjKkLlLlLlNnNnNnOoOoOoRrRrRrSsSsSsSsTtTtUuUuUuUuUuUuWwYyYZzZzZzOoUuAaIiOoUuUuUuUuUuAaAaGgKkOoOojGgNnAaAaAaEeEeIiIiOoOoRrRrUuUuSsTtHhAaEeOoOoOoOoYy";
    private static readonly Dictionary<char, char> Fold = FoldFrom.Zip(FoldTo).ToDictionary(p => p.First, p => p.Second);
    private readonly Dictionary<string, int> _vocab;
    private readonly float[] _scale;
    private readonly sbyte[] _table;
    private readonly int _unk;
    private readonly HashSet<int> _skip;

    public StaticEmbedder(IReadOnlyList<string> vocab, float[] scale, sbyte[] table, int dims)
    {
        ArgumentNullException.ThrowIfNull(vocab);
        ArgumentNullException.ThrowIfNull(scale);
        ArgumentNullException.ThrowIfNull(table);
        if (dims <= 0 || scale.Length != vocab.Count || table.Length != vocab.Count * dims) throw new ArgumentException("A malformed embedding table.");
        Dims = dims;
        _scale = scale;
        _table = table;
        _vocab = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < vocab.Count; i++) _vocab.TryAdd(vocab[i], i);
        _unk = _vocab.GetValueOrDefault("[UNK]");
        _skip = [.. Specials.Where(_vocab.ContainsKey).Select(s => _vocab[s])];
    }

    public int Dims { get; }

    public int Rows => _scale.Length;

    /// <summary>The embedder from <c>static.i8</c> + <c>vocab.txt</c>, or null when either is missing or malformed.</summary>
    public static StaticEmbedder? Load(string folder)
    {
        try
        {
            var raw = File.ReadAllBytes(Path.Combine(folder, "static.i8"));
            if (raw.Length < 12 || raw[0] != (byte)'T' || raw[1] != (byte)'S' || raw[2] != (byte)'E' || raw[3] != (byte)'1') return null;
            var rows = BitConverter.ToInt32(raw, 4);
            var dims = BitConverter.ToInt32(raw, 8);
            if (rows <= 0 || dims <= 0 || dims > 4096 || raw.Length != 12 + (rows * 4) + ((long)rows * dims)) return null;
            var scale = new float[rows];
            Buffer.BlockCopy(raw, 12, scale, 0, rows * 4);
            var table = new sbyte[rows * dims];
            Buffer.BlockCopy(raw, 12 + (rows * 4), table, 0, rows * dims);
            var vocab = File.ReadAllText(Path.Combine(folder, "vocab.txt"), Encoding.UTF8).Split('\n');
            return vocab.Length < rows ? null : new StaticEmbedder(vocab[..rows], scale, table, dims);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The embedder when the folder holds a table and a passing G8 report; else null (search stays words only).</summary>
    public static StaticEmbedder? TryCreate(string folder) =>
        WorkerEmbedder.G8Passed(Path.Combine(folder, "g8.json")) ? Load(folder) : null;

    public Task<IReadOnlyList<(long Id, sbyte[] Q, float S)>?> EmbedAsync(IReadOnlyList<(long Id, string Text)> items, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(items);
        var list = new List<(long, sbyte[], float)>(items.Count);
        foreach (var (id, text) in items)
        {
            cancel.ThrowIfCancellationRequested();
            var v = Vector(text);
            var max = v.Max(MathF.Abs);
            if (max <= 0) continue; // no known token: no vector (the chunk stays words only)
            var s = max / 127f;
            list.Add((id, [.. v.Select(x => (sbyte)Math.Clamp(MathF.Round(x / s), -127, 127))], s));
        }

        return Task.FromResult<IReadOnlyList<(long, sbyte[], float)>?>(list);
    }

    /// <summary>The unit vector of a text (zeros when no token is known).</summary>
    public float[] Vector(string text)
    {
        var sum = new double[Dims];
        var n = 0;
        foreach (var id in Tokens(text ?? string.Empty))
        {
            if (_skip.Contains(id)) continue;
            var s = _scale[id];
            var row = id * Dims;
            for (var d = 0; d < Dims; d++) sum[d] += _table[row + d] * s;
            if (++n >= MaxTokens) break;
        }

        var v = new float[Dims];
        if (n == 0) return v;
        double norm = 0;
        for (var d = 0; d < Dims; d++)
        {
            sum[d] /= n;
            norm += sum[d] * sum[d];
        }

        norm = Math.Sqrt(norm);
        for (var d = 0; d < Dims; d++) v[d] = norm > 0 ? (float)(sum[d] / norm) : 0f;
        return v;
    }

    /// <summary>BERT uncased: lower-case, strip accents, split on whitespace and punctuation, greedy longest word pieces.</summary>
    public IEnumerable<int> Tokens(string text)
    {
        foreach (var word in Words(text))
        {
            if (word.Length > 100)
            {
                yield return _unk;
                continue;
            }

            var pieces = new List<int>();
            var start = 0;
            var ok = true;
            while (start < word.Length)
            {
                var end = word.Length;
                var found = -1;
                while (start < end)
                {
                    var piece = (start > 0 ? "##" : string.Empty) + word[start..end];
                    if (_vocab.TryGetValue(piece, out var id))
                    {
                        found = id;
                        break;
                    }

                    end--;
                }

                if (found < 0)
                {
                    ok = false;
                    break;
                }

                pieces.Add(found);
                start = end;
            }

            if (!ok)
            {
                yield return _unk;
                continue;
            }

            foreach (var p in pieces) yield return p;
        }
    }

    private static List<string> Words(string text)
    {
        var words = new List<string>();
        var cur = new StringBuilder();
        foreach (var raw in text.ToLowerInvariant())
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(raw);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            var c = Fold.GetValueOrDefault(raw, raw);
            if (char.IsWhiteSpace(c))
            {
                Flush();
            }
            else if (IsPunctuation(cat) || (c < 128 && !char.IsLetterOrDigit(c)))
            {
                Flush();
                words.Add(c.ToString());
            }
            else
            {
                cur.Append(c);
            }
        }

        Flush();
        return words;

        void Flush()
        {
            if (cur.Length > 0)
            {
                words.Add(cur.ToString());
                cur.Clear();
            }
        }
    }

    private static bool IsPunctuation(UnicodeCategory c) => c is UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation
        or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation
        or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation;

    public override string ToString() => "static embedder (" + Rows.ToString(CultureInfo.InvariantCulture) + " × " + Dims.ToString(CultureInfo.InvariantCulture) + ")";
}
