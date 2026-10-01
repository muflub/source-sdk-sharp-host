using Descent.Service.Gateway;
using Descent.Service.Tests.Ledger;
using Microsoft.Extensions.Logging.Abstractions;
using SourceSharp.Host.Abstractions;
using G = SourceSharp.Host.Proto.Gateway;

namespace Descent.Service.Tests.Gateway;

/// <summary>A GatewayControl that records what the service told the gateway.</summary>
public sealed class RecordingGatewayControl : IGatewayControl
{
    public List<string> Calls { get; } = [];
    public int Syncs { get; private set; }
    public long TableVersion { get; private set; } = 1;
    public Task SetRoute(string clientAddr, string backend, string? steamId, bool holdUntilReady, CancellationToken ct = default)
    { Calls.Add($"route {clientAddr} -> {backend} steam={steamId} hold={holdUntilReady}"); TableVersion++; return Task.CompletedTask; }
    public Task CloseSession(string clientAddr, string reason, CancellationToken ct = default)
    { Calls.Add($"close {clientAddr} {reason}"); TableVersion++; return Task.CompletedTask; }
    public Task SetDefault(string backend, CancellationToken ct = default) { Calls.Add($"default {backend}"); TableVersion++; return Task.CompletedTask; }
    public Task BackendReady(string backend, bool ready, CancellationToken ct = default) { Calls.Add($"ready {backend} {ready}"); TableVersion++; return Task.CompletedTask; }
    public Task SyncTable(CancellationToken ct = default) { Syncs++; return Task.CompletedTask; }
}

/// <summary>Plan §8.2 and 7e: the service's session registry.</summary>
public class GatewayRegistryFacts
{
    static async Task<(LedgerHarness H, GatewayRegistry R, RecordingGatewayControl C)> Registry()
    {
        var h = await LedgerHarness.Create();
        var control = new RecordingGatewayControl();
        var registry = new GatewayRegistry(h.D.Data, control, h.Options, h.D.Clock, NullLogger<GatewayRegistry>.Instance);
        return (h, registry, control);
    }

    // LedgerHarness instances have PodIp 10.42.0.9; the relay port is 5010 by default.
    const string HubBackend = "10.42.0.9:5010";

    static G.SessionEvent Opened(string id, string client, string backend, string peer) =>
        new() { RequestId = "r-" + id, SessionId = id, ClientAddr = client, Backend = backend, Peer = peer };

    [Fact]
    public async Task An_opened_session_is_stored_against_its_backends_instance()
    {
        var (h, r, _) = await Registry();
        await using var _ = h;
        await r.SessionOpened(Opened("s1", "1.2.3.4:27005", HubBackend, "127.1.0.1:29005"), default);
        var s = await h.R(tx => tx.Sessions.Get("s1"));
        Assert.Equal(("1.2.3.4:27005", HubBackend, "127.1.0.1:29005", "open"), (s!.ClientAddr, s.Backend, s.Peer, s.State));
        Assert.NotNull(s.InstanceId);
    }

    [Fact]
    public async Task PlayerJoined_returns_the_real_address_and_routes_with_the_steamid()
    {
        var (h, r, c) = await Registry();
        await using var _ = h;
        await r.SessionOpened(Opened("s1", "1.2.3.4:27005", HubBackend, "127.1.0.1:29005"), default);
        var joined = await r.PlayerJoined(h.Hub, "127.1.0.1:29005", "765", default);
        Assert.Equal((true, "s1", "1.2.3.4:27005"), (joined.Allowed, joined.SessionId, joined.ClientAddr));
        Assert.Contains($"route 1.2.3.4:27005 -> {HubBackend} steam=765 hold=False", c.Calls);
    }

    [Fact]
    public async Task A_join_reported_before_the_gateways_session_event_binds_when_the_event_arrives()
    {
        var (h, r, c) = await Registry();
        await using var _ = h;
        var joined = await r.PlayerJoined(h.Hub, "127.1.0.1:29005", "765", default);
        Assert.True(joined.Allowed);
        await r.SessionOpened(Opened("s1", "1.2.3.4:27005", HubBackend, "127.1.0.1:29005"), default);
        var s = await h.R(tx => tx.Sessions.Get("s1"));
        Assert.Equal(("765", h.Hub.Id), (s!.SteamId, s.InstanceId));
        Assert.NotNull(s.IdentifiedAt);
        Assert.Contains($"route 1.2.3.4:27005 -> {HubBackend} steam=765 hold=False", c.Calls);
    }

    [Fact]
    public async Task A_join_left_pending_past_the_limit_does_not_bind_a_later_session()
    {
        var (h, r, _) = await Registry();
        await using var _ = h;
        await r.PlayerJoined(h.Hub, "127.1.0.1:29005", "765", default);
        h.D.Clock.Advance(GatewayRegistry.PendingJoinLimit + TimeSpan.FromSeconds(1));
        await r.SessionOpened(Opened("s1", "1.2.3.4:27005", HubBackend, "127.1.0.1:29005"), default);
        Assert.Null((await h.R(tx => tx.Sessions.Get("s1")))!.SteamId);
    }

