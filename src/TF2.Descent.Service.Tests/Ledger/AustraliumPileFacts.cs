using TF2.Descent.Service.Ledger;
using Microsoft.Extensions.Options;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;
using SourceSharp.Host.Testing;

namespace TF2.Descent.Service.Tests.Ledger;

/// <summary>
/// plan_sdk_2_8 §1: one pile per kill, shared on pickup. The fake's kill roll is the depth (3 here)
/// and its shares are the split, the remainder one apiece, killer first.
/// </summary>
public class AustraliumPileFacts
{
    /// <summary>Rules that give one member more than the whole pile.</summary>
    sealed class Greedy(FakeGameRules f) : Contract11RulesFacts.Rules11(f), IGameRules
    {
        public IReadOnlyList<long> AustraliumShares(long total, IReadOnlyList<CharacterSheet> killerFirst) =>
            killerFirst.Select(_ => total + 1).ToList();
    }

    sealed record World(LedgerHarness H, string Ann, string Bo);

    /// <summary>Ann and Bo on the level; in a party when <paramref name="party"/>.</summary>
    static async Task<World> On(bool party = true)
    {
        var h = await LedgerHarness.Create();
        var ann = await h.Character();
        var bo = await h.Character("Bo", account: "2");
        await h.Lease(ann, h.Level);
        await h.Lease(bo, h.Level);
        if (party)
        {
            var p = await h.W(tx => tx.Parties.Create(ann));
            await h.W(tx => tx.Parties.Invite(p.Id, bo));
            await h.W(tx => tx.Parties.Join(p.Id, bo));
        }
        return new World(h, ann, bo);
    }

    static Task<ItemRecord> Kill(LedgerHarness h, string killer, uint seq, int partySize = 1, DescentLedger? ledger = null) =>
        h.W(tx => (ledger ?? h.Ledger).KillAustralium(tx, h.Level, killer, h.Token(killer), seq, "heavy", partySize, h.Req()));

    static Task<IReadOnlyList<(string CharacterId, long Amount)>> Pick(LedgerHarness h, string who, string pile, DescentLedger? ledger = null) =>
        h.W(tx => (ledger ?? h.Ledger).ClaimAustralium(tx, h.Level, who, h.Token(who), pile, h.Req()));

    static async Task<long> Wallet(LedgerHarness h, string ch) => (await h.R(tx => tx.Characters.Get(ch)))!.Australium;

    [Fact]
    public async Task A_kill_mints_one_pile_of_its_roll_for_anyone_to_pick_up()
    {
        var w = await On();
        await using var _ = w.H;
        var pile = await Kill(w.H, w.Ann, 1);
        Assert.Equal((DescentLedger.AustraliumPile, w.H.Level.Depth, OwnerKind.World, (string?)null),
            (pile.BaseType, pile.Count, pile.OwnerKind, pile.ForCharacter));
        Assert.Equal(0, await Wallet(w.H, w.Ann));
    }

    [Fact]
    public async Task A_kill_that_drops_no_item_still_mints_its_pile()
    {
        var w = await On();
        await using var _ = w.H;
        var noDrop = w.H.KillFor(w.H.Level, -1);
        Assert.Equal(w.H.Level.Depth, (await Kill(w.H, w.Ann, noDrop)).Count);
    }

    [Fact]
    public async Task A_kill_mints_its_pile_once()
    {
        var w = await On();
        await using var _ = w.H;
        await Kill(w.H, w.Ann, 1);
        var e = await Assert.ThrowsAsync<HostRefusal>(() => Kill(w.H, w.Bo, 1));
        Assert.Equal("already_minted", e.Reason);
    }

    [Fact]
    public async Task Picking_it_up_pays_every_party_member_on_the_level_their_share_killer_first()
    {
        var w = await On();
        await using var _ = w.H;
        var pile = await Kill(w.H, w.Ann, 1, partySize: 2);
        var credits = await Pick(w.H, w.Bo, pile.Id);
        Assert.Equal([(w.Ann, 2L), (w.Bo, 1L)], credits);
        Assert.Equal((2L, 1L), (await Wallet(w.H, w.Ann), await Wallet(w.H, w.Bo)));
        Assert.Equal(ItemState.Consumed, (await w.H.R(tx => tx.Items.Get(pile.Id)))!.State);
        await w.H.AssertBalanced();
    }

    [Fact]
    public async Task A_killer_with_no_party_takes_the_whole_pile()
    {
        var w = await On(party: false);
        await using var _ = w.H;
        var pile = await Kill(w.H, w.Ann, 1);
        Assert.Equal([(w.Ann, 3L)], await Pick(w.H, w.Ann, pile.Id));
    }

