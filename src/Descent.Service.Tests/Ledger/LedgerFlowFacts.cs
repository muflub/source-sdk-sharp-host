using Descent.Service.Ledger;
using SourceSharp.Host.Abstractions;

namespace Descent.Service.Tests.Ledger;

/// <summary>Plan §5.2, §5.3, §5.4 gate facts: pickup, death, the sweep, Lost &amp; Found, vendors, crafts, trades, reconcile.</summary>
public class LedgerFlowFacts
{
    static async Task<(LedgerHarness H, string Ch, ItemRecord Item)> WithWorldItem(string? forCharacter = null)
    {
        var h = await LedgerHarness.Create();
        var ch = await h.Character();
        await h.Lease(ch, h.Level);
        var reserve = await h.W(tx => h.Ledger.TakeReserve(tx, h.Level, ch, h.Token(ch), [(0, 1)], h.Req()));
        var kill = h.KillFor(h.Level, 0);
        var (item, _) = await h.W(tx => h.Ledger.Reveal(tx, h.Level, ch, h.Token(ch), reserve[0].Id, kill, "heavy", [], h.Req()));
        return (h, ch, item);
    }

    [Fact]
    public async Task A_claim_puts_a_world_item_in_the_first_free_slot()
    {
        var (h, ch, item) = await WithWorldItem();
        await using var _ = h;
        await h.Give(ch, slot: 0);
        var claimed = await h.W(tx => h.Ledger.Claim(tx, h.Level, ch, h.Token(ch), item.Id, h.Req()));
        Assert.Equal((OwnerKind.Character, ch, 1, (string?)null), (claimed.OwnerKind, claimed.OwnerId, claimed.Slot, claimed.ForCharacter));
    }

    [Fact]
    public async Task A_claim_of_an_item_dropped_for_another_player_is_refused()
    {
        var (h, ch, item) = await WithWorldItem();
        await using var _ = h;
        var other = await h.Character("Bo", account: "2");
        await h.Lease(other, h.Level);
        var e = await Assert.ThrowsAsync<HostRefusal>(() => h.W(tx => h.Ledger.Claim(tx, h.Level, other, h.Token(other), item.Id, h.Req())));
        Assert.Equal("wrong_owner", e.Reason);
    }

    [Fact]
    public async Task A_claim_from_another_instance_is_refused()
    {
        var (h, ch, item) = await WithWorldItem();
        await using var _ = h;
        var other = await h.AddInstance("lvl-2", InstanceKind.Level, 3);
        await h.Lease(ch, other);
        var e = await Assert.ThrowsAsync<HostRefusal>(() => h.W(tx => h.Ledger.Claim(tx, other, ch, h.Token(ch), item.Id, h.Req())));
        Assert.Equal("wrong_instance", e.Reason);
    }

    [Fact]
    public async Task A_claim_into_a_full_backpack_is_refused()
    {
        var (h, ch, item) = await WithWorldItem();
        await using var _ = h;
        for (var s = 0; s < h.Rules.Backpack; s++) await h.Give(ch, slot: s);
        var e = await Assert.ThrowsAsync<HostRefusal>(() => h.W(tx => h.Ledger.Claim(tx, h.Level, ch, h.Token(ch), item.Id, h.Req())));
        Assert.Equal("no_capacity", e.Reason);
    }

    [Fact]
    public async Task A_claim_with_a_stale_lease_is_refused_and_audited()
    {
        var (h, ch, item) = await WithWorldItem();
        await using var _ = h;
        var e = await Assert.ThrowsAsync<HostRefusal>(() => h.W(tx => h.Ledger.Claim(tx, h.Level, ch, new byte[32], item.Id, h.Req())));
        Assert.Equal("stale_lease", e.Reason);
    }

    [Fact]
    public async Task Death_moves_everything_carried_to_the_corpse_within_one_call()
    {
        var h = await LedgerHarness.Create();
        await using var _ = h;
        var ch = await h.Character();
        await h.Lease(ch, h.Level);
        await h.Give(ch); await h.Give(ch);
        var moved = await h.W(tx => h.Ledger.RecordDeath(tx, h.Level, ch, h.Token(ch), h.Req()));
        Assert.Equal(2, moved.Count);
        Assert.All(moved, i => Assert.Equal((OwnerKind.Corpse, ch, h.Level.Id), (i.OwnerKind, i.OwnerId, i.InstanceId)));
        Assert.Equal(0, await h.R(tx => tx.Items.Count(new ItemQuery(OwnerKind: OwnerKind.Character, OwnerId: ch))));
    }

