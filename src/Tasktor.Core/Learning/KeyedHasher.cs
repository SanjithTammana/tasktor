using System.Security.Cryptography;
using System.Text;

namespace Tasktor.Core.Learning;

/// <summary>
/// A keyed hash per domain (B D-B10, U1 D-H8): the domain key is HKDF-SHA256(install key, info = "tasktor/hash/" + domain),
/// never the same output used for an encryption key. Outputs are truncated HMAC-SHA256 and cannot be reversed to a name.
/// </summary>
public sealed class KeyedHasher
{
    private readonly byte[] _key;

    public KeyedHasher(ReadOnlySpan<byte> installKey, string domain)
    {
        ArgumentException.ThrowIfNullOrEmpty(domain);
        if (installKey.Length < 16)
        {
            throw new ArgumentException("install key too short", nameof(installKey));
        }

        Domain = domain;
        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, installKey.ToArray(), 32, salt: null, info: Encoding.UTF8.GetBytes("tasktor/hash/" + domain));
    }

    public string Domain { get; }

    /// <summary>HMAC-SHA256 of the UTF-8 text, truncated to 16 bytes.</summary>
    public KindId Hash(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(text), mac);
        return KindId.FromBytes(mac[..16]);
    }
}

/// <summary>A 16-byte keyed-hash id (B D-B10: job kinds, A5 volumes, A6 apps). Hex in files; never a name.</summary>
public readonly record struct KindId(ulong Hi, ulong Lo)
{
    public static KindId FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 16)
        {
            throw new ArgumentException("16 bytes expected", nameof(bytes));
        }

        return new KindId(System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(bytes), System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]));
    }

    public void WriteTo(Span<byte> dest)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(dest, Hi);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(dest[8..], Lo);
    }

    public override string ToString() => Hi.ToString("x16", System.Globalization.CultureInfo.InvariantCulture) + Lo.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);

    public static bool TryParse(string? hex, out KindId id)
    {
        id = default;
        if (hex is null || hex.Length != 32)
        {
            return false;
        }

        try
        {
            id = FromBytes(Convert.FromHexString(hex));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
