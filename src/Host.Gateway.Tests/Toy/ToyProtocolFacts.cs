using System.Buffers.Binary;
using System.Net;
using SourceSharp.Host.FakeClient;
using Client = SourceSharp.Host.FakeClient.FakeClient;

namespace SourceSharp.Host.Gateway.Tests.Toy;

public class ToyWireFacts
{
    [Fact]
    public void Connect_carries_the_steamid_at_the_documented_offset()
    {
        var p = ToyWire.ConnectPacket(1234, 76561198000000042UL, "ann");
        Assert.Equal(76561198000000042UL, BinaryPrimitives.ReadUInt64LittleEndian(p.AsSpan(ToyWire.ConnectSteamIdOffset)));
        Assert.Equal(15, ToyWire.ConnectSteamIdOffset);
    }

    [Fact]
    public void Connect_round_trips_challenge_steamid_and_name()
    {
        var p = ToyWire.ConnectPacket(99, 7UL, "bob");
        Assert.True(ToyWire.TryReadConnect(p, out var ch, out var sid, out var name));
        Assert.Equal((99, (ulong?)7UL, "bob"), (ch, sid, name));
    }

    [Fact]
    public void An_unreadable_ticket_reads_as_no_steamid()
    {
        var p = ToyWire.ConnectPacket(99, ToyWire.UnreadableTicket(), "bob");
        Assert.True(ToyWire.TryReadConnect(p, out _, out var sid, out _));
        Assert.Null(sid);
    }

    [Fact]
    public void Handshake_packets_are_connectionless_and_netchannel_packets_are_not()
    {
        Assert.All([ToyWire.GetChallengePacket(1), ToyWire.ChallengePacket(1, 2), ToyWire.ConnectPacket(1, 2, "x"),
                    ToyWire.AcceptPacket("a"), ToyWire.RejectPacket("r")], p => Assert.True(ToyWire.IsConnectionless(p)));
        Assert.All([ToyWire.KeepalivePacket(1), ToyWire.EchoPacket(1, "a"), ToyWire.RetryPacket(), ToyWire.DataPacket(1, [0xFF, 0xFF, 0xFF, 0xFF])],
            p => Assert.False(ToyWire.IsConnectionless(p)));
    }
}

public class ToyBackendFacts
{
    static readonly TimeSpan T = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task A_client_connects_and_reports_the_instance_id()
    {
        using var b = new ToyBackend("inst-a");
        using var c = new Client(b.EndPoint, 1UL);
        Assert.Equal("inst-a", await c.ConnectAsync(T));
        Assert.Equal(["q", "A", "k", "B inst-a"], c.Steps);
    }

    [Fact]
    public async Task Keepalives_echo_the_instance_id()
    {
        using var b = new ToyBackend("inst-a");
        using var c = new Client(b.EndPoint, 1UL);
        await c.ConnectAsync(T);
        Assert.Equal("inst-a", await c.KeepaliveAsync(T));
    }

    [Fact]
    public async Task The_receive_log_records_every_datagram_with_its_source()
    {
        using var b = new ToyBackend("inst-a");
        using var c = new Client(b.EndPoint, 1UL);
        await c.ConnectAsync(T);
        Assert.True(await b.WaitForAsync(l => l.Count == 3, T)); // q, k, the first keepalive
        Assert.All(b.Log, r => Assert.Equal(c.LocalEndPoint, r.From));
        Assert.Equal([ToyWire.GetChallenge, ToyWire.Connect, (byte)0], b.Log.Select(r => r.Type));
    }

    [Fact]
    public async Task The_backend_records_the_steamid_it_accepted()
    {
        using var b = new ToyBackend("inst-a");
        using var c = new Client(b.EndPoint, 76561198000000001UL);
        await c.ConnectAsync(T);
        Assert.Equal(76561198000000001UL, b.Accepted[c.LocalEndPoint]);
    }

    [Fact]
    public async Task An_unready_backend_logs_but_does_not_answer()
    {
        using var b = new ToyBackend("inst-a") { Ready = false };
        using var c = new Client(b.EndPoint, 1UL);
        c.StartHandshake();
        Assert.True(await b.WaitForAsync(l => l.Count == 1, T));
        await Assert.ThrowsAsync<TimeoutException>(() => c.WaitConnectedAsync(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task A_connect_with_another_peers_challenge_is_rejected()
    {
        using var b = new ToyBackend("inst-a");
        using var good = new Client(b.EndPoint, 1UL);
        await good.ConnectAsync(T);
        using var raw = new Client(b.EndPoint, 2UL);
        raw.SendRaw(ToyWire.ConnectPacket(12345, 2UL, "forger"));
        Assert.True(await raw.WaitForAsync(x => x.RejectReason is not null, T));
        Assert.Equal("bad challenge", raw.RejectReason);
    }

    [Fact]
    public async Task A_sixth_handshake_from_one_ip_is_refused_like_max_reuse_per_ip()
    {
        using var b = new ToyBackend("inst-a");
        var clients = Enumerable.Range(0, 6).Select(i => new Client(b.EndPoint, (ulong)i + 1)).ToList();
        try
        {
            // Five handshakes in progress (challenged, never connected): the sixth is refused.
            foreach (var c in clients.Take(5)) c.SendRaw(ToyWire.GetChallengePacket(1));
            Assert.True(await b.WaitForAsync(_ => b.HandshakingFrom(IPAddress.Loopback) == 5, T));
            clients[5].StartHandshake();
            Assert.True(await clients[5].WaitForAsync(x => x.RejectReason is not null, T));
            Assert.Equal("too many connections from your address", clients[5].RejectReason);
        }
        finally { clients.ForEach(c => c.Dispose()); }
    }

    [Fact]
    public async Task Five_handshakes_from_one_ip_are_all_accepted()
    {
        using var b = new ToyBackend("inst-a");
        var clients = Enumerable.Range(0, 5).Select(i => new Client(b.EndPoint, (ulong)i + 1)).ToList();
        try
        {
            var ids = await Task.WhenAll(clients.Select(c => c.ConnectAsync(T)));
            Assert.All(ids, id => Assert.Equal("inst-a", id));
        }
        finally { clients.ForEach(c => c.Dispose()); }
    }

    [Fact]
    public async Task Retry_restarts_the_handshake_at_the_same_address()
    {
        using var b = new ToyBackend("inst-a");
        using var c = new Client(b.EndPoint, 1UL);
        await c.ConnectAsync(T);
        b.SendRetry(c.LocalEndPoint);
        Assert.True(await c.WaitForAsync(x => x.Steps.Count == 9, T));
        Assert.Equal(1, c.Retries);
        Assert.Equal(["q", "A", "k", "B inst-a", "retry", "q", "A", "k", "B inst-a"], c.Steps);
    }
}
