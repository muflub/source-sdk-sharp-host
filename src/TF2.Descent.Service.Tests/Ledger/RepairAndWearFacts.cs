using TF2.Descent.Service.Ledger;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;
using SourceSharp.Host.Testing;

namespace TF2.Descent.Service.Tests.Ledger;

/// <summary>plan_sdk_2_7 §2: batched wear, the town repair (hub only) and the field repair with a kit.</summary>
public class RepairAndWearFacts
{
    static async Task<(LedgerHarness H, string Ch)> On(InstanceRecord? at = null)
    {
        var h = await LedgerHarness.Create();
        var ch = await h.Character();
        await h.Lease(ch, at ?? h.Hub);
        return (h, ch);
    }

    static async Task<(int Durability, int Max)> DurabilityOf(LedgerHarness h, string itemId)
    {
        var row = await h.R(tx => tx.Items.Get(itemId));
        return FakeGameRules.ItemCodec.DurabilityOf(h.Rules.Items.Decode(row!.Instance, row.SchemaVersion));
    }

    static Task<IReadOnlyList<ItemRecord>> Wear(LedgerHarness h, InstanceRecord at, string ch, params (string, int)[] entries) =>
        h.W(tx => h.Ledger.Wear(tx, at, ch, h.Token(ch), entries, h.Req()));

    static Task<ItemRecord> Repair(LedgerHarness h, InstanceRecord at, string ch, string itemId, RepairKind kind, string? kit = null) =>
        h.W(tx => h.Ledger.Repair(tx, at, ch, h.Token(ch), itemId, kind, kit, h.Req()));

    // ------------------------------------------------------------------ wear