    [Fact]
    public async Task Death_carries_the_australium_onto_the_corpse_and_looting_returns_it()
    {
        var h = await LedgerHarness.Create();
        await using var _ = h;
        var ch = await h.Character();
        await h.Lease(ch, h.Level);
        await h.W(tx => tx.Characters.AddAustralium(ch, 40, "test", null));
        await h.Give(ch);
        await h.W(tx => h.Ledger.RecordDeath(tx, h.Level, ch, h.Token(ch), h.Req()));
        Assert.Equal(0, (await h.R(tx => tx.Characters.Get(ch)))!.Australium);
        var looted = await h.W(tx => h.Ledger.LootCorpse(tx, h.Level, ch, h.Token(ch), h.Req()));
        Assert.Single(looted);
        Assert.Equal(40, (await h.R(tx => tx.Characters.Get(ch)))!.Australium);
    }

    [Fact]
    public async Task A_hardcore_death_marks_the_character_fallen()
    {
        var h = await LedgerHarness.Create();
        await using var _ = h;
        var ch = await h.Character(hardcore: true);
        await h.Lease(ch, h.Level);
        await h.W(tx => h.Ledger.RecordDeath(tx, h.Level, ch, h.Token(ch), h.Req()));
        Assert.True((await h.R(tx => tx.Characters.Get(ch)))!.Fallen);
    }

    [Fact]
    public async Task The_sweep_ends_world_and_reserve_items_of_the_level()
    {
        var (h, ch, item) = await WithWorldItem();
        await using var _ = h;
        await h.W(tx => h.Ledger.TakeReserve(tx, h.Level, ch, h.Token(ch), [(0, 3)], h.Req()));
        var counts = await h.W(tx => h.Ledger.SweepInstance(tx, h.Level));
        Assert.Equal((1, 3), (counts.World, counts.Reserve));
        Assert.Equal((ItemState.Swept, "level_shutdown"), ((await h.R(tx => tx.Items.Get(item.Id)))!.State, (await h.R(tx => tx.Items.Get(item.Id)))!.TerminalReason));
        Assert.Single(await h.R(tx => tx.Audit.List(new AuditQuery(Action: "instance.sweep", Target: h.Level.Id))));
    }

    [Fact]
    public async Task The_sweep_sends_a_softcore_corpse_to_lost_and_found_at_a_quarter_of_vendor_value()
    {
        var h = await LedgerHarness.Create();
        await using var _ = h;
        var ch = await h.Character();
        await h.Lease(ch, h.Level);
        var given = await h.Give(ch, tier: 1);
        await h.W(tx => h.Ledger.RecordDeath(tx, h.Level, ch, h.Token(ch), h.Req()));
        await h.W(tx => h.Ledger.SweepInstance(tx, h.Level));
        var entry = Assert.Single(await h.R(tx => tx.LostAndFound.ForCharacter(ch)));
        var value = h.Rules.Price(h.Rules.Items.Decode(given.Instance, given.SchemaVersion), SourceSharp.Host.Contracts.PriceKind.VendorValue);
        Assert.Equal((given.Id, (long)Math.Round(value * 0.25)), (entry.ItemId, entry.Fee));
        Assert.Equal(OwnerKind.LostAndFound, (await h.R(tx => tx.Items.Get(given.Id)))!.OwnerKind);
    }

    [Fact]
    public async Task The_sweep_destroys_a_hardcore_corpse()
    {
        var h = await LedgerHarness.Create();
        await using var _ = h;
        var ch = await h.Character(hardcore: true);
        await h.Lease(ch, h.Level);
        var given = await h.Give(ch);
        await h.W(tx => h.Ledger.RecordDeath(tx, h.Level, ch, h.Token(ch), h.Req()));
        var counts = await h.W(tx => h.Ledger.SweepInstance(tx, h.Level));
        Assert.Equal((0, 1), (counts.ToLostAndFound, counts.CorpseDestroyed));
        Assert.Equal(ItemState.Destroyed, (await h.R(tx => tx.Items.Get(given.Id)))!.State);
    }

