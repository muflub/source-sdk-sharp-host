using System.Net;
using SourceSharp.Host.FakeClient;
using SourceSharp.Host.Gateway.Relay;
using SourceSharp.Host.Gateway.Tests.Support;
using SourceSharp.Host.Proto.Relay;

namespace SourceSharp.Host.Gateway.Tests;

/// <summary>client → gateway → sidecar → engine: the per-player loopback peer (relay.proto).</summary>
public class SidecarPathFacts
{
    static readonly TimeSpan T = Rig.T;
    const ulong Sid = 76561198000000001UL;

    [Fact]
    public async Task Six_simultaneous_clients_to_one_engine_all_connect_from_distinct_addresses()
    {
        // The engine refuses a sixth handshake from one address (ToyBackend, like MAX_REUSE_PER_IP);
        // through the sidecar every client has its own, so none is refused and no admission is needed.
        await using var rig = new Rig();
        var hub = rig.Backend("hub");
        rig.SetDefault(hub);
        var clients = Enumerable.Range(1, 6).Select(i => rig.Client((ulong)i)).ToList();
        var ids = await Task.WhenAll(clients.Select(c => c.ConnectAsync(T)));
        Assert.All(ids, id => Assert.Equal("hub", id));
        Assert.Equal(6, hub.Senders.Select(p => p.Address).Distinct().Count());
        Assert.All(clients, c => Assert.Null(c.RejectReason));
    }

    [Fact]
    public async Task A_steamid_reconnecting_to_the_same_instance_gets_the_same_address()
    {
        await using var rig = new Rig();
        var hub = rig.Backend("hub");
        rig.SetDefault(hub);
        var first = rig.Client(Sid);
        Assert.Equal(ControlStatus.Ok, rig.Relay.SetRoute(rig.Next(), first.LocalEndPoint.ToString(), hub.EndPoint.ToString(), Sid.ToString()));
        await rig.ConnectSettled(first, hub);
        var address = rig.PeerOf(first).Address;
        rig.Relay.CloseSession(rig.Next(), first.LocalEndPoint.ToString(), "left");
        Assert.True(await Rig.Until(() => hub.Sidecar.Table.LiveCount == 0));

        var again = rig.Client(Sid);
        Assert.Equal(ControlStatus.Ok, rig.Relay.SetRoute(rig.Next(), again.LocalEndPoint.ToString(), hub.EndPoint.ToString(), Sid.ToString()));
        await again.ConnectAsync(T);
        Assert.Equal(address, rig.PeerOf(again).Address);
    }

    [Fact]
    public async Task An_unidentified_client_after_a_player_left_gets_another_address()
    {
        // The negative arm of the fact above: no SteamID, so no address to come back to.
        await using var rig = new Rig();
        var hub = rig.Backend("hub");
        rig.SetDefault(hub);
        var first = rig.Client(Sid);
        await rig.ConnectSettled(first, hub);
        var address = rig.PeerOf(first).Address;
        rig.Relay.CloseSession(rig.Next(), first.LocalEndPoint.ToString(), "left");
        Assert.True(await Rig.Until(() => hub.Sidecar.Table.LiveCount == 0));
        var other = rig.Client(Sid);
        await other.ConnectAsync(T);
        Assert.NotEqual(address, rig.PeerOf(other).Address);
    }

    [Fact]
    public async Task The_sidecar_resolves_a_peer_to_the_clients_real_address()
    {
        await using var rig = new Rig();
        var hub = rig.Backend("hub");
        rig.SetDefault(hub);
        var c = rig.Client(Sid);
        rig.Relay.SetRoute(rig.Next(), c.LocalEndPoint.ToString(), hub.EndPoint.ToString(), Sid.ToString());
        await c.ConnectAsync(T);
        var peer = Assert.Single(hub.Senders);
        var who = await hub.Info.ResolveAsync(new ResolveRequest { Peer = peer.ToString() });
        Assert.Equal((true, c.LocalEndPoint.ToString(), Sid.ToString(), rig.Relay.SessionOf(c.LocalEndPoint)!.SessionId),
                     (who.Found, who.ClientAddr, who.Steamid, who.SessionId));
    }

