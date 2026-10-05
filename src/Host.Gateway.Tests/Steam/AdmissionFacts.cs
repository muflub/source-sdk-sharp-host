using System.Net;
using System.Net.Sockets;
using System.Text;
using SourceSharp.Host.FakeClient;
using SourceSharp.Host.Gateway.Relay;
using SourceSharp.Host.Gateway.Steam;
using SourceSharp.Host.Gateway.Tests.Support;
using SourceSharp.Host.Proto.Gateway;
using Client = SourceSharp.Host.FakeClient.FakeClient;

namespace SourceSharp.Host.Gateway.Tests.Steam;

/// <summary>The relay with Steam on (plan_steam_auth S-3, S-5, S-12…S-17), over the toy wire and the fake Steam.</summary>
public class AdmissionFacts
{
    static readonly TimeSpan T = Rig.T;
    const ulong Alice = 76561198000000001UL, Bob = 76561198000000002UL;

    static int Connects(Pod p) => p.Log.Count(r => r.Type == ToyWire.Connect);
    static int GetChallenges(Pod p) => p.Log.Count(r => r.Type == ToyWire.GetChallenge);
    static int Begins(Rig rig) => rig.Steam!.Calls.Count(c => c.StartsWith("begin:", StringComparison.Ordinal));

    static (Rig Rig, Pod Hub) Hub(Action<GatewayOptions>? configure = null)
    {
        var rig = Rig.WithSteam(configure);
        var hub = rig.Backend("hub");
        rig.SetDefault(hub);
        return (rig, hub);
    }

    static async Task<string> Rejected(Client c)
    {
        Assert.True(await Rig.Until(() => c.RejectReason is not null), $"no reject; steps: {string.Join(", ", c.Steps)}");
        return c.RejectReason!;
    }

    // ------------------------------------------------------------ validate-then-forward (S-3, S-5)

    [Fact]
    public async Task A_valid_ticket_connects_and_identify_carries_the_verdict()
    {
        var (rig, hub) = Hub();
        await using var owned = rig;
        var c = rig.Client(Alice);

        Assert.Equal("hub", await c.ConnectAsync(T));

        Assert.Equal(1, Connects(hub));
        Assert.Equal(1, Begins(rig));
        var id = Assert.Single(rig.Events.Identifies);
        Assert.True(id.SteamValidated);
        Assert.Equal((Alice.ToString(), rig.Steam!.Status.Epoch, rig.Steam.Status.GameServerSteamId, 64),
                     (id.Steamid, id.Epoch, id.GsSteamid, id.TicketSha256.Length));
        Assert.Equal("valid", rig.Relay.SessionOf(c.LocalEndPoint)!.Auth);
    }

    [Fact]
    public async Task Nothing_past_getchallenge_reaches_the_backend_before_the_verdict()
    {
        var (rig, hub) = Hub();
        await using var owned = rig;
        rig.SteamBackend!.VerdictDelay = TimeSpan.FromSeconds(3);
        var c = rig.Client(Alice);
        c.StartHandshake();

        Assert.True(await Rig.Until(() => Begins(rig) == 1));
        Assert.True(GetChallenges(hub) > 0);     // the stimulus reached the backend
        Assert.Equal(0, Connects(hub));           // but the connect is held
        Assert.Equal("validating", rig.Relay.SessionOf(c.LocalEndPoint)!.Auth);

        rig.Time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal("hub", await c.WaitConnectedAsync(T));
        Assert.Equal(1, Connects(hub));
    }

    [Fact]
    public async Task A_denied_ticket_is_rejected_with_the_reason_and_never_reaches_the_backend()
    {
        var (rig, hub) = Hub();
        await using var owned = rig;
        var c = rig.Client(Alice);
        c.TicketTail = Encoding.ASCII.GetBytes("deny:VAC banned");
        c.StartHandshake();

        Assert.Equal("Steam auth failed: VAC banned", await Rejected(c));
        Assert.Equal(0, Connects(hub));
        Assert.Empty(rig.Steam!.Active);
        Assert.Empty(rig.Events.Identifies);
    }

    [Fact]
    public async Task No_verdict_in_time_is_a_timeout_reject()
    {
        var (rig, hub) = Hub();
        await using var owned = rig;
        rig.SteamBackend!.Script = (_, _) => null;
        var c = rig.Client(Alice);
        c.StartHandshake();
        Assert.True(await Rig.Until(() => Begins(rig) == 1));

        rig.Time.Advance(rig.Options.Steam.AuthTimeout);

        Assert.Equal("Steam auth timed out", await Rejected(c));
        Assert.Equal(0, Connects(hub));
    }

