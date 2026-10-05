using System.Net;
using System.Net.Sockets;
using SourceSharp.Host.FakeClient;
using SourceSharp.Host.Gateway.Steam;
using SourceSharp.Host.Gateway.Tests.Support;
using SourceSharp.Host.Proto.Gateway;

namespace SourceSharp.Host.Gateway.Tests.Steam;

/// <summary>
/// Steam's game-server UDP through the proxy socket (plan_steam_auth S-19) and the listing (S-18): the library
/// opens no socket of its own; what is meant for it comes in through the public socket, and what it sends goes out
/// of it.
/// </summary>
public class BridgeFacts
{
    static readonly byte[] MasterPacket = [0xFF, 0xFF, 0xFF, 0xFF, 0x73, 0x0A, 1, 2, 3];

    static Socket Udp()
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ReceiveTimeout = 5000 };
        s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return s;
    }

    static (Rig Rig, Pod Hub) Hub(Action<GatewayOptions>? configure = null)
    {
        var rig = Rig.WithSteam(configure);
        var hub = rig.Backend("hub");
        rig.SetDefault(hub);
        return (rig, hub);
    }

    [Fact]
    public async Task A_packet_for_steam_reaches_the_library_with_its_senders_address()
    {
        var (rig, hub) = Hub();
        await using var owned = rig;
        using var sender = Udp();
        sender.SendTo(MasterPacket, rig.Relay.PublicEndPoint);

        Assert.True(await Rig.Until(() => !rig.Steam!.Incoming.IsEmpty));
        Assert.True(rig.Steam!.Incoming.TryPeek(out var got));
        Assert.Equal(MasterPacket, got.Packet);
        Assert.Equal(sender.LocalEndPoint, got.From);
        Assert.Empty(hub.Log); // nothing of it reached a game pod
        Assert.Null(rig.Relay.SessionOf((IPEndPoint)sender.LocalEndPoint!));
    }

    [Fact]
    public async Task What_the_library_sends_leaves_the_public_socket_within_one_tick()
    {
        var (rig, _) = Hub();
        await using var owned = rig;
        using var master = Udp();
        rig.Steam!.Send([0xFF, 0xFF, 0xFF, 0xFF, 0x71, 9], (IPEndPoint)master.LocalEndPoint!);

        rig.Time.Advance(rig.Options.Steam.OutgoingTick);

        var buf = new byte[64];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        var n = master.ReceiveFrom(buf, ref from);
        Assert.Equal([0xFF, 0xFF, 0xFF, 0xFF, 0x71, 9], buf[..n]);
        Assert.Equal(rig.Relay.PublicEndPoint, from);
    }

    [Fact]
    public async Task The_handshake_and_a2s_never_reach_the_library_by_default()
    {
        var (rig, hub) = Hub();
        await using var owned = rig;
        rig.Relay.SetServerInfo(new ServerInfo { Name = "x", Map = "m" });
        using var c = Udp();
        c.SendTo(ToyWire.GetChallengePacket(1), rig.Relay.PublicEndPoint);
        c.SendTo(ToyWire.ConnectPacket(1, 76561198000000001UL, "a"), rig.Relay.PublicEndPoint);
        c.SendTo([0xFF, 0xFF, 0xFF, 0xFF, 0x54, .. "Source Engine Query\0"u8], rig.Relay.PublicEndPoint);
        Assert.True(await Rig.Until(() => hub.Log.Count > 0)); // the getchallenge went where it belongs
        await Task.Delay(100);
        Assert.Empty(rig.Steam!.Incoming);
    }

    [Fact]
    public async Task With_the_library_answering_a2s_queries_go_to_it()
    {
        var (rig, _) = Hub(o => o.Steam.A2s = A2sAnswerer.Library);
        await using var owned = rig;
        using var c = Udp();
        c.SendTo([0xFF, 0xFF, 0xFF, 0xFF, 0x54, .. "Source Engine Query\0"u8], rig.Relay.PublicEndPoint);
        Assert.True(await Rig.Until(() => !rig.Steam!.Incoming.IsEmpty));
    }

    [Fact]
    public async Task A_banned_address_never_reaches_the_library()
    {
        var (rig, _) = Hub();
        await using var owned = rig;
        rig.Relay.SetBans(rig.Next(), [new Ban { Kind = "address", Value = "127.0.0.0/8", Reason = "t" }]);
        using var sender = Udp();
        sender.SendTo(MasterPacket, rig.Relay.PublicEndPoint);
        await Task.Delay(150);
        Assert.Empty(rig.Steam!.Incoming);
    }

    [Fact]
    public async Task Logged_off_nothing_is_handed_to_the_library()
    {
        var (rig, _) = Hub();
        await using var owned = rig;
        rig.Steam!.SetLoggedOn(false);
        using var sender = Udp();
        sender.SendTo(MasterPacket, rig.Relay.PublicEndPoint);
        await Task.Delay(150);
        Assert.Empty(rig.Steam.Incoming);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_service_entry_becomes_the_steam_listing(bool advertise)
    {
        var (rig, _) = Hub(o => o.Steam.Advertise = advertise);
        await using var owned = rig;
        var info = new ServerInfo { Name = "Descent (2 open)", Map = "depth 7", Game = "TF2 Descent", MaxPlayers = 40, Tags = "rpg", Region = "eu" };
        info.Rules["deepest"] = "7";

        rig.Relay.SetServerInfo(info);

        var (l, adv) = rig.Steam!.Listing!.Value;
        Assert.Equal(("Descent (2 open)", "depth 7", "TF2 Descent", 40, "rpg", "eu", advertise), (l.Name, l.Map, l.Product, l.MaxPlayers, l.Tags, l.Region, adv));
        Assert.Equal("7", l.Rules["deepest"]);
    }

    [Fact]
    public async Task The_snapshot_carries_steams_state_for_the_admin()
    {
        var (rig, _) = Hub(o => o.Steam.Mode = SteamMode.Fake);
        await using var owned = rig;
        var svc = new Control.GatewayControlService(rig.Relay);

        var snap = await svc.Snapshot(new SnapshotRequest(), null!);

        Assert.Equal(("Fake", true, rig.Steam!.Status.GameServerSteamId, rig.Steam.Status.Epoch),
                     (snap.SteamMode, snap.SteamLoggedOn, snap.GsSteamid, snap.SteamEpoch));
        rig.Steam.SetLoggedOn(false);
        Assert.False((await svc.Snapshot(new SnapshotRequest(), null!)).SteamLoggedOn);
    }
}
