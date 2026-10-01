using Descent.Service.Ledger;
using SourceSharp.Host.Abstractions;

namespace Descent.Service.Tests.Ledger;

/// <summary>Plan §5.2a gate facts: the reserve and the kill-roll replay (D-H1).</summary>
public class ReserveFacts
{
    static readonly (int, int)[] FullReserve = [(0, 12), (1, 12), (2, 6), (3, 2), (4, 0)];

    static async Task<(LedgerHarness H, string Ch)> Leased()
    {
        var h = await LedgerHarness.Create();
        var ch = await h.Character();
        await h.Lease(ch, h.Level);
        return (h, ch);
    }

    [Fact]
    public async Task A_reserve_is_minted_with_the_configured_tier_counts()
    {
        var (h, ch) = await Leased();
        await using var _ = h;
        var items = await h.W(tx => h.Ledger.TakeReserve(tx, h.Level, ch, h.Token(ch), FullReserve, h.Req()));
        Assert.Equal(new Dictionary<int, int> { [0] = 12, [1] = 12, [2] = 6, [3] = 2 }, items.GroupBy(i => i.Tier).ToDictionary(g => g.Key, g => g.Count()));
        Assert.All(items, i => Assert.Equal((OwnerKind.Reserve, ch, h.Level.Id, ch, 10), (i.OwnerKind, i.OwnerId, i.InstanceId, i.ForCharacter, i.RolledForLevel)));
    }

    [Fact]
    public async Task A_second_take_mints_only_up_to_the_size()
    {
        var (h, ch) = await Leased();
        await using var _ = h;
        await h.W(tx => h.Ledger.TakeReserve(tx, h.Level, ch, h.Token(ch), [(2, 4)], h.Req()));
        var more = await h.W(tx => h.Ledger.TakeReserve(tx, h.Level, ch, h.Token(ch), [(2, 6)], h.Req()));
        Assert.Equal(2, more.Count);
    }

    [Fact]
    public async Task A_reserve_is_refused_to_an_instance_that_does_not_hold_the_lease()
    {
        var (h, ch) = await Leased();
        await using var _ = h;
        var e = await Assert.ThrowsAsync<HostRefusal>(() => h.W(tx => h.Ledger.TakeReserve(tx, h.Hub, ch, h.Token(ch), [(0, 1)], h.Req())));
        Assert.Equal("wrong_instance", e.Reason);
    }

    [Fact]
    public async Task A_reveal_the_roll_reproduces_moves_exactly_one_item_to_the_world()
    {
        var (h, ch) = await Leased();
        await using var _ = h;
        var reserve = await h.W(tx => h.Ledger.TakeReserve(tx, h.Level, ch, h.Token(ch), [(0, 3)], h.Req()));
        var kill = h.KillFor(h.Level, 0);
        var (item, _) = await h.W(tx => h.Ledger.Reveal(tx, h.Level, ch, h.Token(ch), reserve[0].Id, kill, "heavy", [], h.Req()));
        Assert.Equal((OwnerKind.World, h.Level.Id, ch), (item.OwnerKind, item.InstanceId, item.ForCharacter));
        Assert.Equal(2, await h.R(tx => tx.Items.Count(new ItemQuery(OwnerKind: OwnerKind.Reserve, OwnerId: ch))));
    }

    [Fact]
    public async Task A_reveal_books_the_kills_australium_once_per_member()
    {
        var (h, ch) = await Leased();
        await using var _ = h;
        var mate = await h.Character("Bo", account: "76561198000000002");
        await h.Lease(mate, h.Level);
        var reserve = await h.W(tx => h.Ledger.TakeReserve(tx, h.Level, ch, h.Token(ch), [(0, 2)], h.Req()));
        var kill = h.KillFor(h.Level, 0, partySize: 2);
        var (_, booked) = await h.W(tx => h.Ledger.Reveal(tx, h.Level, ch, h.Token(ch), reserve[0].Id, kill, "heavy", [mate], h.Req()));
        Assert.Equal(h.Level.Depth, booked);
        Assert.Equal((3L, 3L), ((await h.R(tx => tx.Characters.Get(ch)))!.Australium, (await h.R(tx => tx.Characters.Get(mate)))!.Australium));
    }

    [Fact]
    public async Task A_reveal_whose_kill_rolls_no_drop_is_refused_as_forged()
    {
        var (h, ch) = await Leased();
        await using var _ = h;
        var reserve = await h.W(tx => h.Ledger.TakeReserve(tx, h.Level, ch, h.Token(ch), [(0, 1)], h.Req()));
        var noDrop = h.KillFor(h.Level, -1);
        var e = await Assert.ThrowsAsync<HostRefusal>(() => h.W(tx => h.Ledger.Reveal(tx, h.Level, ch, h.Token(ch), reserve[0].Id, noDrop, "heavy", [], h.Req())));
        Assert.Equal("forged_reveal", e.Reason);
    }

