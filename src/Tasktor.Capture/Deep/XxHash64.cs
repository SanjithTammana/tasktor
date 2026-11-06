using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Tasktor.Capture.Deep;

/// <summary>
/// XXH64 (Yann Collet's xxHash, 64-bit, seed 0 by default), as C §4's <c>RefMinter</c> specifies. In-repo so the capture
/// host keeps no runtime package dependency; checked against the reference test vectors. Not a security hash.
/// </summary>
public static class XxHash64
{
    private const ulong P1 = 11400714785074694791UL;
    private const ulong P2 = 14029467366897019727UL;
    private const ulong P3 = 1609587929392839161UL;
    private const ulong P4 = 9650029242287828579UL;
    private const ulong P5 = 2870177450012600261UL;

    public static ulong Hash(ReadOnlySpan<byte> data, ulong seed = 0)
    {
        var len = data.Length;
        ulong h;
        var i = 0;
        if (len >= 32)
        {
            var v1 = seed + P1 + P2;
            var v2 = seed + P2;
            var v3 = seed;
            var v4 = seed - P1;
            for (; i <= len - 32; i += 32)
            {
                v1 = Round(v1, BinaryPrimitives.ReadUInt64LittleEndian(data[i..]));
                v2 = Round(v2, BinaryPrimitives.ReadUInt64LittleEndian(data[(i + 8)..]));
                v3 = Round(v3, BinaryPrimitives.ReadUInt64LittleEndian(data[(i + 16)..]));
                v4 = Round(v4, BinaryPrimitives.ReadUInt64LittleEndian(data[(i + 24)..]));
            }

            h = BitOperations.RotateLeft(v1, 1) + BitOperations.RotateLeft(v2, 7) + BitOperations.RotateLeft(v3, 12) + BitOperations.RotateLeft(v4, 18);
            h = Merge(h, v1);
            h = Merge(h, v2);
            h = Merge(h, v3);
            h = Merge(h, v4);
        }
        else
        {
            h = seed + P5;
        }

        h += (ulong)len;
        for (; i <= len - 8; i += 8)
        {
            h ^= Round(0, BinaryPrimitives.ReadUInt64LittleEndian(data[i..]));
            h = (BitOperations.RotateLeft(h, 27) * P1) + P4;
        }

        if (i <= len - 4)
        {
            h ^= BinaryPrimitives.ReadUInt32LittleEndian(data[i..]) * P1;
            h = (BitOperations.RotateLeft(h, 23) * P2) + P3;
            i += 4;
        }

        for (; i < len; i++)
        {
            h ^= data[i] * P5;
            h = BitOperations.RotateLeft(h, 11) * P1;
        }

        h ^= h >> 33;
        h *= P2;
        h ^= h >> 29;
        h *= P3;
        h ^= h >> 32;
        return h;
    }

    /// <summary>The hash of a string's UTF-16 code units (no encoding step, no allocation).</summary>
    public static ulong Hash(string? text, ulong seed = 0) => Hash(MemoryMarshal.AsBytes((text ?? string.Empty).AsSpan()), seed);

    private static ulong Round(ulong acc, ulong lane) => BitOperations.RotateLeft(acc + (lane * P2), 31) * P1;

    private static ulong Merge(ulong h, ulong v) => ((h ^ Round(0, v)) * P1) + P4;
}