    [Fact]
    public async Task Wear_lowers_durability_in_place()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var item = await h.Give(ch, slot: 3);
        var worn = await Wear(h, h.Hub, ch, (item.Id, 30));
        Assert.Equal((item.Id, 3), (worn[0].Id, worn[0].Slot));
        Assert.Equal((70, 100), await DurabilityOf(h, item.Id));
    }

    [Fact]
    public async Task Wear_writes_every_item_of_the_batch()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var a = await h.Give(ch);
        var b = await h.Give(ch);
        await Wear(h, h.Hub, ch, (a.Id, 10), (b.Id, 25));
        Assert.Equal((90, 75), ((await DurabilityOf(h, a.Id)).Durability, (await DurabilityOf(h, b.Id)).Durability));
    }

    [Fact]
    public async Task Wear_stops_at_zero_and_the_item_stays()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var item = await h.Give(ch);
        await Wear(h, h.Hub, ch, (item.Id, 500));
        Assert.Equal((0, 100), await DurabilityOf(h, item.Id));
        Assert.Equal(ItemState.Live, (await h.R(tx => tx.Items.Get(item.Id)))!.State);
    }

    [Fact]
    public async Task A_refused_entry_refuses_the_whole_batch()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var mine = await h.Give(ch);
        var other = await h.Character("Bo", account: "2");
        var theirs = await h.Give(other);
        // The positive arm: the same first entry alone is written.
        await Wear(h, h.Hub, ch, (mine.Id, 5));
        Assert.Equal(95, (await DurabilityOf(h, mine.Id)).Durability);
        await Assert.ThrowsAsync<HostRefusal>(() => Wear(h, h.Hub, ch, (mine.Id, 5), (theirs.Id, 5)));
        Assert.Equal(95, (await DurabilityOf(h, mine.Id)).Durability);
    }

    [Fact]
    public async Task Wear_naming_an_item_twice_is_refused()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var item = await h.Give(ch);
        var e = await Assert.ThrowsAsync<HostRefusal>(() => Wear(h, h.Hub, ch, (item.Id, 5), (item.Id, 5)));
        Assert.Equal("bad_entries", e.Reason);
    }

    [Fact]
    public async Task Wear_of_zero_is_refused()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var item = await h.Give(ch);
        var e = await Assert.ThrowsAsync<HostRefusal>(() => Wear(h, h.Hub, ch, (item.Id, 0)));
        Assert.Equal("bad_amount", e.Reason);
    }

    [Fact]
    public async Task Wear_runs_on_a_level()
    {
        var h = await LedgerHarness.Create();
        await using var _ = h;
        var ch = await h.Character();
        await h.Lease(ch, h.Level);
        var item = await h.Give(ch);
        await Wear(h, h.Level, ch, (item.Id, 1));
        Assert.Equal(99, (await DurabilityOf(h, item.Id)).Durability);
    }

    // ------------------------------------------------------------------ town repair

    [Fact]
    public async Task A_town_repair_restores_durability_and_keeps_the_item()
    {
        var (h, ch) = await On();
        await using var _ = h;
        await h.W(tx => tx.Characters.AddAustralium(ch, 10, "test", null));
        var item = await h.Give(ch, slot: 2);
        await Wear(h, h.Hub, ch, (item.Id, 40));
        var repaired = await Repair(h, h.Hub, ch, item.Id, RepairKind.Town);
        Assert.Equal((item.Id, 2), (repaired.Id, repaired.Slot));
        Assert.Equal((100, 100), await DurabilityOf(h, item.Id));
    }

    [Fact]
    public async Task A_town_repair_costs_australium()
    {
        var (h, ch) = await On();
        await using var _ = h;
        await h.W(tx => tx.Characters.AddAustralium(ch, 10, "test", null));
        var item = await h.Give(ch);
        await Repair(h, h.Hub, ch, item.Id, RepairKind.Town);
        Assert.Equal(9, (await h.R(tx => tx.Characters.Get(ch)))!.Australium);
    }

    [Fact]
    public async Task A_town_repair_on_a_level_is_refused()
    {
        var h = await LedgerHarness.Create();
        await using var _ = h;
        var ch = await h.Character();
        await h.Lease(ch, h.Level);
        var item = await h.Give(ch);
        var e = await Assert.ThrowsAsync<HostRefusal>(() => Repair(h, h.Level, ch, item.Id, RepairKind.Town));
        Assert.Equal("not_a_hub", e.Reason);
    }

    [Fact]
    public async Task A_town_repair_given_a_kit_is_refused()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var item = await h.Give(ch);
        var kit = await h.Give(ch, baseType: FakeGameRules.RepairKit);
        var e = await Assert.ThrowsAsync<HostRefusal>(() => Repair(h, h.Hub, ch, item.Id, RepairKind.Town, kit.Id));
        Assert.Equal("bad_kit", e.Reason);
    }

    // ------------------------------------------------------------------ field repair

    [Fact]
    public async Task A_field_repair_keeps_the_item_and_lowers_its_maximum()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var item = await h.Give(ch, slot: 4);
        var kit = await h.Give(ch, baseType: FakeGameRules.RepairKit);
        await Wear(h, h.Hub, ch, (item.Id, 60));
        var repaired = await Repair(h, h.Hub, ch, item.Id, RepairKind.Field, kit.Id);
        Assert.Equal((item.Id, 4), (repaired.Id, repaired.Slot));
        var (d, max) = await DurabilityOf(h, item.Id);
        Assert.True(max < 100, $"max {max}");
        Assert.True(d > 40 && d <= max, $"durability {d} of {max}");
    }

    [Fact]
    public async Task A_field_repair_consumes_the_kit_in_the_same_write()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var item = await h.Give(ch);
        var kit = await h.Give(ch, baseType: FakeGameRules.RepairKit);
        await Repair(h, h.Hub, ch, item.Id, RepairKind.Field, kit.Id);
        Assert.Equal(ItemState.Consumed, (await h.R(tx => tx.Items.Get(kit.Id)))!.State);
        await h.AssertBalanced();
    }

    [Fact]
    public async Task A_field_repair_runs_on_a_level()
    {
        var h = await LedgerHarness.Create();
        await using var _ = h;
        var ch = await h.Character();
        await h.Lease(ch, h.Level);
        var item = await h.Give(ch);
        var kit = await h.Give(ch, baseType: FakeGameRules.RepairKit);
        var repaired = await Repair(h, h.Level, ch, item.Id, RepairKind.Field, kit.Id);
        Assert.Equal(item.Id, repaired.Id);
    }

    [Fact]
    public async Task A_field_repair_with_something_that_is_not_a_kit_changes_nothing()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var item = await h.Give(ch);
        var notKit = await h.Give(ch, baseType: "wrench");
        await Wear(h, h.Hub, ch, (item.Id, 10));
        var e = await Assert.ThrowsAsync<HostRefusal>(() => Repair(h, h.Hub, ch, item.Id, RepairKind.Field, notKit.Id));
        Assert.Equal("not_a_kit", e.Reason);
        Assert.Equal(ItemState.Live, (await h.R(tx => tx.Items.Get(notKit.Id)))!.State);
        Assert.Equal(90, (await DurabilityOf(h, item.Id)).Durability);
    }

    [Fact]
    public async Task A_field_repair_without_a_kit_is_refused()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var item = await h.Give(ch);
        var e = await Assert.ThrowsAsync<HostRefusal>(() => Repair(h, h.Hub, ch, item.Id, RepairKind.Field));
        Assert.Equal("bad_kit", e.Reason);
    }

    [Fact]
    public async Task A_field_repair_with_another_players_kit_is_refused()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var item = await h.Give(ch);
        var other = await h.Character("Bo", account: "2");
        var theirKit = await h.Give(other, baseType: FakeGameRules.RepairKit);
        await Assert.ThrowsAsync<HostRefusal>(() => Repair(h, h.Hub, ch, item.Id, RepairKind.Field, theirKit.Id));
        Assert.Equal(ItemState.Live, (await h.R(tx => tx.Items.Get(theirKit.Id)))!.State);
    }
}