    [Fact]
    public async Task A_reveal_of_another_tier_is_refused_as_forged()
    {
        var (h, ch) = await Leased();
        await using var _ = h;
        var reserve = await h.W(tx => h.Ledger.TakeReserve(tx, h.Level, ch, h.Token(ch), [(3, 1)], h.Req()));
        var stock = h.KillFor(h.Level, 0);
        var e = await Assert.ThrowsAsync<HostRefusal>(() => h.W(tx => h.Ledger.Reveal(tx, h.Level, ch, h.Token(ch), reserve[0].Id, stock, "heavy", [], h.Req())));
        Assert.Equal("forged_reveal", e.Reason);
    }

    [Fact]
    public async Task A_forged_reveal_destroys_the_item_with_an_audit_row()
    {
        var (h, ch) = await Leased();
        await using var _ = h;
        var reserve = await h.W(tx => h.Ledger.TakeReserve(tx, h.Level, ch, h.Token(ch), [(0, 1)], h.Req()));
        await h.W(async tx => { await DescentLedger.ForgedReveal(tx, h.Level, reserve[0].Id, 7, "req-x"); return true; });
        var item = (await h.R(tx => tx.Items.Get(reserve[0].Id)))!;
        Assert.Equal((ItemState.Destroyed, "forged_reveal"), (item.State, item.TerminalReason));
        Assert.Single(await h.R(tx => tx.Audit.List(new AuditQuery(Action: "item.forged_reveal"))));
    }

    [Fact]
    public async Task The_same_kill_cannot_be_revealed_twice_by_one_character()
    {
        var (h, ch) = await Leased();
        await using var _ = h;
        var reserve = await h.W(tx => h.Ledger.TakeReserve(tx, h.Level, ch, h.Token(ch), [(0, 2)], h.Req()));
        var kill = h.KillFor(h.Level, 0);
        await h.W(tx => h.Ledger.Reveal(tx, h.Level, ch, h.Token(ch), reserve[0].Id, kill, "heavy", [], h.Req()));
        var e = await Assert.ThrowsAsync<HostRefusal>(() => h.W(tx => h.Ledger.Reveal(tx, h.Level, ch, h.Token(ch), reserve[1].Id, kill, "heavy", [], h.Req())));
        Assert.Equal("forged_reveal", e.Reason);
    }

    [Fact]
    public async Task A_fallback_mint_needs_the_kill_to_roll_that_tier()
    {
        var (h, ch) = await Leased();
        await using var _ = h;
        var vintage = h.KillFor(h.Level, 1);
        var items = await h.W(tx => h.Ledger.MintDrops(tx, h.Level, ch, h.Token(ch), "heavy", [], vintage, 1, h.Req()));
        Assert.Equal((OwnerKind.World, 1), (Assert.Single(items).OwnerKind, items[0].Tier));
        var noDrop = h.KillFor(h.Level, -1);
        await Assert.ThrowsAsync<HostRefusal>(() => h.W(tx => h.Ledger.MintDrops(tx, h.Level, ch, h.Token(ch), "heavy", [], noDrop, 0, h.Req())));
    }

    [Fact]
    public async Task A_boss_mints_per_member_on_the_level_only()
    {
        var (h, ch) = await Leased();
        await using var _ = h;
        var mate = await h.Character("Bo", account: "2");
        await h.Lease(mate, h.Level);
        var inTown = await h.Character("Cy", account: "3");
        await h.Lease(inTown, h.Hub);
        var items = await h.W(tx => h.Ledger.MintDrops(tx, h.Level, ch, h.Token(ch), "boss_heavy", [mate, inTown], 99, -1, h.Req()));
        Assert.Equal(new[] { ch, mate }.Order(), items.Select(i => i.ForCharacter!).Distinct().Order());
    }

    [Fact]
    public async Task A_level_up_of_two_sweeps_the_stale_tiers()
    {
        var (h, ch) = await Leased();
        await using var _ = h;
        await h.W(tx => h.Ledger.TakeReserve(tx, h.Level, ch, h.Token(ch), [(0, 3), (2, 2)], h.Req()));
        Assert.Empty(await h.W(tx => h.Ledger.SweepStaleTiers(tx, ch, h.Level.Id, 11)));
        var swept = await h.W(tx => h.Ledger.SweepStaleTiers(tx, ch, h.Level.Id, 12));
        Assert.Equal([0, 2], swept);
        Assert.Equal(0, await h.R(tx => tx.Items.Count(new ItemQuery(OwnerKind: OwnerKind.Reserve, OwnerId: ch))));
    }

    [Fact]
    public async Task A_reserve_is_swept_when_its_lease_ends()
    {
        var (h, ch) = await Leased();
        await using var _ = h;
        await h.W(tx => h.Ledger.TakeReserve(tx, h.Level, ch, h.Token(ch), [(0, 4)], h.Req()));
        Assert.Equal(4, await h.W(tx => DescentLedger.SweepReserve(tx, ch, h.Level.Id, "reserve_released")));
        await h.AssertBalanced();
        Assert.Equal(0, await h.R(tx => tx.Items.Count(new ItemQuery(OwnerKind: OwnerKind.Reserve))));
    }
}
