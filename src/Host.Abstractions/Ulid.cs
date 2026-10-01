using System.Security.Cryptography;

namespace SourceSharp.Host.Abstractions;

/// <summary>ULIDs (48-bit ms time + 80 random bits, Crockford base32): sortable by creation, unique without a counter.</summary>
public static class Ulid
{
    const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string New(TimeProvider clock) => New(clock.GetUtcNow());

    public static string New(DateTimeOffset at)
    {
        Span<byte> bytes = stackalloc byte[16];
        var ms = (ulong)at.ToUnixTimeMilliseconds();
        for (var i = 5; i >= 0; i--) { bytes[i] = (byte)ms; ms >>= 8; }
        RandomNumberGenerator.Fill(bytes[6..]);

        // 128 bits → 26 chars, 5 bits each, most significant first (the top char carries 3 bits).
        Span<char> chars = stackalloc char[26];
        var hi = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(bytes);
        var lo = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);
        var value = new UInt128(hi, lo);
        for (var i = 25; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(value & 31)];
            value >>= 5;
        }
        return new string(chars);
    }
}
