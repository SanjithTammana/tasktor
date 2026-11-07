using System.Security.Cryptography;
using System.Text;

namespace Tasktor.Core.Storage;

/// <summary>How the install key is protected at rest: DPAPI (current user) on Windows; a test provider elsewhere.</summary>
public sealed record KeyProtection(Func<byte[], byte[]> Protect, Func<byte[], byte[]> Unprotect, string Name)
{
    /// <summary>No protection: for tests and non-Windows development only.</summary>
    public static KeyProtection None { get; } = new(b => b, b => b, "none");
}

/// <summary>
/// <c>state\install.key</c> (U1 D-H8): 32 random bytes, protected at rest. Hash keys and encryption keys are separate HKDF
/// outputs (info <c>tasktor/hash/…</c> vs <c>tasktor/enc/…</c>), so no key is ever used for both.
/// An unreadable key is replaced: everything learned becomes new (B6 edge case), which is the safe direction.
/// </summary>
public sealed class InstallKey
{
    private readonly byte[] _key;

    private InstallKey(byte[] key, bool created)
    {
        _key = key;
        Created = created;
    }

    /// <summary>True when this run created (or replaced) the key.</summary>
    public bool Created { get; }

    public static InstallKey LoadOrCreate(StoreLayout layout, KeyProtection protection)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(protection);
        if (File.Exists(layout.InstallKey))
        {
            try
            {
                var key = protection.Unprotect(File.ReadAllBytes(layout.InstallKey));
                if (key.Length == 32)
                {
                    return new InstallKey(key, created: false);
                }
            }
            catch (CryptographicException)
            {
            }
        }

        var fresh = RandomNumberGenerator.GetBytes(32);
        AtomicFile.Write(layout.InstallKey, protection.Protect(fresh));
        return new InstallKey(fresh, created: true);
    }

    /// <summary>For tests: a fixed key.</summary>
    public static InstallKey FromBytes(byte[] key) => new(key.Length == 32 ? key : throw new ArgumentException("32 bytes", nameof(key)), false);

    /// <summary>A keyed hasher for <paramref name="domain"/> (<c>job.kind</c>, <c>volume</c>, <c>app</c>, <c>title.token</c> …).</summary>
    public Learning.KeyedHasher Hasher(string domain) => new(_key, domain);

    /// <summary>An encryption key for <paramref name="domain"/> (<c>vault</c>, <c>index</c>), never equal to any hash key.</summary>
    public byte[] EncryptionKey(string domain)
    {
        ArgumentException.ThrowIfNullOrEmpty(domain);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, _key, 32, salt: null, info: Encoding.UTF8.GetBytes("tasktor/enc/" + domain));
    }

    /// <summary>A short fingerprint stored beside learned files to detect a different key (old file discarded).</summary>
    public string Fingerprint() => Convert.ToHexStringLower(HKDF.DeriveKey(HashAlgorithmName.SHA256, _key, 8, salt: null, info: Encoding.UTF8.GetBytes("tasktor/fingerprint")));
}
