using System.Buffers.Binary;
using System.Text;
using SourceSharp.Host.FakeClient;
using SourceSharp.Host.Gateway.Handshake;

namespace SourceSharp.Host.Gateway.Tests;

/// <summary>
/// The Source C2S_CONNECT reader against hand-built packets laid out per docs/net-protocol.md §2.3.
/// Unverified against the 2013 engine until the H0f capture; these facts pin the reader to the note.
/// </summary>
public class SourceConnectReaderFacts
{
    const ulong Sid = 76561198000000042UL;

    /// <summary>C2S_CONNECT as docs/net-protocol.md §2.3 lays it out.</summary>
    static byte[] Connect(string name = "ann", string password = "", string version = "1.0.0.0",
                          ulong steamId = Sid, int ticketTail = 20, int challenge = 0x1234, int clientChallenge = 0x0ABC)
    {
        var m = new MemoryStream();
        void I32(int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); m.Write(b); }
        void Str(string s) { m.Write(Encoding.UTF8.GetBytes(s)); m.WriteByte(0); }
        I32(-1);
        m.WriteByte(0x6B);
        I32(24);            // PROTOCOL_VERSION
        I32(3);             // PROTOCOL_STEAM
        I32(challenge);
        I32(clientChallenge);
        Str(name); Str(password); Str(version);
        Span<byte> len = stackalloc byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(len, (short)(8 + ticketTail));
        m.Write(len);
        Span<byte> id = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(id, steamId);
        m.Write(id);
        m.Write(new byte[ticketTail]);
        return m.ToArray();
    }

    [Fact]
    public void Reads_the_steamid_prepended_to_the_ticket()
    {
        Assert.True(SourceConnectReader.TryReadSteamId(Connect(), out var id));
        Assert.Equal(Sid, id);
    }

    [Fact]
    public void The_steamid_offset_follows_the_three_strings()
    {
        var p = Connect(name: new string('x', 200), password: "hunter2", version: "7.1.0.0");
        Assert.True(SourceConnectReader.TryReadSteamId(p, out var id));
        Assert.Equal(Sid, id);
        Assert.Equal(Sid, BinaryPrimitives.ReadUInt64LittleEndian(p.AsSpan(23 + 201 + 8 + 8)));
    }

    [Fact]
    public void A_ticket_of_eight_bytes_or_fewer_is_unreadable()
    {
        Assert.False(SourceConnectReader.TryReadSteamId(Connect(ticketTail: 0), out _));
    }

    [Fact]
    public void A_truncated_packet_is_unreadable()
    {
        var p = Connect();
        Assert.False(SourceConnectReader.TryReadSteamId(p.AsSpan(0, p.Length - 21), out _));
    }

    [Fact]
    public void A_name_without_its_nul_is_unreadable()
    {
        var p = Connect(name: new string('x', 300));
        Assert.False(SourceConnectReader.TryReadSteamId(p, out _));
    }

    [Fact]
    public void A_steamid_that_is_not_a_public_individual_account_is_refused()
    {
        Assert.False(SourceConnectReader.TryReadSteamId(Connect(steamId: 0x0170000100000001UL), out _)); // anonymous gameserver type
    }

    [Fact]
    public void Another_packet_type_is_not_a_connect()
    {
        var p = Connect();
        p[4] = 0x71;
        Assert.False(SourceConnectReader.TryReadSteamId(p, out _));
    }

    [Fact]
    public void The_codec_reads_the_challenge_at_offset_13()
    {
        Assert.True(new SourceCodec().TryReadConnect(Connect(challenge: 777), out var ch, out var sid));
        Assert.Equal((777, (ulong?)Sid), (ch, sid));
    }

    [Fact]
    public void The_gateways_challenge_is_39_bytes_with_magic_and_the_echoed_client_challenge()
    {
        byte[] q = [0xFF, 0xFF, 0xFF, 0xFF, 0x71, 0x78, 0x56, 0x34, 0x12, .. "0000000000\0"u8];
        var a = new SourceCodec { GameServerSteamId = 90071992547409920UL }.Challenge(555, q);
        Assert.Equal(39, a.Length);
        Assert.Equal((0x41, 0x5A4F4933, 555, 0x12345678, 3),
            (a[4], BinaryPrimitives.ReadInt32LittleEndian(a.AsSpan(5)), BinaryPrimitives.ReadInt32LittleEndian(a.AsSpan(9)),
             BinaryPrimitives.ReadInt32LittleEndian(a.AsSpan(13)), BinaryPrimitives.ReadInt32LittleEndian(a.AsSpan(17))));
        Assert.Equal(90071992547409920UL, BinaryPrimitives.ReadUInt64LittleEndian(a.AsSpan(23)));
    }

    [Fact]
    public void The_reject_echoes_the_client_challenge()
    {
        var r = new SourceCodec().Reject(Connect(clientChallenge: 0x0ABC), "#GameUI_ServerRejectBadChallenge");
        Assert.Equal(0x39, r[4]);
        Assert.Equal(0x0ABC, BinaryPrimitives.ReadInt32LittleEndian(r.AsSpan(5)));
        Assert.Equal(0, r[^1]);
    }
}

/// <summary>The gateway's toy codec agrees with Host.FakeClient's ToyWire byte for byte.</summary>
public class ToyCodecFacts
{
    [Fact]
    public void Reads_the_steamid_from_a_toy_connect()
    {
        Assert.True(new ToyCodec().TryReadConnect(ToyWire.ConnectPacket(9, 76561198000000042UL, "ann"), out var ch, out var sid));
        Assert.Equal((9, (ulong?)76561198000000042UL), (ch, sid));
    }

    [Fact]
    public void An_unreadable_toy_ticket_reads_as_no_steamid()
    {
        Assert.True(new ToyCodec().TryReadConnect(ToyWire.ConnectPacket(9, ToyWire.UnreadableTicket(), "ann"), out _, out var sid));
        Assert.Null(sid);
    }

    [Fact]
    public void The_gateways_toy_challenge_is_what_the_client_expects()
    {
        var a = new ToyCodec().Challenge(4242, ToyWire.GetChallengePacket(77));
        Assert.True(ToyWire.TryReadChallenge(a, out var ch, out var cc));
        Assert.Equal((4242, 77), (ch, cc));
    }

    [Fact]
    public void Handshake_types_are_recognised()
    {
        var c = new ToyCodec();
        Assert.True(c.IsGetChallenge(ToyWire.GetChallengePacket(1)));
        Assert.True(c.IsChallenge(ToyWire.ChallengePacket(1, 2)));
        Assert.False(c.IsGetChallenge(ToyWire.KeepalivePacket(1)));
    }
}
