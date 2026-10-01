using System.Buffers.Binary;
using System.Text;

namespace SourceSharp.Host.FakeClient;

/// <summary>
/// The toy protocol (plan §7.6): the smallest thing that looks like Source on the wire in the
/// ways the gateway cares about, so the gateway's code paths are the real ones.
///
/// <para><b>Connectionless packets</b> start with <c>FF FF FF FF</c>, then a type byte, as Source's
/// do (docs/net-protocol.md). The type bytes are Source's: <c>q</c> A2S_GETCHALLENGE,
/// <c>A</c> S2C_CHALLENGE, <c>k</c> C2S_CONNECT, <c>B</c> S2C_CONNECTION, <c>9</c> S2C_CONNREJECT.
/// All integers little-endian.</para>
/// <code>
/// q  getchallenge  FF FF FF FF 'q' clientChallenge:i32
/// A  challenge     FF FF FF FF 'A' challenge:i32 clientChallenge:i32
/// k  connect       FF FF FF FF 'k' challenge:i32 ticketLen:u16 ticket[ticketLen] name:utf8 00
///                  ticket = "TOYT" steamId:u64   (12 bytes; SteamID64 at packet offset 15)
/// B  accept        FF FF FF FF 'B' instanceId:utf8
/// 9  reject        FF FF FF FF '9' reason:utf8
/// </code>
/// <para><b>Netchannel packets</b> are everything that does not start with <c>FF FF FF FF</c>. The toy
/// netchannel's first byte is the message kind (never 0xFF), then a u32 sequence:</para>
/// <code>
/// K  keepalive     'K' seq:u32                    client → server
/// E  echo          'E' seq:u32 instanceId:utf8    server → client (answers K)
/// D  data          'D' seq:u32 payload...         either way, logged only
/// R  retry         'R' seq:u32                    server → client: restart the handshake at the same address
/// </code>
/// </summary>
public static class ToyWire
{
    public const byte GetChallenge = (byte)'q';
    public const byte Challenge = (byte)'A';
    public const byte Connect = (byte)'k';
    public const byte Accept = (byte)'B';
    public const byte Reject = (byte)'9';

    public const byte Keepalive = (byte)'K';
    public const byte Echo = (byte)'E';
    public const byte Data = (byte)'D';
    public const byte Retry = (byte)'R';

    /// <summary>Where the ticket starts in a connect packet.</summary>
    public const int ConnectTicketOffset = 11;
    /// <summary>The SteamID64's offset inside the toy ticket (after the "TOYT" magic).</summary>
    public const int TicketSteamIdOffset = 4;
    /// <summary>The SteamID64's offset in a connect packet whose ticket is readable.</summary>
    public const int ConnectSteamIdOffset = ConnectTicketOffset + TicketSteamIdOffset;
    public static ReadOnlySpan<byte> TicketMagic => "TOYT"u8;

    public static bool IsConnectionless(ReadOnlySpan<byte> p) =>
        p.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(p) == 0xFFFFFFFF;

    /// <summary>The connectionless type byte, or 0 for a netchannel or short packet.</summary>
    public static byte TypeOf(ReadOnlySpan<byte> p) => IsConnectionless(p) && p.Length >= 5 ? p[4] : (byte)0;

    /// <summary>The netchannel kind byte, or 0 for a connectionless or empty packet.</summary>
    public static byte KindOf(ReadOnlySpan<byte> p) => p.Length >= 1 && !IsConnectionless(p) ? p[0] : (byte)0;

