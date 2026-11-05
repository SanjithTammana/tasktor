using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tasktor.Core.Learning;

/// <summary>The on-disk form: numbers only, keyed by hashed ids (B D-B10), with the install key's fingerprint.</summary>
public sealed record KindStoreFile
{
    public int SchemaVersion { get; init; } = 1;

    public required string Fingerprint { get; init; }

    public Dictionary<string, string> Kinds { get; init; } = new(StringComparer.Ordinal);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = false)]
[JsonSerializable(typeof(KindStoreFile))]
internal sealed partial class KindStoreJsonContext : JsonSerializerContext;

/// <summary>
/// Per-key learned statistics (<see cref="KindStats"/>), bounded to 256 keys with least-recently-used eviction (B P5:
/// ≤ 100 KB), persisted as numbers only. A file written under a different install key is discarded (all fallbacks). Loop-only.
/// </summary>
public sealed class KindStore
{
    public const int MaxKinds = 256;

    private readonly Dictionary<KindId, KindStats> _kinds = [];
    private readonly string _fingerprint;

    public KindStore(string fingerprint) => _fingerprint = fingerprint;

    public int Count => _kinds.Count;

    public bool Dirty { get; private set; }

    public bool LearningOn { get; set; } = true;

    public IEnumerable<KeyValuePair<KindId, KindStats>> All => _kinds;

    public KindStats? Find(KindId id) => _kinds.GetValueOrDefault(id);

    /// <summary>The stats for <paramref name="id"/>, created if new; the least recently seen kind is evicted past 256.</summary>
    public KindStats Get(KindId id, DateTimeOffset now)
    {
        if (!_kinds.TryGetValue(id, out var s))
        {
            if (_kinds.Count >= MaxKinds)
            {
                var oldest = _kinds.MinBy(kv => kv.Value.LastSeenUtc).Key;
                _kinds.Remove(oldest);
            }

            s = new KindStats();
            _kinds[id] = s;
        }

        s.LastSeenUtc = now;
        Dirty = true;
        return s;
    }

    public void MarkDirty() => Dirty = true;

    public bool Forget(KindId id)
    {
        Dirty = true;
        return _kinds.Remove(id);
    }

    public void ForgetAll()
    {
        _kinds.Clear();
        Dirty = true;
    }

    public byte[] Serialize()
    {
        Dirty = false;
        var file = new KindStoreFile { Fingerprint = _fingerprint };
        foreach (var kv in _kinds)
        {
            file.Kinds[kv.Key.ToString()] = Convert.ToBase64String(kv.Value.ToBytes());
        }

        return JsonSerializer.SerializeToUtf8Bytes(file, KindStoreJsonContext.Default.KindStoreFile);
    }

    /// <summary>Load; a corrupt file or one from another install key is discarded (returns false).</summary>
    public bool Load(byte[]? bytes)
    {
        _kinds.Clear();
        if (bytes is null || bytes.Length == 0)
        {
            return true;
        }

        try
        {
            var file = JsonSerializer.Deserialize(bytes, KindStoreJsonContext.Default.KindStoreFile);
            if (file is null || file.Fingerprint != _fingerprint)
            {
                return false;
            }

            foreach (var kv in file.Kinds)
            {
                if (KindId.TryParse(kv.Key, out var id))
                {
                    _kinds[id] = KindStats.FromBytes(Convert.FromBase64String(kv.Value));
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            _kinds.Clear();
            return false;
        }
    }
}