    [Fact]
    public async Task Steam_unavailable_is_refused_with_the_configured_reason_and_asks_nothing()
    {
        var (rig, hub) = Hub(o => o.Steam.UnavailableReason = "Steam is down, sorry");
        await using var owned = rig;
        rig.Steam!.SetLoggedOn(false);
        var c = rig.Client(Alice);
        c.StartHandshake();

        Assert.Equal("Steam is down, sorry", await Rejected(c));
        Assert.Equal(0, Begins(rig));
        Assert.Equal(0, Connects(hub));
    }

    [Fact]
    public async Task The_same_ticket_on_a_new_handshake_needs_no_second_steam_call()
    {
        var (rig, hub) = Hub();
        await using var owned = rig;
        var c = rig.Client(Alice);
        await c.ConnectAsync(T);

        c.StartHandshake(); // a hop, or a reconnect with the cached ticket
        Assert.True(await Rig.Until(() => Connects(hub) == 2));
        Assert.Equal(1, Begins(rig));
    }

    [Fact]
    public async Task A_different_ticket_is_validated_again()
    {
        var (rig, hub) = Hub();
        await using var owned = rig;
        var c = rig.Client(Alice);
        await c.ConnectAsync(T);

        c.TicketTail = [1, 2, 3];
        c.StartHandshake();
        Assert.True(await Rig.Until(() => Connects(hub) == 2));
        Assert.Equal(2, Begins(rig));
    }

    [Fact]
    public async Task The_backends_challenge_reaches_the_client_with_the_gateways_identity()
    {
        var (rig, hub) = Hub();
        await using var owned = rig;
        using var raw = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        raw.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        raw.SendTo(ToyWire.GetChallengePacket(4242), rig.Relay.PublicEndPoint);
        var buf = new byte[256];
        raw.ReceiveTimeout = 5000;
        var n = raw.Receive(buf);

        Assert.True(ToyWire.TryReadChallenge(buf.AsSpan(0, n), out _, out var cc));
        Assert.Equal(4242, cc);
        Assert.Equal(rig.Steam!.Status.GameServerSteamId, BitConverter.ToUInt64(buf, Handshake.ToyCodec.GameServerSteamIdOffset));
        Assert.Equal(0, buf[Handshake.ToyCodec.SecureOffset]);
    }

    // ------------------------------------------------------------ one connection per SteamID (S-17)

    [Fact]
    public async Task A_second_connect_for_a_live_steamid_is_refused_with_a_wait_and_no_strike()
    {
        var (rig, hub) = Hub();
        await using var owned = rig;
        var first = rig.Client(Alice);
        await first.ConnectAsync(T);

        var second = rig.Client(Alice);
        second.TicketTail = [9];
        second.StartHandshake();

        Assert.Equal("already connected; try again in 15 s", await Rejected(second));
        Assert.True(first.Connected);
        Assert.Equal(1, Begins(rig));       // refused before Steam was asked
        Assert.Empty(rig.Events.TempBans);
    }

    [Fact]
    public async Task Once_the_holder_has_been_silent_for_the_presence_timeout_the_next_connect_wins()
    {
        var (rig, hub) = Hub();
        await using var owned = rig;
        var first = rig.Client(Alice);
        await first.ConnectAsync(T);
        var firstSession = rig.Relay.SessionOf(first.LocalEndPoint)!.SessionId;

        rig.Time.Advance(rig.Options.PresenceTimeout);
        var second = rig.Client(Alice);
        second.TicketTail = [9];
        Assert.Equal("hub", await second.ConnectAsync(T));

        Assert.Null(rig.Relay.SessionOf(first.LocalEndPoint));
        var calls = rig.Steam!.Calls.ToList();
        Assert.True(calls.IndexOf($"end:{firstSession}") < calls.LastIndexOf(calls.Last(c => c.StartsWith("begin:"))));
    }

    [Fact]
    public async Task A_player_who_left_for_good_can_rejoin_at_once_from_a_new_address()
    {
        var (rig, hub) = Hub();
        await using var owned = rig;
        var first = rig.Client(Alice);
        await first.ConnectAsync(T);
        var session = rig.Relay.SessionOf(first.LocalEndPoint)!.SessionId;

        rig.Relay.EndAuth(session); // the game's PlayerLeft, through the service

        var again = rig.Client(Alice);  // a new client port, well inside the presence timeout
        again.TicketTail = [5];
        Assert.Equal("hub", await again.ConnectAsync(T));
    }

