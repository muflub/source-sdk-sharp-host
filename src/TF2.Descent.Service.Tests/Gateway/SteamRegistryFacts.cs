using SourceSharp.Host.Abstractions;
using TF2.Descent.Service.Gateway;
using G = SourceSharp.Host.Proto.Gateway;

namespace TF2.Descent.Service.Tests.Gateway;

/// <summary>The service's side of plan_steam_auth: bans, verdict rows, release on leave, kicks, temp bans, readmission.</summary>
public class SteamRegistryFacts
{
    const string HubBackend = "10.42.0.9:5010";
    const string Alice = "76561198000000001";
    static readonly string Hash = new('a', 64);

    static G.IdentifyRequest Validated(string session, string client = "1.2.3.4:27005", string epoch = "e1", string? hash = null) => new()
    {
        RequestId = "id-" + session, SessionId = session, ClientAddr = client, Steamid = Alice,
        SteamValidated = true, TicketSha256 = hash ?? Hash, Epoch = epoch, GsSteamid = 90071992547409921UL,
    };

    static G.IdentifyRequest Claimed(string session, string epoch, string? hash = null) => new()
    {
        RequestId = "id-" + session, SessionId = session, ClientAddr = "1.2.3.4:27006", Steamid = Alice,
        TicketClaimed = true, TicketSha256 = hash ?? Hash, Epoch = epoch, GsSteamid = 90071992547409921UL,
    };

    [Fact]
    public async Task Identify_refuses_a_steamid_banned_in_the_table()
    {
        var (h, r, _, _) = await GatewayRegistryFacts.RegistryWithCommands();
        await using var _ = h;
        await h.W(tx => tx.Bans.Set(new BanRecord("steamid", Alice, null, "admin", "cheating", tx.Now)));
        var resp = await r.Identify(Validated("s1"), default);
        Assert.Equal((false, "banned"), (resp.Allowed, resp.Reason));
        Assert.Null(await h.R(tx => tx.SteamAuth.Get("s1")));
    }

    [Fact]
    public async Task A_validated_identify_writes_the_verdict_row()
    {
        var (h, r, _, _) = await GatewayRegistryFacts.RegistryWithCommands();
        await using var _ = h;
        Assert.True((await r.Identify(Validated("s1"), default)).Allowed);
        var row = await h.R(tx => tx.SteamAuth.Get("s1"));
        Assert.Equal((Alice, Hash, "e1", "valid", 90071992547409921UL), (row!.SteamId, row.TicketSha256, row.Epoch, row.State, row.GsSteamId));
    }

    [Fact]
    public async Task A_validated_identify_takes_over_a_recent_row_that_an_unvalidated_one_may_not()
    {
        var (h, r, _, _) = await GatewayRegistryFacts.RegistryWithCommands();
        await using var _ = h;
        await r.Identify(Validated("old", client: "1.2.3.4:1"), default);

        // The control: without Steam's verdict the service's own single presence refuses.
        var plain = await r.Identify(new G.IdentifyRequest { SessionId = "plain", ClientAddr = "1.2.3.4:2", Steamid = Alice }, default);
        Assert.False(plain.Allowed);

        // With it, the gateway already decided presence: the stale row is taken over.
        var resp = await r.Identify(Validated("new", client: "1.2.3.4:3"), default);
        Assert.True(resp.Allowed);
        Assert.Equal(["1.2.3.4:1"], resp.CloseSessions);
        Assert.Equal("closed", (await h.R(tx => tx.Sessions.Get("old")))!.State);
        Assert.Null(await h.R(tx => tx.SteamAuth.Get("old")));
    }

    [Fact]
    public async Task Closing_a_session_deletes_its_verdict()
    {
        var (h, r, _, _) = await GatewayRegistryFacts.RegistryWithCommands();
        await using var _ = h;
        await r.Identify(Validated("s1"), default);
        await r.SessionClosed(new G.SessionEvent { RequestId = "c1", SessionId = "s1", Reason = "expired" }, default);
        Assert.Null(await h.R(tx => tx.SteamAuth.Get("s1")));
    }

