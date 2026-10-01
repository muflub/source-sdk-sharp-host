using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;
using SourceSharp.Host.Testing;

namespace SourceSharp.Host.Sdk.Tests;

/// <summary>IParties, ITrades, ITravel and ILostAndFound, each call once, against the real service.</summary>
public class SocialFacts
{
    static async Task<(HostSdk Hub, ICharacterLease A, ICharacterLease B)> TwoOnTheHub(SdkHarness h)
    {
        await h.Instance("hub-1", InstanceKind.Hub);
        var hub = h.Sdk("hub-1");
        var a = (await hub.Pumped(hub.Characters.Create("76561198000000001", "scout", "Ann"))).Value;
        var b = (await hub.Pumped(hub.Characters.Create("76561198000000002", "heavy", "Bob"))).Value;
        return (hub, (await hub.Pumped(hub.Characters.Lease(a.Id))).Value, (await hub.Pumped(hub.Characters.Lease(b.Id))).Value);
    }

    static Task<ItemRecord> Mint(SdkHarness h, OwnerKind owner, string ownerId, int slot)
    {
        var r = h.Rules.RollItem(new ItemRollContext("scout", 1, 1, 5, 0, "depth1", "test"), (ulong)Random.Shared.NextInt64());
        return h.Data.WriteAsync((tx, _) => tx.Items.Mint(new NewItem(r.Seed, r.BaseType, r.Rarity, r.ItemLevel, 1, true, r.Instance, 1, 0, 1),
            owner, ownerId, null, slot, "test", null));
    }

    // ------------------------------------------------------------------ parties

    [Fact]
    public async Task A_leader_creates_a_party_and_invites_and_the_invitee_accepts()
    {
        await using var h = await SdkHarness.Start();
        var (hub, a, b) = await TwoOnTheHub(h);
        var p = (await hub.Pumped(hub.Parties.Create(a))).Value;
        var invited = await hub.Pumped(hub.Parties.Invite(a, p.Id, b.CharacterId));
        Assert.Equal([b.CharacterId], invited.Value.Invited);
        var joined = await hub.Pumped(hub.Parties.Accept(b, p.Id));
        Assert.Equal([a.CharacterId, b.CharacterId], joined.Value.Members.Order());
    }

    [Fact]
    public async Task Get_finds_a_party_by_member()
    {
        await using var h = await SdkHarness.Start();
        var (hub, a, _) = await TwoOnTheHub(h);
        var p = (await hub.Pumped(hub.Parties.Create(a))).Value;
        var got = await hub.Pumped(hub.Parties.Get("", a.CharacterId));
        Assert.Equal((p.Id, a.CharacterId, "hub-1"), (got.Value.Id, got.Value.Leader, got.Value.LiveInstance));
    }

    [Fact]
    public async Task The_leader_kicks_a_member()
    {
        await using var h = await SdkHarness.Start();
        var (hub, a, b) = await TwoOnTheHub(h);
        var p = (await hub.Pumped(hub.Parties.Create(a))).Value;
        await hub.Pumped(hub.Parties.Invite(a, p.Id, b.CharacterId));
        await hub.Pumped(hub.Parties.Accept(b, p.Id));
        var kicked = await hub.Pumped(hub.Parties.Kick(a, p.Id, b.CharacterId));
        Assert.DoesNotContain(b.CharacterId, kicked.Value.Members);
    }

    [Fact]
    public async Task A_member_leaves()
    {
        await using var h = await SdkHarness.Start();
        var (hub, a, b) = await TwoOnTheHub(h);
        var p = (await hub.Pumped(hub.Parties.Create(a))).Value;
        await hub.Pumped(hub.Parties.Invite(a, p.Id, b.CharacterId));
        await hub.Pumped(hub.Parties.Accept(b, p.Id));
        Assert.True((await hub.Pumped(hub.Parties.Leave(b, p.Id))).Ok);
        Assert.DoesNotContain(b.CharacterId, (await hub.Pumped(hub.Parties.Get(p.Id))).Value.Members);
    }

    [Fact]
    public async Task A_non_leader_invite_is_a_typed_refusal()
    {
        await using var h = await SdkHarness.Start();
        var (hub, a, b) = await TwoOnTheHub(h);
        var p = (await hub.Pumped(hub.Parties.Create(a))).Value;
        var r = await hub.Pumped(hub.Parties.Invite(b, p.Id, a.CharacterId));
        Assert.Equal(HostError.NotInParty, r.Error);
    }

    // ------------------------------------------------------------------ trades

    [Fact]
    public async Task A_confirm_before_both_sides_lock_is_refused_locally_and_sends_nothing()
    {
        await using var h = await SdkHarness.Start();
        var (hub, a, b) = await TwoOnTheHub(h);
        var t = (await hub.Pumped(hub.Trades.Open(a, b.CharacterId))).Value;
        await hub.Pumped(hub.Trades.Lock(a, t.Id));
        var calls = hub.Metrics.RpcCalls;
        var r = await hub.Pumped(hub.Trades.Confirm(a, t.Id));
        Assert.Equal((HostError.TradeState, true), (r.Error, r.Refusal!.Local));
        Assert.Equal(calls, hub.Metrics.RpcCalls);
    }

