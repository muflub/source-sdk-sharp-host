using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;
using SourceSharp.Host.Testing;

namespace TF2.Descent.Service.Tests.Ledger;

/// <summary>plan_sdk_2_8 §4: two carried items exchange their places in one write.</summary>
public class SwapFacts
{
    const string Potion = "health_small";

    static async Task<(LedgerHarness H, string Ch)> On()
    {
        var h = await LedgerHarness.Create();
        var ch = await h.Character();
        await h.Lease(ch, h.Level);
        return (h, ch);
    }

    static Task<IReadOnlyList<ItemRecord>> Swap(LedgerHarness h, string ch, string a, string b) =>
        h.W(tx => h.Ledger.Swap(tx, h.Level, ch, h.Token(ch), a, b, h.Req()));

    static Task<ItemRecord> Move(LedgerHarness h, string ch, string itemId, ItemContainer container, int slot) =>
        h.W(tx => h.Ledger.Move(tx, h.Level, ch, h.Token(ch), itemId, container, slot, h.Req()));

    static async Task<(ItemContainer, int)> At(LedgerHarness h, string itemId)
    {
        var i = (await h.R(tx => tx.Items.Get(itemId)))!;
        return (i.Container, i.Slot);
    }

    [Fact]
    public async Task Two_items_exchange_container_and_slot()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var worn = await h.Give(ch, slot: 0);
        var spare = await h.Give(ch, slot: 5);
        await Move(h, ch, worn.Id, ItemContainer.Equipped, 2);
        await Swap(h, ch, worn.Id, spare.Id);
        Assert.Equal(((ItemContainer.Backpack, 5), (ItemContainer.Equipped, 2)), (await At(h, worn.Id), await At(h, spare.Id)));
    }

    [Fact]
    public async Task An_equip_goes_through_a_full_backpack()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var worn = await h.Give(ch, slot: 0);
        await Move(h, ch, worn.Id, ItemContainer.Equipped, 2);
        var cells = new List<ItemRecord>();
        for (var s = 0; s < h.Rules.Backpack; s++) cells.Add(await h.Give(ch, slot: s));
        // The positive arm of "full": a plain unequip has nowhere to go.
        var full = await Assert.ThrowsAsync<HostRefusal>(() => Move(h, ch, worn.Id, ItemContainer.Backpack, 7));
        Assert.Equal("slot_taken", full.Reason);
        await Swap(h, ch, worn.Id, cells[7].Id);
        Assert.Equal(((ItemContainer.Backpack, 7), (ItemContainer.Equipped, 2)), (await At(h, worn.Id), await At(h, cells[7].Id)));
    }

    [Fact]
    public async Task A_destination_the_rules_refuse_refuses_both_moves()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var potion = await h.Give(ch, slot: 0, baseType: Potion);
        var gun = await h.Give(ch, slot: 1);
        await Move(h, ch, potion.Id, ItemContainer.Belt, 0);
        var e = await Assert.ThrowsAsync<HostRefusal>(() => Swap(h, ch, potion.Id, gun.Id));
        Assert.Equal("potions_only", e.Reason);
        Assert.Equal(((ItemContainer.Belt, 0), (ItemContainer.Backpack, 1)), (await At(h, potion.Id), await At(h, gun.Id)));
    }

    [Fact]
    public async Task Swapping_with_another_characters_item_is_refused()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var mine = await h.Give(ch, slot: 0);
        var other = await h.Character("Bo", account: "2");
        var theirs = await h.Give(other, slot: 3);
        await Assert.ThrowsAsync<HostRefusal>(() => Swap(h, ch, mine.Id, theirs.Id));
        Assert.Equal((ItemContainer.Backpack, 3), await At(h, theirs.Id));
    }

    [Fact]
    public async Task Swapping_an_item_with_itself_is_refused()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var item = await h.Give(ch);
        var e = await Assert.ThrowsAsync<HostRefusal>(() => Swap(h, ch, item.Id, item.Id));
        Assert.Equal("bad_items", e.Reason);
    }
}