    [Fact]
    public async Task A_real_leave_releases_the_ticket_and_a_hop_does_not()
    {
        var (h, r, c, _) = await GatewayRegistryFacts.RegistryWithCommands();
        await using var _ = h;
        await r.SessionOpened(new G.SessionEvent { RequestId = "o1", SessionId = "s1", ClientAddr = "1.2.3.4:27005", Backend = HubBackend, Peer = "127.1.0.1:29005" }, default);
        await r.SessionOpened(new G.SessionEvent { RequestId = "o2", SessionId = "s2", ClientAddr = "1.2.3.5:27005", Backend = HubBackend, Peer = "127.1.0.2:29005" }, default);

        // s2 hops: travel moves its route to the level before the hub reports PlayerLeft.
        await h.W(async tx => { var s = (await tx.Sessions.Get("s2"))!; await tx.Sessions.Upsert(s with { Backend = "10.42.0.10:5010", Peer = "" }); return true; });
        await r.PlayerLeft(h.Hub, "127.1.0.2:29005", Alice, default);
        Assert.DoesNotContain(c.Calls, x => x.StartsWith("endauth", StringComparison.Ordinal));

        await r.PlayerLeft(h.Hub, "127.1.0.1:29005", Alice, default);
        Assert.Equal(["endauth s1"], c.Calls.Where(x => x.StartsWith("endauth", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_revocation_kicks_the_player_at_its_instance_with_the_reason()
    {
        var (h, r, _, k) = await GatewayRegistryFacts.RegistryWithCommands();
        await using var _ = h;
        await r.SessionOpened(new G.SessionEvent { RequestId = "o1", SessionId = "s1", ClientAddr = "1.2.3.4:27005", Backend = HubBackend, Peer = "127.1.0.1:29005" }, default);

        await r.AuthRevoked(new G.AuthRevokedEvent { SessionId = "s1", Steamid = Alice, Backend = HubBackend, Reason = "Steam: VAC banned" }, default);

        var (instance, command) = Assert.Single(k.Sent);
        Assert.Equal(h.Hub.Id, instance);
        Assert.Equal(new InstanceCommand.Kick(Alice, "Steam: VAC banned"), command);
    }

    [Fact]
    public async Task A_temp_ban_from_the_gateway_is_stored_and_audited()
    {
        var (h, r, _, _) = await GatewayRegistryFacts.RegistryWithCommands();
        await using var _ = h;
        var until = h.D.Clock.GetUtcNow().AddMinutes(10);
        await r.TempBanned(new G.TempBannedEvent { Ban = new G.Ban { Kind = "address", Value = "203.0.113.7", UntilUnixMs = until.ToUnixTimeMilliseconds(), Reason = "strikes" }, Strikes = 5 }, default);

        var ban = Assert.Single(await h.R(tx => tx.Bans.List()));
        Assert.Equal(("address", "203.0.113.7", until, "auto"), (ban.Kind, ban.Value, ban.Until, ban.Source));
        Assert.Equal(1, await h.R(tx => tx.Audit.Count(new AuditQuery(Action: "ban.auto"))));
    }

    // ------------------------------------------------------------ S-11: a claimed ticket after a restart

    [Fact]
    public async Task A_claimed_ticket_validated_under_an_earlier_logon_is_readmitted_once()
    {
        var (h, r, _, _) = await GatewayRegistryFacts.RegistryWithCommands();
        await using var _ = h;
        await r.Identify(Validated("before", epoch: "e1"), default);

        var first = await r.Identify(Claimed("after", epoch: "e2"), default);
        Assert.Equal((true, true), (first.Allowed, first.Reauth));
        Assert.Equal("reauthing", (await h.R(tx => tx.SteamAuth.Get("after")))!.State);

        var again = await r.Identify(Claimed("again", epoch: "e2"), default);
        Assert.Equal((false, "please reconnect"), (again.Allowed, again.Reason));
    }

    [Theory]
    [InlineData("other hash")]
    [InlineData("same epoch")]
    [InlineData("too old")]
    public async Task A_claimed_ticket_the_service_cannot_vouch_for_is_refused(string why)
    {
        var (h, r, _, _) = await GatewayRegistryFacts.RegistryWithCommands();
        await using var _ = h;
        await r.Identify(Validated("before", epoch: "e1"), default);
        if (why == "too old") h.D.Clock.Advance(TimeSpan.FromHours(6) + TimeSpan.FromSeconds(1));

        var resp = await r.Identify(why switch
        {
            "other hash" => Claimed("after", epoch: "e2", hash: new string('b', 64)),
            "same epoch" => Claimed("after", epoch: "e1"),
            _ => Claimed("after", epoch: "e2"),
        }, default);

        Assert.Equal((false, "please reconnect"), (resp.Allowed, resp.Reason));
    }

    [Fact]
    public async Task The_table_carries_the_live_bans_and_the_stored_options()
    {
        await using var h = await TF2.Descent.Service.Tests.Ledger.LedgerHarness.Create();
        var now = h.D.Clock.GetUtcNow();
        await h.W(tx => tx.Bans.Set(new BanRecord("address", "203.0.113.7", null, "admin", "", now)));
        await h.W(tx => tx.Bans.Set(new BanRecord("steamid", Alice, now.AddMinutes(-1), "auto", "", now)));   // expired
        await h.W(async tx => { await tx.Settings.Set(GatewayControlClient.OptionsKey, Google.Protobuf.JsonFormatter.Default.Format(new G.LiveOptions { PresenceTimeoutMs = 9000 }), "admin"); return true; });
        var client = new GatewayControlClient(h.Options, h.D.Data, h.D.Clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<GatewayControlClient>.Instance);

        var table = await client.BuildTable(default);

        Assert.Equal(["203.0.113.7"], table.Bans.Select(b => b.Value));
        Assert.Equal(9000, table.Options.PresenceTimeoutMs);
    }

    [Fact]
    public async Task An_address_ban_from_the_admin_is_stored_and_pushed_and_garbage_is_refused()
    {
        await using var h = await TF2.Descent.Service.Tests.Ledger.LedgerHarness.Create();
        var control = new RecordingGatewayControl();
        var admin = new AdminGateway(control, h.D.Data, h.Options, new GatewayState(), h.D.Clock);

        await admin.BanAddress("198.51.100.0/24", "abuse");
        await Assert.ThrowsAsync<HostRefusal>(() => admin.BanAddress("not-an-address", null));

        var ban = Assert.Single(await h.R(tx => tx.Bans.List()));
        Assert.Equal(("address", "198.51.100.0/24", "admin", "abuse"), (ban.Kind, ban.Value, ban.Source, ban.Reason));
        Assert.Equal(["bans"], control.Calls);
    }

    [Fact]
    public async Task A_join_claiming_another_steamid_than_the_peers_session_is_refused()
    {
        var (h, r, c, _) = await GatewayRegistryFacts.RegistryWithCommands();
        await using var _ = h;
        await r.SessionOpened(new G.SessionEvent { RequestId = "o1", SessionId = "s1", ClientAddr = "1.2.3.4:27005", Backend = HubBackend, Peer = "127.1.0.1:29005", Steamid = Alice }, default);

        var other = await r.PlayerJoined(h.Hub, "127.1.0.1:29005", "76561198000000099", default);
        Assert.Equal((false, $"steamid_mismatch: this peer is {Alice}"), (other.Allowed, other.Reason));

        var same = await r.PlayerJoined(h.Hub, "127.1.0.1:29005", Alice, default);   // the control
        Assert.True(same.Allowed);
    }
}
