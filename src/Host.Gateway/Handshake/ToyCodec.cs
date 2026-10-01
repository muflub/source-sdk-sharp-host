using System.Buffers.Binary;
using System.Text;

namespace SourceSharp.Host.Gateway.Handshake;

/// <summary>
/// The toy protocol's handshake (plan §7.6; its reference is <c>Host.FakeClient.ToyWire</c>, which the
/// gateway does not reference: facts prove the two agree byte for byte).
/// <code>
/// q  FF FF FF FF 'q' clientChallenge:i32
/// A  FF FF FF FF 'A' challenge:i32 clientChallenge:i32
/// k  FF FF FF FF 'k' challenge:i32 ticketLen:u16 ticket["TOYT" steamId:u64] name 00
/// 9  FF FF FF FF '9' reason
/// </code>
/// </summary>
public sealed class ToyCodec : IHandshakeCodec
{
    public string Name => "Toy";

    static bool Oob(ReadOnlySpan<byte> p, byte type) =>
        p.Length >= 5 && BinaryPrimitives.ReadUInt32LittleEndian(p) == 0xFFFFFFFF && p[4] == type;

    public bool IsGetChallenge(ReadOnlySpan<byte> p) => Oob(p, (byte)'q') && p.Length >= 9;
    public bool IsChallenge(ReadOnlySpan<byte> p) => Oob(p, (byte)'A') && p.Length >= 13;

    public bool TryReadConnect(ReadOnlySpan<byte> p, out int challenge, out ulong? steamId)
    {
        challenge = 0; steamId = null;
        if (!Oob(p, (byte)'k') || p.Length < 11) return false;
        challenge = BinaryPrimitives.ReadInt32LittleEndian(p[5..]);
        int len = BinaryPrimitives.ReadUInt16LittleEndian(p[9..]);
        if (p.Length < 11 + len) return true;
        var ticket = p.Slice(11, len);
        if (len >= 12 && ticket[..4].SequenceEqual("TOYT"u8))
            steamId = BinaryPrimitives.ReadUInt64LittleEndian(ticket[4..]);
        return true;
    }

    public byte[] Challenge(int challenge, ReadOnlySpan<byte> getChallenge)
    {
        var b = new byte[13];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 0xFFFFFFFF);
        b[4] = (byte)'A';
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(5), challenge);
        getChallenge.Slice(5, 4).CopyTo(b.AsSpan(9));
        return b;
    }

    public byte[] Reject(ReadOnlySpan<byte> connect, string reason)
    {
        var t = Encoding.UTF8.GetBytes(reason);
        var b = new byte[5 + t.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 0xFFFFFFFF);
        b[4] = (byte)'9';
        t.CopyTo(b.AsSpan(5));
        return b;
    }
}
