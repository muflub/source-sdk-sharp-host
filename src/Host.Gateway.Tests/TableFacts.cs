using SourceSharp.Host.Gateway.Relay;
using SourceSharp.Host.Gateway.Tests.Support;
using SourceSharp.Host.Proto.Gateway;

namespace SourceSharp.Host.Gateway.Tests;

/// <summary>The versioned route table (plan §8.2), through the relay's control surface.</summary>
public class TableFacts
{
    static readonly TimeSpan T = Rig.T;

    [Fact]
    public async Task A_newer_version_is_accepted_and_becomes_the_tables()
    {
        await using var rig = new Rig();
        Assert.Equal(ControlStatus.Ok, rig.Relay.SetDefault(7, "127.0.0.1:27015"));
        Assert.Equal(7UL, rig.Relay.TableVersion);
    }

    [Fact]
    public async Task A_stale_version_is_refused_and_changes_nothing()
    {
        await using var rig = new Rig();
        Assert.Equal(ControlStatus.Ok, rig.Relay.SetDefault(7, "127.0.0.1:27015"));
        Assert.Equal(ControlStatus.Stale, rig.Relay.SetDefault(7, "127.0.0.1:27016"));
        Assert.Equal(ControlStatus.Stale, rig.Relay.SetDefault(6, "127.0.0.1:27016"));
        Assert.Equal("127.0.0.1:27015", rig.Relay.DefaultBackend!.ToString());
        Assert.Equal(7UL, rig.Relay.TableVersion);
    }

    [Fact]
    public async Task A_stale_set_route_is_refused()
    {
        await using var rig = new Rig();
        rig.Relay.SetDefault(5, "127.0.0.1:27015");
        Assert.Equal(ControlStatus.Stale, rig.Relay.SetRoute(5, "127.0.0.1:1234", "127.0.0.1:27015"));
        Assert.Equal(ControlStatus.Ok, rig.Relay.SetRoute(6, "127.0.0.1:1234", "127.0.0.1:27015"));
    }

    [Fact]
    public async Task Sync_table_with_an_older_version_is_refused_and_an_equal_one_accepted()
    {
        await using var rig = new Rig();
        rig.Relay.SetDefault(5, "127.0.0.1:27015");
        Assert.Equal(ControlStatus.Stale, rig.Relay.SyncTable(new RouteTable { Version = 4, DefaultBackend = "127.0.0.1:1" }));
        Assert.Equal(ControlStatus.Ok, rig.Relay.SyncTable(new RouteTable { Version = 5, DefaultBackend = "127.0.0.1:2" }));
        Assert.Equal("127.0.0.1:2", rig.Relay.DefaultBackend!.ToString());
    }

    [Fact]
    public async Task An_unidentified_session_is_refused_a_level_route()
    {
        await using var rig = new Rig();
        var hub = rig.Backend("hub");
        var level = rig.Backend("level");
        rig.SetDefault(hub);
        var c = rig.Client(1);
        await c.ConnectAsync(T);
        Assert.Equal(ControlStatus.Unidentified, rig.Relay.SetRoute(rig.Next(), c.LocalEndPoint.ToString(), level.EndPoint.ToString()));
        Assert.Equal(hub.EndPoint.ToString(), rig.Relay.SessionOf(c.LocalEndPoint)!.Backend);
    }

    [Fact]
    public async Task An_identified_session_is_given_a_level_route()
    {
        await using var rig = new Rig();
        var hub = rig.Backend("hub");
        var level = rig.Backend("level");
        rig.SetDefault(hub);
        var c = rig.Client(1);
        await c.ConnectAsync(T);
        Assert.Equal(ControlStatus.Ok, rig.Relay.SetRoute(rig.Next(), c.LocalEndPoint.ToString(), level.EndPoint.ToString(), "76561198000000001"));
        Assert.Equal("76561198000000001", rig.Relay.SessionOf(c.LocalEndPoint)!.Steamid);
    }

    [Fact]
    public async Task A_steamid_held_by_another_live_session_is_refused()
    {
        await using var rig = new Rig();
        var hub = rig.Backend("hub");
        rig.SetDefault(hub);
        var c1 = rig.Client(1);
        var c2 = rig.Client(1);
        await Task.WhenAll(c1.ConnectAsync(T), c2.ConnectAsync(T));
        Assert.Equal(ControlStatus.Ok, rig.Relay.SetRoute(rig.Next(), c1.LocalEndPoint.ToString(), hub.EndPoint.ToString(), "76561198000000001"));
        Assert.Equal(ControlStatus.Conflict, rig.Relay.SetRoute(rig.Next(), c2.LocalEndPoint.ToString(), hub.EndPoint.ToString(), "76561198000000001"));
    }

    [Fact]
    public async Task A_silent_session_closed_by_the_service_is_taken_over()
    {
        await using var rig = new Rig();
        var hub = rig.Backend("hub");
        rig.SetDefault(hub);
        var c1 = rig.Client(1);
        var c2 = rig.Client(1);
        await Task.WhenAll(c1.ConnectAsync(T), c2.ConnectAsync(T));
        rig.Relay.SetRoute(rig.Next(), c1.LocalEndPoint.ToString(), hub.EndPoint.ToString(), "76561198000000001");
        // Single presence is the service's decision (7e); the gateway executes it through CloseSession.
        Assert.Equal(ControlStatus.Ok, rig.Relay.CloseSession(rig.Next(), c1.LocalEndPoint.ToString(), "taken over"));
        Assert.Equal(ControlStatus.Ok, rig.Relay.SetRoute(rig.Next(), c2.LocalEndPoint.ToString(), hub.EndPoint.ToString(), "76561198000000001"));
        Assert.Equal("taken over", Assert.Single(rig.Events.Of("closed")).Reason);
    }

