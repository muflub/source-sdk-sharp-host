using System.Text;
using SourceSharp.Host.FakeClient;
using SourceSharp.Host.Gateway.Relay;
using SourceSharp.Host.Gateway.Tests.Support;
using SourceSharp.Host.Proto.Gateway;
using Client = SourceSharp.Host.FakeClient.FakeClient;

namespace SourceSharp.Host.Gateway.Tests.Steam;

/// <summary>Re-auth after a gateway restart (plan_steam_auth S-11, S-20): carried sessions keep playing while a fresh ticket is asked for.</summary>
public class ReauthFacts
{
    const ulong Alice = 76561198000000001UL;
    static readonly byte[] Fresh = ToyWire.Ticket(Alice, "fresh"u8);

    /// <summary>A rig whose table carries an identified route for a client, as a restarted gateway's does; the client's
    /// first datagram after it makes the carried session.</summary>
    static async Task<(Rig Rig, Pod Hub, Client C, string SessionId)> Carried(Action<GatewayOptions>? configure = null)
    {
        // The client is silent while the clock jumps by minutes: keep its session from expiring, which is not the subject.
        var rig = Rig.WithSteam(o => { o.SessionExpiry = TimeSpan.FromHours(1); configure?.Invoke(o); });
        var hub = rig.Backend("hub");
        var c = rig.Client(Alice);
        var table = new RouteTable { Version = rig.Next(), DefaultBackend = hub.EndPoint.ToString() };
        table.Routes.Add(new Route { SessionId = "carried-1", ClientAddr = c.LocalEndPoint.ToString(), Backend = hub.EndPoint.ToString(), Steamid = Alice.ToString(), Identified = true });
        Assert.Equal(ControlStatus.Ok, rig.Relay.SyncTable(table));
        c.SendKeepalive();
        // Relaying, not still holding its first datagram: a clock jump past HoldTime would close a holding session.
        Assert.True(await Rig.Until(() => rig.Relay.SessionOf(c.LocalEndPoint)?.State == "relaying"));
        return (rig, hub, c, "carried-1");
    }

    static void Sweep(Rig rig) => rig.Time.Advance(rig.Options.SweepInterval);

    /// <summary>The client's session's auth state; a closed session fails with the reason the relay gave.</summary>
    static string AuthOf(Rig rig, Client c) =>
        rig.Relay.SessionOf(c.LocalEndPoint)?.Auth
        ?? throw new Xunit.Sdk.XunitException($"the session closed: {string.Join("; ", rig.Events.Of("closed").Select(e => e.Reason))}");

    [Fact]
    public async Task A_client_heard_before_the_table_arrives_still_resumes_its_carried_session()
    {
        // Live at 8ca1a2e+: a restarted gateway hears a playing client ~2 s before the service's SyncTable, and the
        // packet made a fresh session the carried route then never reached (auth none, a new id, stranded).
        var rig = Rig.WithSteam();
        await using var owned = rig;
        var hub = rig.Backend("hub");
        var c = rig.Client(Alice);
        c.SendKeepalive();
        Assert.True(await Rig.Until(() => rig.Relay.SessionOf(c.LocalEndPoint) is not null));
        Assert.Equal("none", rig.Relay.SessionOf(c.LocalEndPoint)!.Auth);

        var table = new RouteTable { Version = rig.Next(), DefaultBackend = hub.EndPoint.ToString() };
        table.Routes.Add(new Route { SessionId = "carried-1", ClientAddr = c.LocalEndPoint.ToString(), Backend = hub.EndPoint.ToString(), Steamid = Alice.ToString(), Identified = true });
        Assert.Equal(ControlStatus.Ok, rig.Relay.SyncTable(table));

        var s = rig.Relay.SessionOf(c.LocalEndPoint)!;
        Assert.Equal(("carried-1", "reauthing"), (s.SessionId, s.Auth));
        c.SendKeepalive();
        Assert.True(await Rig.Until(() => hub.Log.Any(r => r.From is not null && r.Type != ToyWire.GetChallenge)));
    }

    [Fact]
    public async Task A_carried_route_never_takes_over_a_session_already_being_admitted()
    {
        var rig = Rig.WithSteam();
        await using var owned = rig;
        var hub = rig.Backend("hub");
        rig.SetDefault(hub);
        var c = rig.Client(Alice);
        Assert.Equal("hub", await c.ConnectAsync(Rig.T));
        var before = rig.Relay.SessionOf(c.LocalEndPoint)!;
        Assert.Equal("valid", before.Auth);

        var table = new RouteTable { Version = rig.Next(), DefaultBackend = hub.EndPoint.ToString() };
        table.Routes.Add(new Route { SessionId = "carried-1", ClientAddr = c.LocalEndPoint.ToString(), Backend = hub.EndPoint.ToString(), Steamid = Alice.ToString(), Identified = true });
        Assert.Equal(ControlStatus.Ok, rig.Relay.SyncTable(table));

        Assert.Equal((before.SessionId, "valid"), (rig.Relay.SessionOf(c.LocalEndPoint)!.SessionId, rig.Relay.SessionOf(c.LocalEndPoint)!.Auth));
    }

