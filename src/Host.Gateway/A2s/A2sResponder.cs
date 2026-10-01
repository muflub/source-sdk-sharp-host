using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using SourceSharp.Host.Proto.Gateway;

namespace SourceSharp.Host.Gateway.A2s;

/// <summary>
/// Answers the Valve server queries for the hub from the service's <c>SetServerInfo</c> snapshot
/// (plan §8.3, Q7); they are never forwarded. Formats: Valve Server Queries
/// (developer.valvesoftware.com/wiki/Server_queries), cited in docs/net-protocol.md. Every query is
/// challenged: A2S_INFO in its 2020 variant (the request re-sent with the challenge appended),
/// A2S_PLAYER and A2S_RULES with the challenge in place of -1. The challenge is derived from the
/// requester's address with a per-process key, so a spoofed source cannot use a reflected answer.
/// </summary>
public sealed class A2sResponder
{
    public const byte InfoRequest = 0x54;    // 'T'
    public const byte PlayerRequest = 0x55;  // 'U'
    public const byte RulesRequest = 0x56;   // 'V'
    public const byte ChallengeReply = 0x41; // 'A'
    public const byte InfoReply = 0x49;      // 'I'
    public const byte PlayerReply = 0x44;    // 'D'
    public const byte RulesReply = 0x45;     // 'E'
    static ReadOnlySpan<byte> InfoPayload => "Source Engine Query\0"u8;

    readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    readonly int _gamePort;
    readonly Lock _sync = new();
    ServerInfo? _info;

    public A2sResponder(int gamePort) => _gamePort = gamePort;

    /// <summary>Queries answered, challenges sent, and queries dropped (no snapshot, bad challenge, malformed).</summary>
    public long Answered { get; private set; }
    public long Challenged { get; private set; }
    public long Dropped { get; private set; }

    public void SetInfo(ServerInfo info) { lock (_sync) _info = info.Clone(); }
    public ServerInfo? Info { get { lock (_sync) return _info?.Clone(); } }

    /// <summary>Whether a connectionless type byte is one of the queries this answers.</summary>
    public static bool IsQuery(byte type) => type is InfoRequest or PlayerRequest or RulesRequest;

    /// <summary>The challenge a requester at <paramref name="from"/> must present.</summary>
    public int ChallengeFor(IPEndPoint from)
    {
        Span<byte> addr = stackalloc byte[6];
        from.Address.MapToIPv4().TryWriteBytes(addr, out _);
        BinaryPrimitives.WriteUInt16LittleEndian(addr[4..], (ushort)from.Port);
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(_key, addr, mac);
        var c = BinaryPrimitives.ReadInt32LittleEndian(mac) & 0x7FFFFFFF;
        return c == 0 ? 1 : c;
    }

    /// <summary>The answer to a query packet (starting FF FF FF FF), or null to drop it.</summary>
    public byte[]? Answer(ReadOnlySpan<byte> p, IPEndPoint from)
    {
        if (p.Length < 5) { Dropped++; return null; }
        ServerInfo? info;
        lock (_sync) info = _info;
        switch (p[4])
        {
            case InfoRequest:
            {
                var body = p[5..];
                if (!body.StartsWith(InfoPayload)) { Dropped++; return null; }
                var rest = body[InfoPayload.Length..];
                if (rest.Length < 4) return Challenge(from);
                if (BinaryPrimitives.ReadInt32LittleEndian(rest) != ChallengeFor(from)) return Challenge(from);
                if (info is null) { Dropped++; return null; }
                Answered++;
                return InfoBytes(info);
            }
            case PlayerRequest:
            case RulesRequest:
            {
                if (p.Length < 9) { Dropped++; return null; }
                var c = BinaryPrimitives.ReadInt32LittleEndian(p[5..]);
                if (c != ChallengeFor(from)) return Challenge(from);
                if (info is null) { Dropped++; return null; }
                Answered++;
                return p[4] == PlayerRequest ? PlayerBytes(info) : RulesBytes(info);
            }
            default:
                Dropped++;
                return null;
        }
    }

    byte[] Challenge(IPEndPoint from)
    {
        Challenged++;
        var b = new byte[9];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 0xFFFFFFFF);
        b[4] = ChallengeReply;
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(5), ChallengeFor(from));
        return b;
    }

    byte[] InfoBytes(ServerInfo i)
    {
        var w = new Writer(InfoReply);
        w.Byte(17); // protocol, as Source 2013 servers report it
        w.Str(i.Name); w.Str(i.Map); w.Str(i.Folder); w.Str(i.Game);
        w.Short((short)(i.AppId & 0xFFFF));
        w.Byte((byte)Math.Clamp(i.Players, 0, 255));
        w.Byte((byte)Math.Clamp(i.MaxPlayers, 0, 255));
        w.Byte((byte)Math.Clamp(i.Bots, 0, 255));
        w.Byte((byte)'d'); // dedicated
        w.Byte((byte)'l'); // linux
        w.Byte(0);         // visibility: public
        w.Byte(0);         // VAC: unsecured (LAN/friends, Q24)
        w.Str(i.Version);
        w.Byte(0x80 | 0x01); // EDF: game port, game id
        w.Short((short)_gamePort);
        w.Long((long)i.AppId);
        return w.ToArray();
    }

    static byte[] PlayerBytes(ServerInfo i)
    {
        var w = new Writer(PlayerReply);
        var players = i.PlayerList.Take(255).ToList();
        w.Byte((byte)players.Count);
        for (var n = 0; n < players.Count; n++)
        {
            w.Byte((byte)n);
            w.Str(players[n].Name);
            w.Int(players[n].Score);
            w.Float(players[n].Duration);
        }
        return w.ToArray();
    }

    static byte[] RulesBytes(ServerInfo i)
    {
        var w = new Writer(RulesReply);
        var rules = i.Rules.OrderBy(r => r.Key, StringComparer.Ordinal).ToList();
        w.Short((short)rules.Count);
        foreach (var (k, v) in rules) { w.Str(k); w.Str(v); }
        return w.ToArray();
    }

    sealed class Writer
    {
        readonly MemoryStream _m = new();
        public Writer(byte type) { _m.Write([0xFF, 0xFF, 0xFF, 0xFF, type]); }
        public void Byte(byte b) => _m.WriteByte(b);
        public void Str(string s) { _m.Write(Encoding.UTF8.GetBytes(s)); _m.WriteByte(0); }
        public void Short(short v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteInt16LittleEndian(b, v); _m.Write(b); }
        public void Int(int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); _m.Write(b); }
        public void Long(long v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64LittleEndian(b, v); _m.Write(b); }
        public void Float(float v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteSingleLittleEndian(b, v); _m.Write(b); }
        public byte[] ToArray() => _m.ToArray();
    }
}
