using System.Buffers.Binary;
using System.Text;

namespace SourceSharp.Host.Gateway.Handshake;

/// <summary>
/// Source's connectionless handshake as documented publicly and observed
/// (docs/net-protocol.md §2.1-2.4). <b>Unverified</b>: the shipped 2013 engine is closed, and every
/// layout here is marked <c>pending (H0f capture)</c> in the note until a capture of a real client
/// confirms it. Identity-first stays off (<c>GatewayOptions.IdentityFirst = false</c>) until then.
/// </summary>
public sealed class SourceCodec : IHandshakeCodec
{
    public const byte GetChallengeType = 0x71; // 'q'  A2S_GETCHALLENGE
    public const byte ChallengeType = 0x41;    // 'A'  S2C_CHALLENGE
    public const byte ConnectType = 0x6B;      // 'k'  C2S_CONNECT
    public const byte RejectType = 0x39;       // '9'  S2C_CONNREJECT
    public const int MagicVersion = 0x5A4F4933; // S2C_MAGICVERSION
    public const int AuthProtocolSteam = 3;     // PROTOCOL_STEAM

    /// <summary>The game-server SteamID the gateway's own challenge carries (§8.1b step 1); the
    /// backend's last-seen one avoids a second ticket (docs/net-protocol.md, identity-first step 4).</summary>
    public ulong GameServerSteamId { get; set; }

    public string Name => "Source";

    static bool Oob(ReadOnlySpan<byte> p, byte type) =>
        p.Length >= 5 && BinaryPrimitives.ReadUInt32LittleEndian(p) == 0xFFFFFFFF && p[4] == type;

    public bool IsGetChallenge(ReadOnlySpan<byte> p) => Oob(p, GetChallengeType) && p.Length >= 9;
    public bool IsChallenge(ReadOnlySpan<byte> p) => Oob(p, ChallengeType) && p.Length >= 17;

    public bool TryReadConnect(ReadOnlySpan<byte> p, out int challenge, out ulong? steamId)
    {
        challenge = 0; steamId = null;
        if (!Oob(p, ConnectType) || p.Length < 21) return false;
        challenge = BinaryPrimitives.ReadInt32LittleEndian(p[13..]);
        steamId = SourceConnectReader.TryReadSteamId(p, out var sid) ? sid : null;
        return true;
    }

    /// <summary>S2C_CHALLENGE, 39 bytes (docs/net-protocol.md §2.2).</summary>
    public byte[] Challenge(int challenge, ReadOnlySpan<byte> getChallenge)
    {
        var b = new byte[39];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 0xFFFFFFFF);
        b[4] = ChallengeType;
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(5), MagicVersion);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(9), challenge);
        getChallenge.Slice(5, 4).CopyTo(b.AsSpan(13));   // echo the client's challenge
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(17), AuthProtocolSteam);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(21), 0); // steam2 key length
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(23), GameServerSteamId);
        b[31] = 0; // not secure (LAN / friends, Q24)
        "000000\0"u8.CopyTo(b.AsSpan(32));
        return b;
    }

    /// <summary>S2C_CONNREJECT: FF FF FF FF '9' clientChallenge:i32 reason 00 (docs/net-protocol.md §2.4).</summary>
    public byte[] Reject(ReadOnlySpan<byte> connect, string reason)
    {
        var t = Encoding.UTF8.GetBytes(reason);
        var b = new byte[9 + t.Length + 1];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 0xFFFFFFFF);
        b[4] = RejectType;
        if (connect.Length >= 21) connect.Slice(17, 4).CopyTo(b.AsSpan(5));
        t.CopyTo(b.AsSpan(9));
        return b;
    }
}

/// <summary>
/// Reads the SteamID64 from a Source C2S_CONNECT (docs/net-protocol.md §2.3): after the header, type,
/// protocol, auth protocol, challenge and client challenge (21 bytes), three NUL-terminated strings
/// (name ≤ 256, password ≤ 256, product version ≤ 32), an i16 ticket length L (8 &lt; L ≤ 2048), then
/// the ticket blob whose first 8 bytes are the SteamID64, little-endian, in the clear
/// (docs/net-protocol.md §2.3). It reads, never rewrites.
/// <b>Unverified until the H0f capture.</b>
/// </summary>
public static class SourceConnectReader
{
    public const int FixedPrefix = 21;

    public static bool TryReadSteamId(ReadOnlySpan<byte> p, out ulong steamId)
    {
        steamId = 0;
        if (p.Length < FixedPrefix || BinaryPrimitives.ReadUInt32LittleEndian(p) != 0xFFFFFFFF || p[4] != SourceCodec.ConnectType)
            return false;
        var at = FixedPrefix;
        foreach (var max in (ReadOnlySpan<int>)[256, 256, 32])
        {
            var nul = p[at..].IndexOf((byte)0);
            if (nul < 0 || nul + 1 > max) return false;
            at += nul + 1;
        }
        if (p.Length < at + 2) return false;
        int len = BinaryPrimitives.ReadInt16LittleEndian(p[at..]);
        at += 2;
        if (len <= 8 || len > 2048 || p.Length < at + len) return false;
        var id = BinaryPrimitives.ReadUInt64LittleEndian(p[at..]);
        // A plausible individual SteamID before Steam sees it: universe
        // public (1) and an individual account (type 1).
        if ((id >> 56) != 1 || ((id >> 52) & 0xF) != 1) return false;
        steamId = id;
        return true;
    }
}