    [Fact]
    public async Task A_second_live_session_for_one_steamid_is_refused()
    {
        var (h, r, _) = await Registry();
        await using var _ = h;
        await r.SessionOpened(Opened("s1", "1.2.3.4:27005", HubBackend, "127.1.0.1:29005"), default);
        await r.PlayerJoined(h.Hub, "127.1.0.1:29005", "765", default);
        await r.SessionOpened(Opened("s2", "5.6.7.8:27005", HubBackend, "127.1.0.2:29005"), default);
        var second = await r.PlayerJoined(h.Hub, "127.1.0.2:29005", "765", default);
        Assert.Equal((false, "single_presence"), (second.Allowed, second.Reason));
    }

    [Fact]
    public async Task A_silent_session_is_taken_over_and_closed_at_the_gateway()
    {
        var (h, r, c) = await Registry();
        await using var _ = h;
        await r.SessionOpened(Opened("s1", "1.2.3.4:27005", HubBackend, "127.1.0.1:29005"), default);
        await r.PlayerJoined(h.Hub, "127.1.0.1:29005", "765", default);
        h.D.Clock.Advance(GatewayRegistry.SilentAfter + TimeSpan.FromSeconds(1));
        await r.SessionOpened(Opened("s2", "5.6.7.8:27005", HubBackend, "127.1.0.2:29005"), default);
        var second = await r.PlayerJoined(h.Hub, "127.1.0.2:29005", "765", default);
        Assert.True(second.Allowed);
        Assert.Contains("close 1.2.3.4:27005 taken_over", c.Calls);
        Assert.Equal("closed", (await h.R(tx => tx.Sessions.Get("s1")))!.State);
    }

    [Fact]
    public async Task Closing_an_identified_session_releases_its_characters_lease_at_once()
    {
        var (h, r, _) = await Registry();
        await using var _ = h;
        var ch = await h.Character(account: "765");
        await h.Lease(ch, h.Hub);
        await r.SessionOpened(Opened("s1", "1.2.3.4:27005", HubBackend, "127.1.0.1:29005"), default);
        await r.PlayerJoined(h.Hub, "127.1.0.1:29005", "765", default);
        await r.SessionClosed(new G.SessionEvent { RequestId = "c1", SessionId = "s1", Reason = "expired" }, default);
        Assert.Null(await h.R(tx => tx.Leases.Get(ch)));
    }

    [Fact]
    public async Task Closing_an_unidentified_session_releases_nothing()
    {
        var (h, r, _) = await Registry();
        await using var _ = h;
        var ch = await h.Character(account: "765");
        await h.Lease(ch, h.Hub);
        await r.SessionOpened(Opened("s1", "1.2.3.4:27005", HubBackend, "127.1.0.1:29005"), default);
        await r.SessionClosed(new G.SessionEvent { RequestId = "c1", SessionId = "s1", Reason = "expired" }, default);
        Assert.NotNull(await h.R(tx => tx.Leases.Get(ch)));
    }

    [Fact]
    public async Task A_peer_resolves_only_within_its_own_backend()
    {
        var (h, r, _) = await Registry();
        await using var _ = h;
        await r.SessionOpened(Opened("s1", "1.2.3.4:27005", HubBackend, "127.1.0.1:29005"), default);
        var other = await h.AddInstance("lvl-x", InstanceKind.Level, 2);
        var otherPod = await h.W(tx => tx.Instances.Update(other.Id, i => i with { PodIp = "10.42.0.77" }));
        Assert.True((await r.ResolvePeer(h.Hub, "127.1.0.1:29005", default)).Found);
        Assert.False((await r.ResolvePeer(otherPod, "127.1.0.1:29005", default)).Found);
    }

    [Fact]
    public async Task A_live_hub_is_marked_ready_and_becomes_the_default()
    {
        var (h, r, c) = await Registry();
        await using var _ = h;
        await r.InstanceLive(h.Hub, default);
        Assert.Equal([$"ready {HubBackend} True", $"default {HubBackend}"], c.Calls);
    }

    [Fact]
    public async Task Sessions_of_a_departing_level_fall_back_to_the_hub()
    {
        var (h, r, c) = await Registry();
        await using var _ = h;
        var lvl = await h.W(tx => tx.Instances.Update(h.Level.Id, i => i with { PodIp = "10.42.0.50" }));
        await r.SessionOpened(Opened("s1", "1.2.3.4:27005", "10.42.0.50:5010", "127.1.0.1:29005"), default);
        await r.InstanceGone(lvl, default);
        Assert.Contains($"route 1.2.3.4:27005 -> {HubBackend} steam= hold=False", c.Calls);
        Assert.Contains("ready 10.42.0.50:5010 False", c.Calls);
    }

    [Fact]
    public async Task Identify_routes_a_returning_player_to_the_instance_that_leases_their_character()
    {
        var (h, r, _) = await Registry();
        await using var _ = h;
        var lvl = await h.W(tx => tx.Instances.Update(h.Level.Id, i => i with { PodIp = "10.42.0.50" }));
        var ch = await h.Character(account: "765");
        await h.Lease(ch, lvl);
        var answer = await r.Identify(new G.IdentifyRequest { RequestId = "i1", SessionId = "s9", ClientAddr = "1.2.3.4:27005", Steamid = "765" }, default);
        Assert.Equal((true, "10.42.0.50:5010"), (answer.Allowed, answer.Backend));
    }
}