    [Fact]
    public async Task A_second_sweep_moves_nothing()
    {
        var (h, _, _) = await WithWorldItem();
        await using var __ = h;
        await h.W(tx => h.Ledger.SweepInstance(tx, h.Level));
        var again = await h.W(tx => h.Ledger.SweepInstance(tx, h.Level));
        Assert.Equal(new DescentLedger.SweepCounts(0, 0, 0, 0, 0, 0, 0), again);
    }

    [Fact]
    public async Task Count_in_equals_count_out_from_mint_through_death_reap_and_reclaim()
    {
        var h = await LedgerHarness.Create();
        await using var _ = h;
        var ch = await h.Character();
        await h.Lease(ch, h.Level);
        var reserve = await h.W(tx => h.Ledger.TakeReserve(tx, h.Level, ch, h.Token(ch), [(0, 2)], h.Req()));
        await h.AssertBalanced();
        var (world, _) = await h.W(tx => h.Ledger.Reveal(tx, h.Level, ch, h.Token(ch), reserve[0].Id, h.KillFor(h.Level, 0), "heavy", [], h.Req()));
        var claimed = await h.W(tx => h.Ledger.Claim(tx, h.Level, ch, h.Token(ch), world.Id, h.Req()));
        await h.W(tx => h.Ledger.Drop(tx, h.Level, ch, h.Token(ch), claimed.Id, h.Req()));
        await h.W(tx => h.Ledger.Claim(tx, h.Level, ch, h.Token(ch), claimed.Id, h.Req()));
        await h.W(tx => h.Ledger.RecordDeath(tx, h.Level, ch, h.Token(ch), h.Req()));
        await h.AssertBalanced();
        await h.W(tx => h.Ledger.SweepInstance(tx, h.Level)); // the reap: the other reserve item is swept, the corpse goes to L&F
        await h.AssertBalanced();
        await h.W(tx => tx.Characters.AddAustralium(ch, 1000, "test", null));
        await h.Lease(ch, h.Hub);
        var entry = Assert.Single(await h.R(tx => tx.LostAndFound.ForCharacter(ch)));
        var back = await h.W(tx => h.Ledger.Reclaim(tx, h.Hub, ch, h.Token(ch), entry.Id, h.Req()));
        Assert.Equal((claimed.Id, OwnerKind.Character), (back.Id, back.OwnerKind));
        var (minted, terminal, live) = await h.Totals();
        Assert.Equal((2L, 1L, 1L), (minted, terminal, live));
        // The kill booked the depth's Australium (3); death carried it onto the corpse and the
        // softcore reap returned it; the reclaim paid the fee.
        Assert.Equal(h.Level.Depth + 1000 - entry.Fee, (await h.R(tx => tx.Characters.Get(ch)))!.Australium);
    }

    [Fact]
    public async Task Buying_pays_the_price_and_selling_keeps_a_buy_back()
    {
        var h = await LedgerHarness.Create();
        await using var _ = h;
        var ch = await h.Character();
        await h.Lease(ch, h.Hub);
        await h.W(tx => tx.Characters.AddAustralium(ch, 10_000, "test", null));
        await h.W(tx => h.Ledger.RollVendor(tx, h.Hub, "engineer"));
        var stock = await h.R(tx => tx.Items.Query(new ItemQuery(OwnerKind: OwnerKind.Vendor, OwnerId: DescentLedger.VendorOwner(h.Hub.Id, "engineer"))));
        Assert.Equal(6, stock.Count);
        var bought = await h.W(tx => h.Ledger.Buy(tx, h.Hub, ch, h.Token(ch), stock[0].Id, "engineer", h.Req()));
        var price = h.Rules.Price(h.Rules.Items.Decode(bought.Instance, bought.SchemaVersion), SourceSharp.Host.Contracts.PriceKind.Buy);
        Assert.Equal(10_000 - price, (await h.R(tx => tx.Characters.Get(ch)))!.Australium);
        await h.W(tx => h.Ledger.Sell(tx, h.Hub, ch, h.Token(ch), bought.Id, "engineer", h.Req()));
        var back = await h.W(tx => h.Ledger.BuyBack(tx, h.Hub, ch, h.Token(ch), bought.Id, "engineer", h.Req()));
        Assert.Equal(OwnerKind.Character, back.OwnerKind);
    }

