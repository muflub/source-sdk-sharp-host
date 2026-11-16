using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;
using SourceSharp.Host.Testing;

namespace SourceSharp.Host.Data.Tests;

/// <summary>plan_sdk_2_7 §1: an item's container is a column; a partial consume lowers a stack's count in place.</summary>
public class ContainerAndCountFacts
{
    static Task<ItemRecord> MintCarried(TestData d, ItemContainer container = ItemContainer.Backpack, int slot = 0) =>
        d.Write(tx => tx.Items.Mint(TestData.Item(), OwnerKind.Character, "ch1", null, slot, "test", "req-mint", container));

    [Fact]
    public async Task A_mint_lands_in_the_backpack_unless_told_otherwise()
    {
        await using var d = new TestData();
        Assert.Equal(ItemContainer.Backpack, (await MintCarried(d)).Container);
        var belt = await MintCarried(d, ItemContainer.Belt, 2);
        Assert.Equal((ItemContainer.Belt, 2), ((await d.Read(tx => tx.Items.Get(belt.Id)))!.Container, belt.Slot));
    }

    [Fact]
    public async Task A_move_sets_the_container()
    {
        await using var d = new TestData();
        var item = await MintCarried(d);
        var moved = await d.Write(tx => tx.Items.Move(new ItemMove(item.Id, OwnerKind.Character, OwnerKind.Character, "ch1", "move",
            Slot: 1, Container: ItemContainer.Equipped)));
        Assert.Equal((ItemContainer.Equipped, 1), (moved.Container, moved.Slot));
    }

    [Fact]
    public async Task A_move_that_names_no_container_lands_in_the_backpack()
    {
        await using var d = new TestData();
        var item = await MintCarried(d, ItemContainer.Equipped, 4);
        var moved = await d.Write(tx => tx.Items.Move(new ItemMove(item.Id, OwnerKind.Character, OwnerKind.Character, "ch1", "move", Slot: 0)));
        Assert.Equal(ItemContainer.Backpack, moved.Container);
    }

    [Fact]
    public async Task SetCount_changes_the_count_and_the_instance_and_bumps_the_version()
    {
        await using var d = new TestData();
        var item = await MintCarried(d);
        var after = await d.Write(tx => tx.Items.SetCount(item.Id, item.Version, 3, [7, 7], 2, "consumed", "req-c"));
        Assert.Equal((3, 2, item.Version + 1, ItemState.Live), (after.Count, after.SchemaVersion, after.Version, after.State));
        Assert.Equal([7, 7], after.Instance);
        Assert.Equal("consumed", (await d.Read(tx => tx.Items.Events(item.Id)))[^1].Kind);
    }

    [Fact]
    public async Task SetCount_at_a_stale_version_is_refused()
    {
        await using var d = new TestData();
        var item = await MintCarried(d);
        var e = await Assert.ThrowsAsync<HostRefusal>(() =>
            d.Write(tx => tx.Items.SetCount(item.Id, item.Version + 1, 3, item.Instance, item.SchemaVersion, "consumed", null)));
        Assert.Equal("version_conflict", e.Reason);
    }

    [Fact]
    public async Task SetCount_to_zero_is_refused()
    {
        await using var d = new TestData();
        var item = await MintCarried(d);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            d.Write(tx => tx.Items.SetCount(item.Id, item.Version, 0, item.Instance, item.SchemaVersion, "consumed", null)));
        Assert.Equal(item.Count, (await d.Read(tx => tx.Items.Get(item.Id)))!.Count);
    }
}
