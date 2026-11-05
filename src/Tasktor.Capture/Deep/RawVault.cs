using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace Tasktor.Capture.Deep;

/// <summary>One decrypted vault record.</summary>
public sealed record VaultRecord(DateTimeOffset At, byte Kind, byte[] Body);

/// <summary>
/// D-C22 raw store: one file per UTC day, each with its own random 256-bit key, wrapped (AES-GCM) by the data key that
/// DPAPI unwraps at start. Every record is AES-256-GCM with nonce = day salt ‖ sequence and its header as associated
/// data, so a tampered or moved record fails. Kept 7 days. Dropping a day zeroes and deletes its key <b>before</b> its
/// file; forget by time range rewrites the affected days with fresh sequence numbers (a nonce is never reused).
/// One writer (the vault thread).
/// </summary>
public sealed class RawVault
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private const int HeaderSize = 4 + 8 + 8 + 1;
    private const int TagSize = 16;

    private readonly string _dir;
    private readonly byte[] _dataKey;
    private readonly Dictionary<string, Day> _days = new(StringComparer.Ordinal);

    public RawVault(string dir, ReadOnlySpan<byte> dataKey)
    {
        if (dataKey.Length != 32)
        {
            throw new ArgumentException("the data key is 32 bytes", nameof(dataKey));
        }

        _dir = dir;
        _dataKey = dataKey.ToArray();
        Directory.CreateDirectory(dir);
    }

    /// <summary>Records that failed authentication (tampered) or were cut off (a crash mid-write) on the last reads.</summary>
    public long Tampered { get; private set; }

    public long Truncated { get; private set; }

    private sealed class Day
    {
        public required byte[] Key { get; init; }

        public required byte[] Salt { get; init; }

        public ulong NextSeq { get; set; }
    }

    private static string DayName(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private string VaultPath(string day) => Path.Combine(_dir, day + ".vault");

    private string KeyPath(string day) => Path.Combine(_dir, day + ".key");

    public IReadOnlyList<string> DayFiles() =>
        [.. Directory.EnumerateFiles(_dir, "*.vault").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order(StringComparer.Ordinal)];

    private Day Open(string day, bool create)
    {
        if (_days.TryGetValue(day, out var d))
        {
            return d;
        }

        var keyPath = KeyPath(day);
        byte[] key, salt;
        if (File.Exists(keyPath))
        {
            var blob = File.ReadAllBytes(keyPath);
            salt = blob[..4];
            key = new byte[32];
            using var gcm = new AesGcm(_dataKey, TagSize);
            gcm.Decrypt(blob.AsSpan(4, 12), blob.AsSpan(16, 32), blob.AsSpan(48, TagSize), key, salt);
        }
        else if (create)
        {
            key = RandomNumberGenerator.GetBytes(32);
            salt = RandomNumberGenerator.GetBytes(4);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var wrapped = new byte[32];
            var tag = new byte[TagSize];
            using (var gcm = new AesGcm(_dataKey, TagSize))
            {
                gcm.Encrypt(nonce, key, wrapped, tag, salt);
            }

            Core.Storage.AtomicFile.Write(keyPath, [.. salt, .. nonce, .. wrapped, .. tag]);
        }
        else
        {
            throw new FileNotFoundException("no key for " + day);
        }

        d = new Day { Key = key, Salt = salt, NextSeq = NextSeqOf(day) };
        _days[day] = d;
        return d;
    }

    /// <summary>One past the highest sequence in the day file, from the headers alone (no decryption, no counting).</summary>
    private ulong NextSeqOf(string day)
    {
        var path = VaultPath(day);
        if (!File.Exists(path))
        {
            return 0;
        }

        var bytes = File.ReadAllBytes(path);
        ulong next = 0;
        for (var pos = 0; pos + HeaderSize <= bytes.Length;)
        {
            var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos));
            if (len < 0 || pos + HeaderSize + len + TagSize > bytes.Length)
            {
                break;
            }

            next = Math.Max(next, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(pos + 4)) + 1);
            pos += HeaderSize + len + TagSize;
        }

        return next;
    }

    public void Append(DateTimeOffset at, byte kind, ReadOnlySpan<byte> body)
    {
        var day = DayName(at);
        var d = Open(day, create: true);
        var record = Seal(d, d.NextSeq++, at, kind, body);
        using var fs = new FileStream(VaultPath(day), FileMode.Append, FileAccess.Write, FileShare.Read);
        fs.Write(record);
        fs.Flush(flushToDisk: true);
    }

    private static byte[] Seal(Day d, ulong seq, DateTimeOffset at, byte kind, ReadOnlySpan<byte> body)
    {
        var record = new byte[HeaderSize + body.Length + TagSize];
        var header = record.AsSpan(0, HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)body.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(header[4..], seq);
        BinaryPrimitives.WriteInt64LittleEndian(header[12..], at.ToUnixTimeMilliseconds());
        header[20] = kind;
        using var gcm = new AesGcm(d.Key, TagSize);
        gcm.Encrypt(Nonce(d.Salt, seq), body, record.AsSpan(HeaderSize, body.Length), record.AsSpan(HeaderSize + body.Length, TagSize), header);
        return record;
    }

    private static byte[] Nonce(byte[] salt, ulong seq)
    {
        var nonce = new byte[12];
        salt.CopyTo(nonce, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(nonce.AsSpan(4), seq);
        return nonce;
    }

    /// <summary>Records of one day file: (seq, record, plaintext or null when tampered).</summary>
    private IEnumerable<(ulong Seq, VaultRecord? Record, bool Ok)> Scan(string day, Day d)
    {
        var path = VaultPath(day);
        if (!File.Exists(path))
        {
            yield break;
        }

        var bytes = File.ReadAllBytes(path);
        var pos = 0;
        using var gcm = new AesGcm(d.Key, TagSize);
        while (pos + HeaderSize <= bytes.Length)
        {
            var header = bytes.AsSpan(pos, HeaderSize);
            var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (len < 0 || pos + HeaderSize + len + TagSize > bytes.Length)
            {
                Truncated++;
                yield break; // a crash mid-append: the tail is ignored
            }

            var seq = BinaryPrimitives.ReadUInt64LittleEndian(header[4..]);
            var at = DateTimeOffset.FromUnixTimeMilliseconds(BinaryPrimitives.ReadInt64LittleEndian(header[12..]));
            var kind = header[20];
            var body = new byte[len];
            var ok = true;
            try
            {
                gcm.Decrypt(Nonce(d.Salt, seq), bytes.AsSpan(pos + HeaderSize, len), bytes.AsSpan(pos + HeaderSize + len, TagSize), body, bytes.AsSpan(pos, HeaderSize));
            }
            catch (AuthenticationTagMismatchException)
            {
                Tampered++;
                ok = false;
            }

            yield return (seq, ok ? new VaultRecord(at, kind, body) : null, ok);
            pos += HeaderSize + len + TagSize;
        }

        if (pos < bytes.Length)
        {
            Truncated++; // a torn header at the end
        }
    }

    /// <summary>Records in [from, to), oldest first; tampered records are skipped and counted.</summary>
    public IEnumerable<VaultRecord> Read(DateTimeOffset from, DateTimeOffset to)
    {
        foreach (var day in DayFiles())
        {
            if (string.CompareOrdinal(day, DayName(from)) < 0 || string.CompareOrdinal(day, DayName(to)) > 0 || !File.Exists(KeyPath(day)))
            {
                continue;
            }

            foreach (var (_, r, ok) in Scan(day, Open(day, create: false)))
            {
                if (ok && r!.At >= from && r.At < to)
                {
                    yield return r;
                }
            }
        }
    }

    /// <summary>Retention: drop every day older than 7 days (key first, then file).</summary>
    public int Purge(DateTimeOffset now)
    {
        var cutoff = DayName(now - Retention);
        var dropped = 0;
        foreach (var day in DayFiles().Concat(Directory.EnumerateFiles(_dir, "*.key").Select(Path.GetFileNameWithoutExtension).OfType<string>()).Distinct().ToList())
        {
            if (string.CompareOrdinal(day, cutoff) < 0)
            {
                DropDay(day);
                dropped++;
            }
        }

        return dropped;
    }

    /// <summary>Zero and delete the day's key, then its file: once the key is gone nothing in the file is readable.</summary>
    public void DropDay(string day)
    {
        if (_days.Remove(day, out var d))
        {
            CryptographicOperations.ZeroMemory(d.Key);
        }

        var keyPath = KeyPath(day);
        if (File.Exists(keyPath))
        {
            File.WriteAllBytes(keyPath, new byte[new FileInfo(keyPath).Length]);
            File.Delete(keyPath);
        }

        File.Delete(VaultPath(day));
    }

    /// <summary>C3.4 / D-G6 "forget the last hour": remove every record in [from, to), rewriting affected days.</summary>
    public int Forget(DateTimeOffset from, DateTimeOffset to)
    {
        var removed = 0;
        foreach (var day in DayFiles().ToList())
        {
            if (string.CompareOrdinal(day, DayName(from)) < 0 || string.CompareOrdinal(day, DayName(to)) > 0 || !File.Exists(KeyPath(day)))
            {
                continue;
            }

            var d = Open(day, create: false);
            var keep = new List<VaultRecord>();
            foreach (var (_, r, ok) in Scan(day, d).ToList())
            {
                if (!ok)
                {
                    continue; // tampered records are dropped on a rewrite
                }

                if (r!.At >= from && r.At < to)
                {
                    removed++;
                }
                else
                {
                    keep.Add(r);
                }
            }

            using var ms = new MemoryStream();
            foreach (var r in keep)
            {
                ms.Write(Seal(d, d.NextSeq++, r.At, r.Kind, r.Body)); // fresh sequence numbers: no nonce reuse
            }

            Core.Storage.AtomicFile.Write(VaultPath(day), ms.ToArray());
        }

        return removed;
    }

    public void ForgetAll()
    {
        foreach (var day in Directory.EnumerateFiles(_dir, "*.key").Select(Path.GetFileNameWithoutExtension).OfType<string>().ToList())
        {
            DropDay(day);
        }

        foreach (var f in Directory.EnumerateFiles(_dir, "*.vault"))
        {
            File.Delete(f);
        }
    }
}