    public static byte[] GetChallengePacket(int clientChallenge)
    {
        var b = Header(GetChallenge, 4);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(5), clientChallenge);
        return b;
    }

    public static byte[] ChallengePacket(int challenge, int clientChallenge)
    {
        var b = Header(Challenge, 8);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(5), challenge);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(9), clientChallenge);
        return b;
    }

    public static bool TryReadGetChallenge(ReadOnlySpan<byte> p, out int clientChallenge)
    {
        clientChallenge = 0;
        if (TypeOf(p) != GetChallenge || p.Length < 9) return false;
        clientChallenge = BinaryPrimitives.ReadInt32LittleEndian(p[5..]);
        return true;
    }

    public static bool TryReadChallenge(ReadOnlySpan<byte> p, out int challenge, out int clientChallenge)
    {
        challenge = clientChallenge = 0;
        if (TypeOf(p) != Challenge || p.Length < 13) return false;
        challenge = BinaryPrimitives.ReadInt32LittleEndian(p[5..]);
        clientChallenge = BinaryPrimitives.ReadInt32LittleEndian(p[9..]);
        return true;
    }

    /// <summary>A readable toy ticket: "TOYT" + SteamID64.</summary>
    public static byte[] Ticket(ulong steamId)
    {
        var t = new byte[12];
        TicketMagic.CopyTo(t);
        BinaryPrimitives.WriteUInt64LittleEndian(t.AsSpan(TicketSteamIdOffset), steamId);
        return t;
    }

    /// <summary>A ticket no reader can take a SteamID from (wrong magic, short).</summary>
    public static byte[] UnreadableTicket() => "JUNK?"u8.ToArray();

    public static byte[] ConnectPacket(int challenge, ReadOnlySpan<byte> ticket, string name)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var b = Header(Connect, 4 + 2 + ticket.Length + nameBytes.Length + 1);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(5), challenge);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(9), (ushort)ticket.Length);
        ticket.CopyTo(b.AsSpan(ConnectTicketOffset));
        nameBytes.CopyTo(b.AsSpan(ConnectTicketOffset + ticket.Length));
        return b;
    }

    public static byte[] ConnectPacket(int challenge, ulong steamId, string name) =>
        ConnectPacket(challenge, Ticket(steamId), name);

    /// <summary>Reads a connect packet. <paramref name="steamId"/> is null when the ticket is not a readable toy ticket.</summary>
    public static bool TryReadConnect(ReadOnlySpan<byte> p, out int challenge, out ulong? steamId, out string name)
    {
        challenge = 0; steamId = null; name = "";
        if (TypeOf(p) != Connect || p.Length < ConnectTicketOffset) return false;
        challenge = BinaryPrimitives.ReadInt32LittleEndian(p[5..]);
        int len = BinaryPrimitives.ReadUInt16LittleEndian(p[9..]);
        if (p.Length < ConnectTicketOffset + len) return false;
        var ticket = p.Slice(ConnectTicketOffset, len);
        if (len >= 12 && ticket[..4].SequenceEqual(TicketMagic))
            steamId = BinaryPrimitives.ReadUInt64LittleEndian(ticket[TicketSteamIdOffset..]);
        var rest = p[(ConnectTicketOffset + len)..];
        var nul = rest.IndexOf((byte)0);
        name = Encoding.UTF8.GetString(nul >= 0 ? rest[..nul] : rest);
        return true;
    }

    public static byte[] AcceptPacket(string instanceId) => HeaderWithText(Accept, instanceId);
    public static byte[] RejectPacket(string reason) => HeaderWithText(Reject, reason);

    /// <summary>The text after the type byte of an accept or reject.</summary>
    public static string TextOf(ReadOnlySpan<byte> p) => p.Length > 5 ? Encoding.UTF8.GetString(p[5..]) : "";

    public static byte[] Net(byte kind, uint seq, ReadOnlySpan<byte> payload = default)
    {
        var b = new byte[5 + payload.Length];
        b[0] = kind;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(1), seq);
        payload.CopyTo(b.AsSpan(5));
        return b;
    }

    public static byte[] KeepalivePacket(uint seq) => Net(Keepalive, seq);
    public static byte[] EchoPacket(uint seq, string instanceId) => Net(Echo, seq, Encoding.UTF8.GetBytes(instanceId));
    public static byte[] RetryPacket(uint seq = 0) => Net(Retry, seq);
    public static byte[] DataPacket(uint seq, ReadOnlySpan<byte> payload) => Net(Data, seq, payload);

    public static uint SeqOf(ReadOnlySpan<byte> p) => p.Length >= 5 ? BinaryPrimitives.ReadUInt32LittleEndian(p[1..]) : 0;
    public static ReadOnlySpan<byte> PayloadOf(ReadOnlySpan<byte> p) => p.Length > 5 ? p[5..] : default;

    static byte[] Header(byte type, int extra)
    {
        var b = new byte[5 + extra];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 0xFFFFFFFF);
        b[4] = type;
        return b;
    }

    static byte[] HeaderWithText(byte type, string text)
    {
        var t = Encoding.UTF8.GetBytes(text);
        var b = Header(type, t.Length);
        t.CopyTo(b.AsSpan(5));
        return b;
    }
}
