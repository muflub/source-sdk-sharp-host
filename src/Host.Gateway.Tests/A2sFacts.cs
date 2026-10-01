using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SourceSharp.Host.FakeClient;
using SourceSharp.Host.Gateway.Tests.Support;
using SourceSharp.Host.Proto.Gateway;

namespace SourceSharp.Host.Gateway.Tests;

/// <summary>A2S for the hub from the SetServerInfo snapshot, challenged, never forwarded (plan §8.3).</summary>
public class A2sFacts
{
    static readonly byte[] InfoQuery = [0xFF, 0xFF, 0xFF, 0xFF, 0x54, .. "Source Engine Query\0"u8];

    static ServerInfo Snapshot() => new()
    {
        Name = "Descent Town", Map = "descent_town", Folder = "descent", Game = "Descent",
        Players = 2, MaxPlayers = 32, Bots = 0, AppId = 243750, Version = "1.0.0",
        PlayerList = { new A2sPlayer { Name = "ann", Score = 3, Duration = 12.5f }, new A2sPlayer { Name = "bob", Score = 1, Duration = 2f } },
        Rules = { ["sv_lan"] = "1", ["descent_depth"] = "0" },
    };

    sealed class Query : IDisposable
    {
        readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        readonly IPEndPoint _to;
        public Query(IPEndPoint to) => _to = to;
        public IPEndPoint Local => (IPEndPoint)_udp.Client.LocalEndPoint!;

        public async Task<byte[]?> AskAsync(byte[] packet, int ms = 1000)
        {
            await _udp.SendAsync(packet, _to);
            using var cts = new CancellationTokenSource(ms);
            try { return (await _udp.ReceiveAsync(cts.Token)).Buffer; }
            catch (OperationCanceledException) { return null; }
        }

        public void Dispose() => _udp.Dispose();
    }

    static byte[] With(byte[] q, int challenge)
    {
        var b = new byte[q.Length + 4];
        q.CopyTo(b, 0);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(q.Length), challenge);
        return b;
    }

    static byte[] Req(byte type, int challenge) => With([0xFF, 0xFF, 0xFF, 0xFF, type], challenge);

    static int ChallengeOf(byte[] reply)
    {
        Assert.Equal(0x41, reply[4]);
        return BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(5));
    }

    static List<string> Strings(ReadOnlySpan<byte> p, int count)
    {
        var list = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var n = p.IndexOf((byte)0);
            list.Add(Encoding.UTF8.GetString(p[..n]));
            p = p[(n + 1)..];
        }
        return list;
    }

    [Fact]
    public async Task Info_without_a_challenge_is_answered_with_one()
    {
        await using var rig = new Rig();
        rig.Relay.SetServerInfo(Snapshot());
        using var q = new Query(rig.Relay.PublicEndPoint);
        var r = await q.AskAsync(InfoQuery);
        Assert.Equal(rig.Relay.A2s.ChallengeFor(q.Local), ChallengeOf(r!));
    }

    [Fact]
    public async Task Info_with_the_challenge_answers_from_the_snapshot()
    {
        await using var rig = new Rig();
        rig.Relay.SetServerInfo(Snapshot());
        using var q = new Query(rig.Relay.PublicEndPoint);
        var c = ChallengeOf((await q.AskAsync(InfoQuery))!);
        var r = (await q.AskAsync(With(InfoQuery, c)))!;
        Assert.Equal(0x49, r[4]);
        Assert.Equal(["Descent Town", "descent_town", "descent", "Descent"], Strings(r.AsSpan(6), 4));
        var after = r.AsSpan(6 + "Descent Town\0descent_town\0descent\0Descent\0".Length);
        Assert.Equal((2, 32), (after[2], after[3])); // after the 2-byte app id: players, max players
    }

    [Fact]
    public async Task Info_with_a_wrong_challenge_is_rechallenged_not_answered()
    {
        await using var rig = new Rig();
        rig.Relay.SetServerInfo(Snapshot());
        using var q = new Query(rig.Relay.PublicEndPoint);
        var right = rig.Relay.A2s.ChallengeFor(q.Local);
        var r = (await q.AskAsync(With(InfoQuery, right ^ 0x5555)))!;
        Assert.Equal(right, ChallengeOf(r));
    }

    [Fact]
    public async Task Player_list_is_answered_after_the_challenge()
    {
        await using var rig = new Rig();
        rig.Relay.SetServerInfo(Snapshot());
        using var q = new Query(rig.Relay.PublicEndPoint);
        var c = ChallengeOf((await q.AskAsync(Req(0x55, -1)))!);
        var r = (await q.AskAsync(Req(0x55, c)))!;
        Assert.Equal((0x44, 2), (r[4], r[5]));
        Assert.Equal(0, r[6]);
        Assert.Equal("ann", Strings(r.AsSpan(7), 1)[0]);
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(r.AsSpan(7 + 4)));
        Assert.Equal(12.5f, BinaryPrimitives.ReadSingleLittleEndian(r.AsSpan(7 + 8)));
    }

    [Fact]
    public async Task Rules_are_answered_after_the_challenge()
    {
        await using var rig = new Rig();
        rig.Relay.SetServerInfo(Snapshot());
        using var q = new Query(rig.Relay.PublicEndPoint);
        var c = ChallengeOf((await q.AskAsync(Req(0x56, -1)))!);
        var r = (await q.AskAsync(Req(0x56, c)))!;
        Assert.Equal(0x45, r[4]);
        Assert.Equal(2, BinaryPrimitives.ReadInt16LittleEndian(r.AsSpan(5)));
        Assert.Equal(["descent_depth", "0", "sv_lan", "1"], Strings(r.AsSpan(7), 4));
    }

    [Fact]
    public async Task Without_a_snapshot_a_challenged_query_is_dropped()
    {
        await using var rig = new Rig();
        using var q = new Query(rig.Relay.PublicEndPoint);
        var c = ChallengeOf((await q.AskAsync(InfoQuery))!);
        Assert.Null(await q.AskAsync(With(InfoQuery, c), 300));
        Assert.Equal(1, rig.Relay.A2s.Dropped);
    }

    [Fact]
    public async Task Queries_are_never_forwarded_and_open_no_session()
    {
        await using var rig = new Rig();
        var hub = rig.Backend("hub");
        rig.SetDefault(hub);
        rig.Relay.SetServerInfo(Snapshot());
        using var q = new Query(rig.Relay.PublicEndPoint);
        var c = ChallengeOf((await q.AskAsync(InfoQuery))!);
        Assert.NotNull(await q.AskAsync(With(InfoQuery, c)));
        Assert.Equal(0, rig.Relay.SessionCount);

        // Positive arm: a handshake packet from the same socket does reach the hub.
        await q.AskAsync(ToyWire.GetChallengePacket(1));
        Assert.True(await hub.WaitForAsync(l => l.Count == 1, Rig.T));
        Assert.Equal(ToyWire.GetChallenge, Assert.Single(hub.Log).Type);
    }
}