    [Fact]
    public async Task A_carried_session_keeps_relaying_while_it_reauths()
    {
        var (rig, hub, c, _) = await Carried();
        await using var owned = rig;
        Assert.Equal("reauthing", rig.Relay.SessionOf(c.LocalEndPoint)!.Auth);
        Assert.True(await Rig.Until(() => hub.Log.Any(r => r.From is not null && r.Type != ToyWire.GetChallenge)));
    }

    [Fact]
    public async Task The_sweep_asks_once_with_a_nonce_and_the_fresh_ticket_validates_it()
    {
        var (rig, _, c, id) = await Carried();
        await using var owned = rig;
        Sweep(rig);
        var ask = Assert.Single(rig.Events.Reauths);
        Assert.Equal((id, Alice.ToString(), rig.Steam!.Status.GameServerSteamId), (ask.SessionId, ask.Steamid, ask.GsSteamid));
        Sweep(rig);
        Assert.Single(rig.Events.Reauths); // not asked again before the deadline

        var r = await rig.Relay.ReauthAsync(id, ask.Nonce, Fresh);

        Assert.Equal("Ok", r.Outcome);
        Assert.Equal((rig.Steam.Status.Epoch, 64), (r.Epoch, r.TicketSha256.Length));
        Assert.Equal("valid", rig.Relay.SessionOf(c.LocalEndPoint)!.Auth);
    }

    [Fact]
    public async Task A_ticket_with_another_nonce_is_stale_and_asks_steam_nothing()
    {
        var (rig, _, _, id) = await Carried();
        await using var owned = rig;
        Sweep(rig);
        var r = await rig.Relay.ReauthAsync(id, "not-the-nonce", Fresh);
        Assert.Equal("Stale", r.Outcome);
        Assert.Empty(rig.Steam!.Calls);
    }

    [Fact]
    public async Task A_denied_fresh_ticket_revokes_the_session()
    {
        var (rig, _, c, id) = await Carried();
        await using var owned = rig;
        Sweep(rig);
        var r = await rig.Relay.ReauthAsync(id, Assert.Single(rig.Events.Reauths).Nonce, ToyWire.Ticket(Alice, Encoding.ASCII.GetBytes("deny:VAC banned")));
        Assert.Equal("Denied", r.Outcome);
        Assert.Equal("Steam: VAC banned", Assert.Single(rig.Events.Revocations).Reason);
        Assert.Equal("revoked", rig.Relay.SessionOf(c.LocalEndPoint)!.Auth);
    }

    [Fact]
    public async Task Unanswered_it_is_asked_again_and_then_kicked()
    {
        var (rig, _, c, _) = await Carried();
        await using var owned = rig;
        Sweep(rig);
        rig.Time.Advance(rig.Options.Steam.ReauthTimeout);
        Sweep(rig);
        Assert.Equal(2, rig.Events.Reauths.Count);
        Assert.NotEqual(rig.Events.Reauths.First().Nonce, rig.Events.Reauths.Last().Nonce);
        Assert.Empty(rig.Events.Revocations);

        rig.Time.Advance(rig.Options.Steam.ReauthTimeout);
        Sweep(rig);

        Assert.Equal("please reconnect: no fresh Steam ticket", Assert.Single(rig.Events.Revocations).Reason);
        Assert.Equal("revoked", rig.Relay.SessionOf(c.LocalEndPoint)?.Auth ?? "closed");
    }

    [Fact]
    public async Task Unanswered_with_trust_it_stays_and_is_trusted()
    {
        var (rig, _, c, _) = await Carried(o => o.Steam.ReauthUnanswered = ReauthUnanswered.Trust);
        await using var owned = rig;
        for (var i = 0; i < 3; i++) { Sweep(rig); rig.Time.Advance(rig.Options.Steam.ReauthTimeout); }
        Sweep(rig);
        Assert.Empty(rig.Events.Revocations);
        Assert.Equal("valid", AuthOf(rig, c));
    }

    [Fact]
    public async Task While_steam_is_unavailable_nobody_is_asked_or_kicked()
    {
        var (rig, _, c, _) = await Carried();
        await using var owned = rig;
        rig.Steam!.SetLoggedOn(false);
        for (var i = 0; i < 5; i++) { rig.Time.Advance(rig.Options.Steam.ReauthTimeout); Sweep(rig); }
        Assert.Empty(rig.Events.Reauths);
        Assert.Empty(rig.Events.Revocations);
        Assert.Equal("reauthing", AuthOf(rig, c));

        rig.Steam.SetLoggedOn(true);  // back: the ask goes out
        Sweep(rig);
        Assert.Single(rig.Events.Reauths);
    }
}