    [Fact]
    public async Task Buy_back_keeps_only_the_last_twelve()
    {
        var h = await LedgerHarness.Create();
        await using var _ = h;
        var ch = await h.Character();
        await h.Lease(ch, h.Hub);
        var sold = new List<string>();
        for (var i = 0; i < 13; i++)
        {
            var item = await h.Give(ch, slot: 0);
            await h.W(tx => h.Ledger.Sell(tx, h.Hub, ch, h.Token(ch), item.Id, "engineer", h.Req()));
            sold.Add(item.Id);
        }
        Assert.Equal(12, await h.R(tx => tx.Items.Count(new ItemQuery(OwnerKind: OwnerKind.Vendor, OwnerId: DescentLedger.BuyBackOwner(ch, "engineer")))));
        Assert.Equal(ItemState.Consumed, (await h.R(tx => tx.Items.Get(sold[0])))!.State);
    }

    [Fact]
    public async Task A_vendor_roll_sweeps_the_unsold_stock()
    {
        var h = await LedgerHarness.Create();
        await using var _ = h;
        await h.W(tx => h.Ledger.RollVendor(tx, h.Hub, "engineer"));
        await h.W(tx => h.Ledger.RollVendor(tx, h.Hub, "engineer"));
        Assert.Equal(6, await h.R(tx => tx.Items.Count(new ItemQuery(OwnerKind: OwnerKind.Vendor))));
        Assert.Equal(6, await h.R(tx => tx.Items.Count(new ItemQuery(OwnerKind: OwnerKind.Vendor, State: ItemState.Swept))));
    }

    [Fact]
    public async Task A_craft_consumes_three_mints_one_and_pays()
    {
        var h = await LedgerHarness.Create();
        await using var _ = h;
        var ch = await h.Character();
        await h.Lease(ch, h.Hub);
        await h.W(tx => tx.Characters.AddAustralium(ch, 10, "test", null));
        var inputs = new List<string>();
        for (var i = 0; i < 3; i++) inputs.Add((await h.Give(ch, tier: 1)).Id);
        var made = await h.W(tx => h.Ledger.Transform(tx, h.Hub, ch, h.Token(ch), "craft", inputs, "combine3", h.Req()));
        Assert.Equal(2, Assert.Single(made).Rarity);
        Assert.All(inputs, id => Assert.Equal(ItemState.Consumed, h.R(tx => tx.Items.Get(id)).Result!.State));
        Assert.Equal(5, (await h.R(tx => tx.Characters.Get(ch)))!.Australium);
        await h.AssertBalanced();
    }

    [Fact]
    public async Task Identify_changes_the_item_and_mints_nothing()
    {
        var h = await LedgerHarness.Create();
        await using var _ = h;
        var ch = await h.Character();
        await h.Lease(ch, h.Hub);
        await h.W(tx => tx.Characters.AddAustralium(ch, 10, "test", null));
        var strange = await h.Give(ch, tier: 2);
        Assert.False(strange.Identified);
        var before = await h.Totals();
        var after = Assert.Single(await h.W(tx => h.Ledger.Transform(tx, h.Hub, ch, h.Token(ch), "identify", [strange.Id], null, h.Req())));
        Assert.Equal((strange.Id, true), (after.Id, after.Identified));
        Assert.Equal(before, await h.Totals());
    }

    static async Task<(LedgerHarness H, string A, string B, ItemRecord ItemA, ItemRecord ItemB, TradeRecord T)> Trade()
    {
        var h = await LedgerHarness.Create();
        var a = await h.Character("Ann", account: "1");
        var b = await h.Character("Bo", account: "2");
        await h.Lease(a, h.Hub);
        await h.Lease(b, h.Hub);
        var ia = await h.Give(a);
        var ib = await h.Give(b);
        var t = await h.W(tx => h.Ledger.TradeOpen(tx, h.Hub, a, h.Token(a), b, h.Req()));
        await h.W(tx => h.Ledger.TradeOffer(tx, h.Hub, a, h.Token(a), t.Id, [ia.Id], 0, h.Req()));
        await h.W(tx => h.Ledger.TradeOffer(tx, h.Hub, b, h.Token(b), t.Id, [ib.Id], 0, h.Req()));
        return (h, a, b, ia, ib, t);
    }