    [Fact]
    public async Task Packets_sent_during_a_flip_reach_the_new_backend_once_in_order_after_opened()
    {
        await using var rig = new Rig();
        var a = rig.Backend("a");
        var b = rig.Backend("b");
        rig.SetDefault(a);
        var c = rig.Client(Sid);
        await rig.ConnectSettled(c, a); // else the post-accept keepalive lands on A after the snapshot
        rig.Relay.SetRoute(rig.Next(), c.LocalEndPoint.ToString(), b.EndPoint.ToString(), Sid.ToString());
        var atA = a.Log.Count;
        // The flip starts at the client's next handshake; everything after it waits for B's Opened.
        c.SendRaw(ToyWire.GetChallengePacket(5));
        var seqs = new[] { c.SendData([1]), c.SendData([2]), c.SendData([3]) };
        Assert.True(await b.WaitForAsync(l => l.Count(r => r.Kind == ToyWire.Data) == 3, T));
        var log = b.Log;
        Assert.Equal(ToyWire.GetChallenge, log[0].Type);
        Assert.Equal(seqs, log.Where(r => r.Kind == ToyWire.Data).Select(r => ToyWire.SeqOf(r.Bytes)));
        Assert.Equal(atA, a.Log.Count);
    }

    [Fact]
    public async Task A_flip_ends_the_old_backends_stream_and_releases_its_peer()
    {
        await using var rig = new Rig();
        var a = rig.Backend("a");
        var b = rig.Backend("b");
        rig.SetDefault(a);
        var c = rig.Client(Sid);
        await c.ConnectAsync(T);
        var old = rig.PeerOf(c);
        Assert.True((await a.Info.ResolveAsync(new ResolveRequest { Peer = old.ToString() })).Found);
        rig.Relay.SetRoute(rig.Next(), c.LocalEndPoint.ToString(), b.EndPoint.ToString(), Sid.ToString());
        a.SendRetry(old);
        Assert.True(await c.WaitForAsync(x => x.Connected && x.InstanceId == "b", T));
        Assert.True(await Rig.Until(() => a.Sidecar.Table.LiveCount == 0));
        Assert.False((await a.Info.ResolveAsync(new ResolveRequest { Peer = old.ToString() })).Found);
    }

    [Fact]
    public async Task An_unreachable_backend_holds_and_the_hold_expires_with_the_reason()
    {
        await using var rig = new Rig();
        rig.Relay.SetDefault(rig.Next(), "127.0.0.1:1"); // nothing listens there
        var c = rig.Client(Sid);
        c.StartHandshake();
        Assert.True(await Rig.Until(() => rig.Relay.HoldReasonOf(c.LocalEndPoint) == "backend 127.0.0.1:1 unreachable"));
        rig.Time.Advance(rig.Options.HoldTime);
        Assert.Equal("hold expired: backend 127.0.0.1:1 unreachable", Assert.Single(rig.Events.Of("closed")).Reason);
    }

    [Fact]
    public async Task A_route_change_away_from_an_unreachable_backend_releases_the_hold_at_once()
    {
        await using var rig = new Rig();
        await using var late = new Pod("late");
        var port = late.EndPoint.Port;
        await late.Sidecar.DisposeAsync(); // the sidecar is not up yet...
        rig.Relay.SetDefault(rig.Next(), $"127.0.0.1:{port}");
        var c = rig.Client(Sid);
        c.StartHandshake();
        Assert.True(await Rig.Until(() => rig.Relay.HoldReasonOf(c.LocalEndPoint)?.EndsWith("unreachable") == true));
        await using var up = new Pod("up");
        rig.Relay.SetDefault(rig.Next(), up.EndPoint.ToString()); // ...the service moves the default to one that is
        Assert.Equal("up", await c.WaitConnectedAsync(T));
    }

    [Fact]
    public async Task A_stream_that_failed_is_retried_after_the_back_off_and_released_when_the_sidecar_is_up()
    {
        await using var rig = new Rig();
        int port;
        await using (var probe = new Pod("probe")) port = probe.EndPoint.Port; // a free port, then nothing there
        rig.Relay.SetDefault(rig.Next(), $"127.0.0.1:{port}");
        var c = rig.Client(Sid);
        c.StartHandshake();
        Assert.True(await Rig.Until(() => rig.Relay.HoldReasonOf(c.LocalEndPoint)?.EndsWith("unreachable") == true));
        await using var up = new Pod("same-port", port: port);
        await Task.Delay(200);
        Assert.False(c.Connected); // still backing off on the fake clock
        // Each back-off elapsed on the gateway clock is one more attempt; the channel reconnects in real time.
        Assert.True(await Rig.Until(() => { rig.Time.Advance(rig.Options.StreamRetry); return c.Connected; }));
        Assert.Equal("same-port", c.InstanceId);
    }
}