    [Fact]
    public async Task A_party_member_who_is_not_on_the_level_is_not_paid()
    {
        var w = await On();
        await using var _ = w.H;
        await w.H.Lease(w.Bo, w.H.Hub);
        var pile = await Kill(w.H, w.Ann, 1);
        Assert.Equal([(w.Ann, 3L)], await Pick(w.H, w.Ann, pile.Id));
        Assert.Equal(0, await Wallet(w.H, w.Bo));
    }

    [Fact]
    public async Task Someone_outside_the_killers_party_may_not_pick_it_up()
    {
        var w = await On(party: false);
        await using var _ = w.H;
        var pile = await Kill(w.H, w.Ann, 1);
        var e = await Assert.ThrowsAsync<HostRefusal>(() => Pick(w.H, w.Bo, pile.Id));
        Assert.Equal("not_in_party", e.Reason);
        Assert.Equal(ItemState.Live, (await w.H.R(tx => tx.Items.Get(pile.Id)))!.State);
    }

    [Fact]
    public async Task A_pile_pays_once()
    {
        var w = await On();
        await using var _ = w.H;
        var pile = await Kill(w.H, w.Ann, 1, partySize: 2);
        await Pick(w.H, w.Ann, pile.Id);
        await Assert.ThrowsAsync<HostRefusal>(() => Pick(w.H, w.Bo, pile.Id));
        Assert.Equal((2L, 1L), (await Wallet(w.H, w.Ann), await Wallet(w.H, w.Bo)));
    }

    [Fact]
    public async Task A_pile_never_goes_in_a_backpack()
    {
        var w = await On();
        await using var _ = w.H;
        var pile = await Kill(w.H, w.Ann, 1);
        var e = await Assert.ThrowsAsync<HostRefusal>(() => w.H.W(tx => w.H.Ledger.Claim(tx, w.H.Level, w.Ann, w.H.Token(w.Ann), pile.Id, w.H.Req())));
        Assert.Equal("australium_pile", e.Reason);
    }

    [Fact]
    public async Task Only_a_pile_is_picked_up_as_Australium()
    {
        var w = await On();
        await using var _ = w.H;
        var item = await w.H.Give(w.Ann);
        var e = await Assert.ThrowsAsync<HostRefusal>(() => Pick(w.H, w.Ann, item.Id));
        Assert.Equal("not_a_pile", e.Reason);
    }

    [Fact]
    public async Task Shares_above_the_whole_pile_are_refused()
    {
        var w = await On();
        await using var _ = w.H;
        var ledger = new DescentLedger(new SingleRulesProvider(new Greedy(w.H.Rules)), Options.Create(w.H.Options));
        var pile = await Kill(w.H, w.Ann, 1, ledger: ledger);
        var e = await Assert.ThrowsAsync<HostRefusal>(() => Pick(w.H, w.Ann, pile.Id, ledger));
        Assert.Equal("rules_bad_shares", e.Reason);
        Assert.Equal(0, await Wallet(w.H, w.Ann));
    }

    [Fact]
    public async Task Rules_built_on_contract_11_pay_every_member_the_whole_roll()
    {
        var w = await On();
        await using var _ = w.H;
        var ledger = new DescentLedger(new SingleRulesProvider(new Contract11RulesFacts.Rules11(w.H.Rules)), Options.Create(w.H.Options));
        var pile = await Kill(w.H, w.Ann, 1, ledger: ledger);
        Assert.Equal([(w.Ann, 3L), (w.Bo, 3L)], await Pick(w.H, w.Ann, pile.Id, ledger));
    }

    [Fact]
    public async Task A_28_reveal_credits_nothing()
    {
        var w = await On();
        await using var _ = w.H;
        var reserve = await w.H.W(tx => w.H.Ledger.TakeReserve(tx, w.H.Level, w.Ann, w.H.Token(w.Ann), [(0, 1)], w.H.Req()));
        var kill = w.H.KillFor(w.H.Level, 0, partySize: 2);
        var (_, booked) = await w.H.W(tx => w.H.Ledger.Reveal(tx, w.H.Level, w.Ann, w.H.Token(w.Ann), reserve[0].Id, kill, "heavy", [w.Bo], w.H.Req(),
            australiumPile: true));
        Assert.Equal((0L, 0L, 0L), (booked, await Wallet(w.H, w.Ann), await Wallet(w.H, w.Bo)));
    }

    [Fact]
    public async Task An_unclaimed_pile_is_swept_with_the_level()
    {
        var w = await On();
        await using var _ = w.H;
        var pile = await Kill(w.H, w.Ann, 1);
        await w.H.W(tx => w.H.Ledger.SweepInstance(tx, w.H.Level));
        Assert.Equal(ItemState.Swept, (await w.H.R(tx => tx.Items.Get(pile.Id)))!.State);
    }
}