    [Fact]
    public async Task A_confirm_before_both_locks_is_refused()
    {
        var (h, a, _, _, _, t) = await Trade();
        await using var _ = h;
        await h.W(tx => h.Ledger.TradeLock(tx, h.Hub, a, h.Token(a), t.Id, h.Req()));
        var e = await Assert.ThrowsAsync<HostRefusal>(() => h.W(tx => h.Ledger.TradeConfirm(tx, h.Hub, a, h.Token(a), t.Id, h.Req())));
        Assert.Equal("trade_state", e.Reason);
    }

    [Fact]
    public async Task A_locked_offer_sits_in_escrow()
    {
        var (h, a, _, ia, _, t) = await Trade();
        await using var _ = h;
        await h.W(tx => h.Ledger.TradeLock(tx, h.Hub, a, h.Token(a), t.Id, h.Req()));
        Assert.Equal((OwnerKind.TradeEscrow, t.Id), ((await h.R(tx => tx.Items.Get(ia.Id)))!.OwnerKind, (await h.R(tx => tx.Items.Get(ia.Id)))!.OwnerId));
    }

    [Fact]
    public async Task The_second_confirm_swaps_the_items()
    {
        var (h, a, b, ia, ib, t) = await Trade();
        await using var _ = h;
        await h.W(tx => h.Ledger.TradeLock(tx, h.Hub, a, h.Token(a), t.Id, h.Req()));
        await h.W(tx => h.Ledger.TradeLock(tx, h.Hub, b, h.Token(b), t.Id, h.Req()));
        await h.W(tx => h.Ledger.TradeConfirm(tx, h.Hub, a, h.Token(a), t.Id, h.Req()));
        var done = await h.W(tx => h.Ledger.TradeConfirm(tx, h.Hub, b, h.Token(b), t.Id, h.Req()));
        Assert.Equal(TradeStatus.Committed, done.State);
        Assert.Equal((b, a), ((await h.R(tx => tx.Items.Get(ia.Id)))!.OwnerId, (await h.R(tx => tx.Items.Get(ib.Id)))!.OwnerId));
    }

    [Fact]
    public async Task A_cancel_returns_the_escrow()
    {
        var (h, a, _, ia, _, t) = await Trade();
        await using var _ = h;
        await h.W(tx => h.Ledger.TradeLock(tx, h.Hub, a, h.Token(a), t.Id, h.Req()));
        await h.W(tx => h.Ledger.TradeCancel(tx, h.Hub, a, h.Token(a), t.Id, h.Req()));
        Assert.Equal((OwnerKind.Character, a), ((await h.R(tx => tx.Items.Get(ia.Id)))!.OwnerKind, (await h.R(tx => tx.Items.Get(ia.Id)))!.OwnerId));
    }

    [Fact]
    public async Task A_hub_reap_cancels_its_trades_and_returns_escrow()
    {
        var (h, a, _, ia, _, t) = await Trade();
        await using var _ = h;
        await h.W(tx => h.Ledger.TradeLock(tx, h.Hub, a, h.Token(a), t.Id, h.Req()));
        var counts = await h.W(tx => h.Ledger.SweepInstance(tx, h.Hub));
        Assert.Equal((1, 1), (counts.EscrowReturned, counts.TradesCancelled));
        Assert.Equal(OwnerKind.Character, (await h.R(tx => tx.Items.Get(ia.Id)))!.OwnerKind);
    }

    [Fact]
    public async Task Reconcile_finds_nothing_on_a_clean_ledger()
    {
        var (h, _, _) = await WithWorldItem();
        await using var _ = h;
        Assert.Empty(await h.W(tx => DescentLedger.Reconcile(tx, fixCachedSums: false)));
    }

    [Fact]
    public async Task Reconcile_finds_a_world_item_of_a_reaped_instance()
    {
        var (h, _, item) = await WithWorldItem();
        await using var _ = h;
        await h.W(tx => tx.Instances.Update(h.Level.Id, i => i with { State = InstanceState.Reaped }));
        var f = Assert.Single(await h.W(tx => DescentLedger.Reconcile(tx, fixCachedSums: false)), x => x.Kind == "world_item_orphan");
        Assert.Equal(item.Id, f.Target);
    }
}