    [Fact]
    public async Task A_trade_commits_after_offers_both_locks_and_both_confirms()
    {
        await using var h = await SdkHarness.Start();
        var (hub, a, b) = await TwoOnTheHub(h);
        var item = await Mint(h, OwnerKind.Character, a.CharacterId, 0);
        var t = (await hub.Pumped(hub.Trades.Open(a, b.CharacterId))).Value;
        var offered = await hub.Pumped(hub.Trades.Offer(a, t.Id, [item.Id]));
        Assert.Equal([item.Id], offered.Value.OfferA);
        await hub.Pumped(hub.Trades.Lock(a, t.Id));
        await hub.Pumped(hub.Trades.Lock(b, t.Id));
        Assert.True(hub.Trades.Known(t.Id)!.BothLocked);
        await hub.Pumped(hub.Trades.Confirm(a, t.Id));
        var done = await hub.Pumped(hub.Trades.Confirm(b, t.Id));
        Assert.Equal(TradeStateKind.Committed, done.Value.State);
        Assert.Equal((OwnerKind.Character, b.CharacterId), await h.Data.ReadAsync(async (tx, _) =>
        {
            var i = (await tx.Items.Get(item.Id))!;
            return (i.OwnerKind, i.OwnerId);
        }));
    }

    [Fact]
    public async Task A_trade_can_be_cancelled()
    {
        await using var h = await SdkHarness.Start();
        var (hub, a, b) = await TwoOnTheHub(h);
        var t = (await hub.Pumped(hub.Trades.Open(a, b.CharacterId))).Value;
        var r = await hub.Pumped(hub.Trades.Cancel(b, t.Id));
        Assert.Equal(TradeStateKind.Cancelled, r.Value.State);
    }

    // ------------------------------------------------------------------ travel

    [Fact]
    public async Task RequestDescent_carries_the_lease_party_and_depth_and_answers_started()
    {
        await using var h = await SdkHarness.Start();
        var (hub, a, _) = await TwoOnTheHub(h);
        var r = await hub.Pumped(hub.Travel.RequestDescent(a, "party-1", 4));
        Assert.Equal((TravelState.Started, "lvl-4"), (r.Value.State, r.Value.InstanceId));
        var (method, req) = Assert.Single(h.TravelApi.Calls);
        Assert.Equal(("RequestDescent", a.CharacterId, "party-1", 4), (method, req.CharacterId, req.PartyId, req.Depth));
        Assert.Equal(32, req.LeaseToken.Length);
        Assert.NotEmpty(req.RequestId);
    }

    [Fact]
    public async Task StairsDown_can_answer_waiting_with_an_eta()
    {
        await using var h = await SdkHarness.Start();
        var (hub, a, _) = await TwoOnTheHub(h);
        var r = await hub.Pumped(hub.Travel.StairsDown(a, "party-1", 5));
        Assert.Equal((TravelState.Waiting, TimeSpan.FromMilliseconds(1500)), (r.Value.State, r.Value.Eta));
    }

    [Fact]
    public async Task The_portal_and_corpse_trips_reach_the_service()
    {
        await using var h = await SdkHarness.Start();
        var (hub, a, _) = await TwoOnTheHub(h);
        Assert.True((await hub.Pumped(hub.Travel.TownPortal(a))).Ok);
        Assert.True((await hub.Pumped(hub.Travel.ReturnThroughPortal(a))).Ok);
        Assert.True((await hub.Pumped(hub.Travel.ReturnToCorpse(a))).Ok);
        Assert.Equal(["TownPortal", "ReturnThroughPortal", "ReturnToCorpse"], h.TravelApi.Calls.Select(c => c.Method));
    }

    // ------------------------------------------------------------------ lost & found

    [Fact]
    public async Task Lost_and_found_lists_an_entry_and_reclaims_it()
    {
        await using var h = await SdkHarness.Start();
        var (hub, a, _) = await TwoOnTheHub(h);
        await h.Data.WriteAsync((tx, _) => tx.Characters.AddAustralium(a.CharacterId, 1000, "test", null));
        var item = await Mint(h, OwnerKind.LostAndFound, a.CharacterId, -1);
        var entry = await h.Data.WriteAsync((tx, _) => tx.LostAndFound.Add(a.CharacterId, item.Id, 10, "lvl-9"));
        var list = await hub.Pumped(hub.LostAndFound.List(a.CharacterId));
        var e = Assert.Single(list.Value);
        Assert.Equal((entry.Id, item.Id, 10L, "lvl-9"), (e.Id, e.Item.Id, e.Fee, e.OriginInstance));
        var back = await hub.Pumped(hub.LostAndFound.Reclaim(a, e.Id));
        Assert.Equal((item.Id, HostOwnerKind.Character), (back.Value.Id, back.Value.Owner));
    }
}
