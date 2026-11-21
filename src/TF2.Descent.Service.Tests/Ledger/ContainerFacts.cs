using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;
using SourceSharp.Host.Testing;

namespace TF2.Descent.Service.Tests.Ledger;

/// <summary>plan_sdk_2_7 §1: backpack, equip slots and belt; the rules say what may go where.</summary>
public class ContainerFacts
{
    const string Potion = "health_small";

    static async Task<(LedgerHarness H, string Ch)> On()
    {
        var h = await LedgerHarness.Create();
        var ch = await h.Character();
        await h.Lease(ch, h.Level);
        return (h, ch);
    }

    static Task<ItemRecord> Move(LedgerHarness h, string ch, string itemId, ItemContainer container, int slot) =>
        h.W(tx => h.Ledger.Move(tx, h.Level, ch, h.Token(ch), itemId, container, slot, h.Req()));

    static async Task<string> Refusal(Task work) => (await Assert.ThrowsAsync<HostRefusal>(() => work)).Reason;

    [Fact]
    public async Task Equipping_puts_the_item_in_the_named_slot()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var item = await h.Give(ch, slot: 0);
        var hat = Array.IndexOf(FakeGameRules.Slots, "hat");
        var moved = await Move(h, ch, item.Id, ItemContainer.Equipped, hat);
        Assert.Equal((ItemContainer.Equipped, hat), (moved.Container, moved.Slot));
    }

    [Fact]
    public async Task An_equipped_item_frees_its_backpack_cell()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var a = await h.Give(ch, slot: 0);
        var b = await h.Give(ch, slot: 1);
        await Move(h, ch, a.Id, ItemContainer.Equipped, 0);
        var moved = await Move(h, ch, b.Id, ItemContainer.Backpack, 0);
        Assert.Equal((ItemContainer.Backpack, 0), (moved.Container, moved.Slot));
    }

    [Fact]
    public async Task The_same_slot_number_in_another_container_is_free()
    {
        var (h, ch) = await On();
        await using var _ = h;
        await h.Give(ch, slot: 2);
        var potion = await h.Give(ch, slot: 5, baseType: Potion);
        var moved = await Move(h, ch, potion.Id, ItemContainer.Belt, 2);
        Assert.Equal((ItemContainer.Belt, 2), (moved.Container, moved.Slot));
    }

    [Fact]
    public async Task An_occupied_slot_of_the_same_container_is_refused()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var a = await h.Give(ch, slot: 0);
        var b = await h.Give(ch, slot: 1);
        await Move(h, ch, a.Id, ItemContainer.Equipped, 4);
        Assert.Equal("slot_taken", await Refusal(Move(h, ch, b.Id, ItemContainer.Equipped, 4)));
    }

    [Fact]
    public async Task A_slot_past_the_containers_end_is_refused()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var potion = await h.Give(ch, baseType: Potion);
        // The positive arm: the last belt slot is accepted.
        await Move(h, ch, potion.Id, ItemContainer.Belt, FakeGameRules.BeltSlots - 1);
        Assert.Equal("bad_slot", await Refusal(Move(h, ch, potion.Id, ItemContainer.Belt, FakeGameRules.BeltSlots)));
    }

    [Fact]
    public async Task An_unknown_container_is_refused()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var item = await h.Give(ch);
        Assert.Equal("bad_container", await Refusal(Move(h, ch, item.Id, (ItemContainer)7, 0)));
    }

    [Fact]
    public async Task The_belt_takes_potions_only()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var potion = await h.Give(ch, baseType: Potion);
        var gun = await h.Give(ch, baseType: "scattergun");
        await Move(h, ch, potion.Id, ItemContainer.Belt, 0);
        Assert.Equal("potions_only", await Refusal(Move(h, ch, gun.Id, ItemContainer.Belt, 1)));
        Assert.Equal(ItemContainer.Backpack, (await h.R(tx => tx.Items.Get(gun.Id)))!.Container);
    }

    [Fact]
    public async Task A_potion_cannot_be_equipped()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var potion = await h.Give(ch, baseType: Potion);
        Assert.Equal("wrong_slot", await Refusal(Move(h, ch, potion.Id, ItemContainer.Equipped, 0)));
    }

    [Fact]
    public async Task Unequipping_puts_the_item_back_in_a_backpack_cell()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var item = await h.Give(ch, slot: 0);
        await Move(h, ch, item.Id, ItemContainer.Equipped, 1);
        var back = await Move(h, ch, item.Id, ItemContainer.Backpack, 7);
        Assert.Equal((ItemContainer.Backpack, 7), (back.Container, back.Slot));
    }

    [Fact]
    public async Task The_26_move_is_a_backpack_move()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var item = await h.Give(ch, slot: 0);
        await Move(h, ch, item.Id, ItemContainer.Equipped, 1);
        var moved = await h.W(tx => h.Ledger.Move(tx, h.Level, ch, h.Token(ch), item.Id, 3, h.Req()));
        Assert.Equal((ItemContainer.Backpack, 3), (moved.Container, moved.Slot));
    }

    [Fact]
    public async Task Death_moves_every_container_to_the_corpse()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var gun = await h.Give(ch, slot: 0);
        var hat = await h.Give(ch, slot: 1);
        var potion = await h.Give(ch, slot: 2, baseType: Potion);
        await Move(h, ch, hat.Id, ItemContainer.Equipped, 3);
        await Move(h, ch, potion.Id, ItemContainer.Belt, 0);
        var moved = await h.W(tx => h.Ledger.RecordDeath(tx, h.Level, ch, h.Token(ch), h.Req()));
        Assert.Equal(new[] { gun.Id, hat.Id, potion.Id }.Order(), moved.Select(i => i.Id).Order());
        Assert.All(moved, i => Assert.Equal(OwnerKind.Corpse, i.OwnerKind));
    }

    [Fact]
    public async Task Looting_a_corpse_puts_equipped_items_in_backpack_cells()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var hat = await h.Give(ch, slot: 0);
        await Move(h, ch, hat.Id, ItemContainer.Equipped, 3);
        await h.W(tx => h.Ledger.RecordDeath(tx, h.Level, ch, h.Token(ch), h.Req()));
        var looted = Assert.Single(await h.W(tx => h.Ledger.LootCorpse(tx, h.Level, ch, h.Token(ch), h.Req())));
        Assert.Equal((ItemContainer.Backpack, 0), (looted.Container, looted.Slot));
    }
}