    [Fact]
    public async Task A_restarted_gateway_with_the_same_id_never_reuses_a_session_id()
    {
        var (first, _) = Hub();
        string before;
        await using (first)
        {
            var c = first.Client(Alice);
            await c.ConnectAsync(T);
            before = first.Relay.SessionOf(c.LocalEndPoint)!.SessionId;
        }
        var (second, _) = Hub(); // the same GatewayId, as a process restarted in its pod has
        await using var owned = second;
        var c2 = second.Client(Bob);
        await c2.ConnectAsync(T);
        var after = second.Relay.SessionOf(c2.LocalEndPoint)!.SessionId;

        Assert.Equal(first.Options.GatewayId, second.Options.GatewayId);
        Assert.StartsWith(second.Options.GatewayId + "-", after);
        Assert.NotEqual(before, after);
    }

    // ------------------------------------------------------------ bans (S-14) and temp bans (S-15)

    [Fact]
    public async Task A_banned_address_gets_no_reply_and_no_session()
    {
        var (rig, hub) = Hub();
        await using var owned = rig;
        var control = rig.Client(Alice);
        await control.ConnectAsync(T);  // before the ban: answered
        var before = GetChallenges(hub);

        Assert.Equal(ControlStatus.Ok, rig.Relay.SetBans(rig.Next(), [new Ban { Kind = "address", Value = "127.0.0.0/8", Reason = "test" }]));
        var c = rig.Client(Bob);
        c.StartHandshake();
        await Task.Delay(200);

        Assert.All(c.Steps, step => Assert.Equal("q", step)); // nothing came back
        Assert.Equal(before, GetChallenges(hub));
        Assert.Null(rig.Relay.SessionOf(c.LocalEndPoint));
    }

    [Fact]
    public async Task A_banned_steamid_is_refused_before_steam_is_asked()
    {
        var (rig, hub) = Hub();
        await using var owned = rig;
        rig.Relay.SetBans(rig.Next(), [new Ban { Kind = "steamid", Value = Alice.ToString(), Reason = "cheating" }]);
        var c = rig.Client(Alice);
        c.StartHandshake();

        Assert.Equal("banned", await Rejected(c));
        Assert.Equal(0, Begins(rig));
        Assert.Equal(0, Connects(hub));

        var bob = rig.Client(Bob);  // the control
        Assert.Equal("hub", await bob.ConnectAsync(T));
    }

    [Fact]
    public async Task The_legacy_address_list_is_enforced_when_no_bans_are_given()
    {
        var (rig, _) = Hub();
        await using var owned = rig;
        var table = new RouteTable { Version = rig.Next(), DefaultBackend = rig.Relay.DefaultBackend!.ToString() };
        table.BannedAddresses.Add("127.0.0.1");
        Assert.Equal(ControlStatus.Ok, rig.Relay.SyncTable(table));
        Assert.True(rig.Relay.IsBanned(ip: IPAddress.Loopback));
    }

    [Fact]
    public async Task Repeated_denials_from_one_address_become_a_temp_ban_that_drops_its_packets()
    {
        var (rig, hub) = Hub();
        await using var owned = rig;
        var c = rig.Client(Alice);
        c.TicketTail = Encoding.ASCII.GetBytes("deny:invalid");
        for (var i = 0; i < 5; i++)
        {
            c.StartHandshake();
            Assert.True(await Rig.Until(() => c.RejectReason is not null && Begins(rig) == i + 1), $"attempt {i}");
        }

        var ban = Assert.Single(rig.Events.TempBans, b => b.Ban.Kind == "address");
        Assert.Equal("127.0.0.1", ban.Ban.Value);
        var getChallenges = GetChallenges(hub);
        c.TicketTail = [];
        c.StartHandshake();
        await Task.Delay(200);
        Assert.Equal(getChallenges, GetChallenges(hub));   // banned: the getchallenge never reached the backend
    }

    [Fact]
    public async Task Refusals_that_are_not_failures_never_ban()
    {
        var (rig, _) = Hub();
        await using var owned = rig;
        rig.Steam!.SetLoggedOn(false);
        var c = rig.Client(Alice);
        for (var i = 0; i < 8; i++)
        {
            c.StartHandshake();
            Assert.True(await Rig.Until(() => c.Steps.Count(s => s.StartsWith('9')) == i + 1));
        }
        Assert.Empty(rig.Events.TempBans);
    }

    [Fact]
    public async Task A_new_ban_kicks_the_live_session_it_covers()
    {
        var (rig, hub) = Hub();
        await using var owned = rig;
        var c = rig.Client(Alice);
        await c.ConnectAsync(T);

        rig.Relay.SetBans(rig.Next(), [new Ban { Kind = "steamid", Value = Alice.ToString(), Reason = "cheating" }]);

        var revoked = Assert.Single(rig.Events.Revocations);
        Assert.Equal((Alice.ToString(), "banned"), (revoked.Steamid, revoked.Reason));
        Assert.Equal("revoked", rig.Relay.SessionOf(c.LocalEndPoint)!.Auth);
    }

