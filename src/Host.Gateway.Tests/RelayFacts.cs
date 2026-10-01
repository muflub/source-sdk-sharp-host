using SourceSharp.Host.FakeClient;
using SourceSharp.Host.Gateway.Relay;
using SourceSharp.Host.Gateway.Tests.Support;

namespace SourceSharp.Host.Gateway.Tests;

public class RelayFacts
{
    static readonly TimeSpan T = Rig.T;

    [Fact]
    public async Task A_client_reaches_the_default_backend_through_the_gateway()
    {
        await using var rig = new Rig();
        var a = rig.Backend("inst-a");
        rig.SetDefault(a);
        var c = rig.Client(1);
        Assert.Equal("inst-a", await c.ConnectAsync(T));
        Assert.Equal("inst-a", await c.KeepaliveAsync(T));
    }

    [Fact]
    public async Task The_backend_sees_the_sessions_peer_not_the_client()
    {
        await using var rig = new Rig();
        var a = rig.Backend("inst-a");
        rig.SetDefault(a);
        var c = rig.Client(1);
        await c.ConnectAsync(T);
        var peer = Assert.Single(a.Senders);
        Assert.NotEqual(c.LocalEndPoint, peer);
        Assert.Equal(rig.PeerOf(c), peer);
    }

    [Fact]
    public async Task Two_clients_get_two_sessions_with_distinct_peers()
    {
        await using var rig = new Rig();
        var a = rig.Backend("inst-a");
        rig.SetDefault(a);
        var c1 = rig.Client(1);
        var c2 = rig.Client(2);
        await Task.WhenAll(c1.ConnectAsync(T), c2.ConnectAsync(T));
        Assert.Equal(2, rig.Relay.SessionCount);
        Assert.Equal(2, a.Senders.Count);
    }

    [Fact]
    public async Task A_new_session_is_reported_opened_with_its_backend_and_peer()
    {
        await using var rig = new Rig();
        var a = rig.Backend("inst-a");
        rig.SetDefault(a);
        var c = rig.Client(1);
        await c.ConnectAsync(T);
        var e = Assert.Single(rig.Events.Of("opened"));
        Assert.Equal((c.LocalEndPoint.ToString(), a.EndPoint.ToString(), rig.PeerOf(c).ToString()), (e.ClientAddr, e.Backend, e.Peer));
    }

    [Fact]
    public async Task A_silent_session_expires_and_is_reported_closed()
    {
        await using var rig = new Rig();
        var a = rig.Backend("inst-a");
        rig.SetDefault(a);
        var c = rig.Client(1);
        await c.ConnectAsync(T);
        Assert.Equal(1, rig.Relay.SessionCount);
        rig.Time.Advance(rig.Options.SessionExpiry);
        Assert.Equal(0, rig.Relay.SessionCount);
        Assert.Equal("expired", Assert.Single(rig.Events.Of("closed")).Reason);
    }

    [Fact]
    public async Task A_session_heard_within_the_expiry_is_kept()
    {
        await using var rig = new Rig();
        var a = rig.Backend("inst-a");
        rig.SetDefault(a);
        var c = rig.Client(1);
        await c.ConnectAsync(T);
        rig.Time.Advance(rig.Options.SessionExpiry - TimeSpan.FromSeconds(1));
        await c.KeepaliveAsync(T);
        rig.Time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, rig.Relay.SessionCount);
        Assert.Empty(rig.Events.Of("closed"));
    }

    [Fact]
    public async Task A_route_flip_affects_only_the_next_handshake()
    {
        await using var rig = new Rig();
        var a = rig.Backend("inst-a");
        var b = rig.Backend("inst-b");
        rig.SetDefault(a);
        var c = rig.Client(76561198000000001);
        await c.ConnectAsync(T);
        Assert.Equal(ControlStatus.Ok, rig.Relay.SetRoute(rig.Next(), c.LocalEndPoint.ToString(), b.EndPoint.ToString(), "76561198000000001"));

        // The established netchannel still reaches A...
        Assert.Equal("inst-a", await c.KeepaliveAsync(T));
        Assert.Empty(b.Log);
        // ...until A sends retry and the client's new handshake lands on B.
        a.SendRetry(rig.PeerOf(c));
        Assert.True(await c.WaitForAsync(x => x.Retries == 1 && x.Connected && x.InstanceId == "inst-b", T));
    }

    [Fact]
    public async Task A_route_flip_is_a_hard_cut_no_datagram_reaches_both_backends()
    {
        await using var rig = new Rig();
        var a = rig.Backend("inst-a");
        var b = rig.Backend("inst-b");
        rig.SetDefault(a);
        var c = rig.Client(76561198000000001);
        await c.ConnectAsync(T);
        rig.Relay.SetRoute(rig.Next(), c.LocalEndPoint.ToString(), b.EndPoint.ToString(), "76561198000000001");
        a.SendRetry(rig.PeerOf(c));
        Assert.True(await c.WaitForAsync(x => x.Connected && x.InstanceId == "inst-b", T));
        for (var i = 0; i < 5; i++) Assert.Equal("inst-b", await c.KeepaliveAsync(T));

        var atA = a.Log;
        var atB = b.Log;
        Assert.NotEmpty(atB); // the stimulus reached B
        Assert.Empty(atA.Select(r => Convert.ToHexString(r.Bytes)).Intersect(atB.Select(r => Convert.ToHexString(r.Bytes))));
        Assert.True(atA[^1].At <= atB[0].At, "A received after B's first datagram");
        Assert.DoesNotContain(atA, r => r.Type == ToyWire.GetChallenge && r.Order > 0); // the retry's q went to B only
        Assert.Single(rig.Events.Of("moved"));
    }

    [Fact]
    public async Task After_a_hop_the_new_backend_sees_the_session_from_its_own_sidecar()
    {
        await using var rig = new Rig();
        var a = rig.Backend("inst-a");
        var b = rig.Backend("inst-b");
        rig.SetDefault(a);
        var c = rig.Client(76561198000000001);
        await c.ConnectAsync(T);
        var before = rig.PeerOf(c);
        rig.Relay.SetRoute(rig.Next(), c.LocalEndPoint.ToString(), b.EndPoint.ToString(), "76561198000000001");
        a.SendRetry(before);
        Assert.True(await c.WaitForAsync(x => x.Connected && x.InstanceId == "inst-b", T));
        var after = rig.PeerOf(c);
        Assert.Equal(before, Assert.Single(a.Senders));
        Assert.Equal(after, Assert.Single(b.Senders));
        var who = await b.Info.ResolveAsync(new Proto.Relay.ResolveRequest { Peer = after.ToString() });
        Assert.Equal((true, c.LocalEndPoint.ToString()), (who.Found, who.ClientAddr));
    }
}
