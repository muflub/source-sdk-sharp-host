using SourceSharp.Host.Abstractions;

namespace SourceSharp.Host.Admin.Tests;

/// <summary>Parties, trades and Lost &amp; Found (§11).</summary>
public sealed class SocialActionFacts : WorldFacts
{
    async Task<Party> Party(string leader, params string[] members)
    {
        return await W.D.Write(async tx =>
        {
            var p = await tx.Parties.Create(leader);
            foreach (var m in members) { await tx.Parties.Invite(p.Id, m); p = await tx.Parties.Join(p.Id, m); }
            return p;
        });
    }

    [Fact]
    public async Task Disband_removes_the_party()
    {
        var a = await W.Character("Ann");
        var p = await Party(a.Id);
        Assert.True((await A.DisbandParty(p.Id)).Ok);
        Assert.Null(await W.D.Read(tx => tx.Parties.Get(p.Id)));
    }

    [Fact]
    public async Task Disband_is_refused_while_a_member_is_leased()
    {
        var a = await W.Character("Ann");
        var b = await W.Character("Bob", account: "76561198000000002");
        var p = await Party(a.Id, b.Id);
        await W.Lease(b.Id);
        Assert.Equal((AdminOutcome.Leased, b.Id), ((await A.DisbandParty(p.Id)).Outcome, (await A.DisbandParty(p.Id)).CharacterId));
        Assert.NotNull(await W.D.Read(tx => tx.Parties.Get(p.Id)));
    }

    [Fact]
    public async Task Kick_from_party_removes_that_member()
    {
        var a = await W.Character("Ann");
        var b = await W.Character("Bob", account: "76561198000000002");
        var p = await Party(a.Id, b.Id);
        Assert.True((await A.KickFromParty(p.Id, b.Id)).Ok);
        Assert.DoesNotContain(b.Id, (await W.D.Read(tx => tx.Parties.Get(p.Id)))!.Members);
    }

    async Task<(TradeRecord Trade, ItemRecord Item, CharacterRecord A, CharacterRecord B)> LockedTrade()
    {
        var a = await W.Character("Ann");
        var b = await W.Character("Bob", account: "76561198000000002");
        var t = await W.D.Write(tx => tx.Trades.Open("hub-1", a.Id, b.Id));
        var i = await W.Mint(OwnerKind.TradeEscrow, t.Id, 4);
        t = await W.D.Write(tx => tx.Trades.Save(t with { OfferA = [i.Id], LockedA = true }));
        return (t, i, a, b);
    }

    [Fact]
    public async Task Cancel_trade_returns_the_escrow_to_the_offerer()
    {
        var (t, i, a, _) = await LockedTrade();
        var r = await A.CancelTrade(t.Id);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(1, r.Value);
        var after = (await W.Item(i.Id))!;
        Assert.Equal((OwnerKind.Character, a.Id), (after.OwnerKind, after.OwnerId));
        Assert.Equal(TradeStatus.Cancelled, (await W.D.Read(tx => tx.Trades.Get(t.Id)))!.State);
    }

    [Fact]
    public async Task Cancel_trade_is_refused_while_a_party_to_it_is_leased()
    {
        var (t, i, _, b) = await LockedTrade();
        await W.Lease(b.Id, "hub-1");
        Assert.Equal(AdminOutcome.Leased, (await A.CancelTrade(t.Id)).Outcome);
        Assert.Equal(OwnerKind.TradeEscrow, (await W.Item(i.Id))!.OwnerKind);
        Assert.Empty(W.Ledger.Calls);
    }

    [Fact]
    public async Task Cancel_of_a_committed_trade_is_refused()
    {
        var (t, _, _, _) = await LockedTrade();
        await W.D.Write(tx => tx.Trades.Save(t with { State = TradeStatus.Committed }));
        Assert.Equal("trade_state", (await A.CancelTrade(t.Id)).Reason);
    }

    async Task<(LostAndFoundRecord Entry, CharacterRecord C)> Lost(long fee = 40)
    {
        var c = await W.Character();
        var i = await W.Mint(OwnerKind.LostAndFound, c.Id);
        return (await W.D.Write(tx => tx.LostAndFound.Add(c.Id, i.Id, fee, "lvl-1")), c);
    }

    [Fact]
    public async Task Waive_fee_sets_the_fee_to_zero()
    {
        var (e, _) = await Lost();
        Assert.True((await A.WaiveFee(e.Id)).Ok);
        Assert.Equal(0, (await W.D.Read(tx => tx.LostAndFound.Get(e.Id)))!.Fee);
    }

    [Fact]
    public async Task Waive_fee_is_refused_while_the_owner_is_leased()
    {
        var (e, c) = await Lost();
        await W.Lease(c.Id);
        Assert.Equal(AdminOutcome.Leased, (await A.WaiveFee(e.Id)).Outcome);
        Assert.Equal(40, (await W.D.Read(tx => tx.LostAndFound.Get(e.Id)))!.Fee);
    }

    [Fact]
    public async Task Release_to_stash_moves_the_item_into_the_owners_stash()
    {
        var (e, c) = await Lost();
        Assert.True((await A.ReleaseToStash(e.Id)).Ok);
        var after = (await W.Item(e.ItemId))!;
        Assert.Equal((OwnerKind.Stash, W.Ledger.StashOwner(c.Account, false)), (after.OwnerKind, after.OwnerId));
        Assert.Null(await W.D.Read(tx => tx.LostAndFound.Get(e.Id)));
    }

    [Fact]
    public async Task Delete_lost_and_found_destroys_the_item_and_removes_the_entry()
    {
        var (e, _) = await Lost();
        Assert.True((await A.DeleteLostAndFound(e.Id)).Ok);
        Assert.Equal(ItemState.Destroyed, (await W.Item(e.ItemId))!.State);
        Assert.Null(await W.D.Read(tx => tx.LostAndFound.Get(e.Id)));
    }

    [Fact]
    public async Task Without_a_ledger_wired_a_ledger_action_is_refused_unavailable()
    {
        var bare = new AdminActions(W.D.Data, new SingleRulesProvider(W.Rules), W.Options);
        var (t, _, _, _) = await LockedTrade();
        Assert.Equal("unavailable", (await bare.CancelTrade(t.Id)).Reason);
    }
}