    // ------------------------------------------------------------ release (S-13) and kick (S-12)

    [Fact]
    public async Task Closing_a_session_releases_its_ticket()
    {
        var (rig, _) = Hub();
        await using var owned = rig;
        var c = rig.Client(Alice);
        await c.ConnectAsync(T);
        var session = rig.Relay.SessionOf(c.LocalEndPoint)!.SessionId;

        rig.Relay.CloseSession(rig.Next(), c.LocalEndPoint.ToString(), "admin");

        Assert.Contains($"end:{session}", rig.Steam!.Calls);
        Assert.Empty(rig.Steam.Active);
    }

    [Fact]
    public async Task EndAuth_releases_the_ticket_at_once_and_keeps_the_session()
    {
        var (rig, _) = Hub();
        await using var owned = rig;
        var c = rig.Client(Alice);
        await c.ConnectAsync(T);
        var session = rig.Relay.SessionOf(c.LocalEndPoint)!.SessionId;

        Assert.Equal(ControlStatus.Ok, rig.Relay.EndAuth(session));

        Assert.Equal([$"begin:{session}", $"end:{session}"], rig.Steam!.Calls);
        Assert.NotNull(rig.Relay.SessionOf(c.LocalEndPoint));
    }

    [Fact]
    public async Task A_revocation_stops_upstream_at_once_and_closes_after_the_kick_grace()
    {
        var (rig, hub) = Hub();
        await using var owned = rig;
        var c = rig.Client(Alice);
        await c.ConnectAsync(T);
        c.SendKeepalive();
        Assert.True(await Rig.Until(() => hub.Log.Any(r => r.Type != ToyWire.GetChallenge && r.Type != ToyWire.Connect)));

        rig.SteamBackend!.Revoke(Alice, "ticket canceled");
        var frozen = hub.Log.Count;
        var begins = Begins(rig);
        c.SendKeepalive();
        c.SendKeepalive();
        c.TicketTail = [7];   // a new handshake during the grace: neither forwarded nor validated
        c.StartHandshake();
        await Task.Delay(200);

        Assert.Equal(frozen, hub.Log.Count);
        Assert.Equal(begins, Begins(rig));
        var e = Assert.Single(rig.Events.Revocations);
        Assert.Equal("Steam: ticket canceled", e.Reason);
        Assert.NotNull(rig.Relay.SessionOf(c.LocalEndPoint));

        rig.Time.Advance(rig.Options.Steam.KickGrace);
        rig.Time.Advance(rig.Options.SweepInterval);
        Assert.Null(rig.Relay.SessionOf(c.LocalEndPoint));
    }

    // ------------------------------------------------------------ S-11: a ticket claimed under an earlier logon

    /// <summary>The client's ticket as an earlier logon of the gateway claimed it (a restart, as Steam remembers it).</summary>
    static async Task ClaimedBefore(Rig rig, Client c)
    {
        var earlier = new FakeSteamAuthServer(rig.SteamBackend!, rig.Time);
        await earlier.StartAsync(default);
        Assert.True((await earlier.BeginAsync("before-restart", c.SteamId, ToyWire.Ticket(c.SteamId, c.TicketTail), default)).Ok);
    }

    [Fact]
    public async Task A_claimed_ticket_the_service_vouches_for_is_admitted_and_reauthed_at_once()
    {
        var (rig, hub) = Hub();
        await using var owned = rig;
        var c = rig.Client(Alice);
        await ClaimedBefore(rig, c);
        rig.Events.OnIdentify = r => Task.FromResult(new IdentifyResponse { Allowed = r.TicketClaimed, Reauth = r.TicketClaimed });

        Assert.Equal("hub", await c.ConnectAsync(T));

        Assert.True(Assert.Single(rig.Events.Identifies).TicketClaimed);
        Assert.Equal("reauthing", rig.Relay.SessionOf(c.LocalEndPoint)!.Auth);
        Assert.Equal(Alice.ToString(), Assert.Single(rig.Events.Reauths).Steamid);
    }

    [Fact]
    public async Task A_claimed_ticket_the_service_does_not_vouch_for_is_refused_without_a_strike()
    {
        var (rig, hub) = Hub();
        await using var owned = rig;
        var c = rig.Client(Alice);
        await ClaimedBefore(rig, c);
        rig.Events.OnIdentify = _ => Task.FromResult(new IdentifyResponse { Allowed = false, Reason = "please reconnect" });
        c.StartHandshake();

        Assert.Equal("please reconnect", await Rejected(c));
        Assert.Equal(0, Connects(hub));
        Assert.Empty(rig.Events.TempBans);
        Assert.Equal(0, rig.Relay.Metrics.ConnectsRefused.WithLabels("steam_denied").Value);
    }
}