    [Fact]
    public async Task Sync_table_after_a_restart_routes_a_known_client_to_its_backend()
    {
        await using var rig = new Rig();
        var hub = rig.Backend("hub");
        var level = rig.Backend("level");
        var c = rig.Client(1);
        // A fresh gateway knows nothing; the service's table says this client is on the level.
        Assert.Equal(ControlStatus.Ok, rig.Relay.SyncTable(new RouteTable
        {
            Version = 40,
            DefaultBackend = hub.EndPoint.ToString(),
            Routes = { new Route { ClientAddr = c.LocalEndPoint.ToString(), Backend = level.EndPoint.ToString(), Steamid = "76561198000000001", Identified = true, SessionId = "s-40" } },
        }));
        Assert.Equal("level", await c.ConnectAsync(T));
        Assert.Empty(hub.Log);
        Assert.Equal("s-40", rig.Relay.SessionOf(c.LocalEndPoint)!.SessionId);
    }

    [Fact]
    public async Task Sync_table_after_a_restart_rebinds_the_peer_the_backend_knew()
    {
        await using var rig = new Rig();
        var hub = rig.Backend("hub");
        rig.SetDefault(hub);
        var c = rig.Client(1);
        await c.ConnectAsync(T);
        var peer = rig.PeerOf(c);
        var sessionId = rig.Relay.SessionOf(c.LocalEndPoint)!.SessionId;

        // A second gateway (the restarted one) gets the table with the peer; the old one is gone.
        await rig.DisposeRelayOnlyAsync();
        await using var rig2 = new Rig(o => o.Public = $"127.0.0.1:{rig.Relay.PublicEndPoint.Port}");
        Assert.Equal(ControlStatus.Ok, rig2.Relay.SyncTable(new RouteTable
        {
            Version = 9,
            DefaultBackend = hub.EndPoint.ToString(),
            Routes = { new Route { ClientAddr = c.LocalEndPoint.ToString(), Backend = hub.EndPoint.ToString(), Peer = peer.ToString(), SessionId = sessionId } },
        }));
        // The client's netchannel continues; the hub sees the same peer and still answers.
        Assert.Equal("hub", await c.KeepaliveAsync(T));
        Assert.Single(hub.Senders);
    }

    [Fact]
    public async Task Sync_table_without_the_peer_after_a_restart_loses_the_backends_netchannel()
    {
        // The negative control of the fact above: same restart, no peer in the table.
        await using var rig = new Rig();
        var hub = rig.Backend("hub");
        rig.SetDefault(hub);
        var c = rig.Client(1);
        await c.ConnectAsync(T);
        await rig.DisposeRelayOnlyAsync();
        await using var rig2 = new Rig(o => o.Public = $"127.0.0.1:{rig.Relay.PublicEndPoint.Port}");
        rig2.Relay.SyncTable(new RouteTable
        {
            Version = 9,
            DefaultBackend = hub.EndPoint.ToString(),
            Routes = { new Route { ClientAddr = c.LocalEndPoint.ToString(), Backend = hub.EndPoint.ToString() } },
        });
        await Assert.ThrowsAsync<TimeoutException>(() => c.KeepaliveAsync(TimeSpan.FromMilliseconds(300)));
        Assert.True(await hub.WaitForAsync(_ => hub.Senders.Count == 2, T)); // it did arrive, from a peer the hub never accepted
    }

    static RouteTable Carried(ulong version, string hub) => new()
    {
        Version = version,
        DefaultBackend = hub,
        Routes = { new Route { ClientAddr = "10.9.9.9:27005", Backend = hub, Steamid = "76561198000000001", Identified = true, SessionId = "s-old" } },
    };

    [Fact]
    public async Task A_synced_route_whose_client_never_returns_is_reported_closed_after_the_expiry()
    {
        await using var rig = new Rig();
        var hub = rig.Backend("hub");
        Assert.Equal(ControlStatus.Ok, rig.Relay.SyncTable(Carried(9, hub.EndPoint.ToString())));
        rig.Time.Advance(rig.Options.SessionExpiry);
        var closed = Assert.Single(rig.Events.Of("closed"));
        Assert.Equal(("s-old", "10.9.9.9:27005"), (closed.SessionId, closed.ClientAddr));
    }

    [Fact]
    public async Task A_later_sync_does_not_restart_a_carried_routes_expiry()
    {
        await using var rig = new Rig();
        var hub = rig.Backend("hub");
        rig.Relay.SyncTable(Carried(9, hub.EndPoint.ToString()));
        rig.Time.Advance(rig.Options.SessionExpiry - TimeSpan.FromSeconds(1));
        Assert.Empty(rig.Events.Of("closed"));
        rig.Relay.SyncTable(Carried(10, hub.EndPoint.ToString()));
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("s-old", Assert.Single(rig.Events.Of("closed")).SessionId);
    }
}
