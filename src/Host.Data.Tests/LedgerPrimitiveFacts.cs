using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Testing;

namespace SourceSharp.Host.Data.Tests;

/// <summary>Plan §5.1: exactly one owner at a time; a move affects one row or none, and writes its event.</summary>
public class LedgerPrimitiveFacts
{
    static Task<ItemRecord> MintWorld(TestData d, string instance = "inst-a") =>
        d.Write(tx => tx.Items.Mint(TestData.Item(), OwnerKind.World, instance, instance, -1, "test", "req-mint"));

    [Fact]
    public async Task A_mint_is_live_owned_and_has_its_event()
    {
        await using var d = new TestData();
        var item = await MintWorld(d);
        Assert.Equal((ItemState.Live, OwnerKind.World, 1L), (item.State, item.OwnerKind, item.Version));
        var ev = Assert.Single(await d.Read(tx => tx.Items.Events(item.Id)));
        Assert.Equal(("mint:test", "World:inst-a"), (ev.Kind, ev.ToOwner));
    }

    [Fact]
    public async Task A_move_changes_the_owner_bumps_the_version_and_records_both_ends()
    {
        await using var d = new TestData();
        var item = await MintWorld(d);
        var moved = await d.Write(tx => tx.Items.Move(new ItemMove(item.Id, OwnerKind.World, OwnerKind.Character, "ch1", "claim", Slot: 3)));
        Assert.Equal((OwnerKind.Character, "ch1", 3, 2L), (moved.OwnerKind, moved.OwnerId, moved.Slot, moved.Version));
        var ev = (await d.Read(tx => tx.Items.Events(item.Id)))[^1];
        Assert.Equal(("claim", "World:inst-a", "Character:ch1"), (ev.Kind, ev.FromOwner, ev.ToOwner));
    }

    [Fact]
    public async Task A_move_from_the_wrong_owner_is_refused_and_writes_no_event()
    {
        await using var d = new TestData();
        var item = await MintWorld(d);
        var e = await Assert.ThrowsAsync<HostRefusal>(() =>
            d.Write(tx => tx.Items.Move(new ItemMove(item.Id, OwnerKind.Reserve, OwnerKind.Character, "ch1", "claim"))));
        Assert.Equal("wrong_owner", e.Reason);
        Assert.Single(await d.Read(tx => tx.Items.Events(item.Id)));
        Assert.Equal(OwnerKind.World, (await d.Read(tx => tx.Items.Get(item.Id)))!.OwnerKind);
    }

    [Fact]
    public async Task A_move_at_a_stale_version_is_refused()
    {
        await using var d = new TestData();
        var item = await MintWorld(d);
        await d.Write(tx => tx.Items.Move(new ItemMove(item.Id, OwnerKind.World, OwnerKind.World, "inst-a", "nudge", InstanceId: "inst-a")));
        var e = await Assert.ThrowsAsync<HostRefusal>(() =>
            d.Write(tx => tx.Items.Move(new ItemMove(item.Id, OwnerKind.World, OwnerKind.Character, "ch1", "claim", ExpectedVersion: 1))));
        Assert.Equal("version_conflict", e.Reason);
    }

    [Fact]
    public async Task A_move_expecting_another_instance_is_refused()
    {
        await using var d = new TestData();
        var item = await MintWorld(d, "inst-a");
        var e = await Assert.ThrowsAsync<HostRefusal>(() =>
            d.Write(tx => tx.Items.Move(new ItemMove(item.Id, OwnerKind.World, OwnerKind.Character, "ch1", "claim", ExpectedInstanceId: "inst-b"))));
        Assert.Equal("wrong_instance", e.Reason);
    }

    [Fact]
    public async Task A_terminal_item_cannot_move()
    {
        await using var d = new TestData();
        var item = await MintWorld(d);
        await d.Write(tx => tx.Items.Terminate(item.Id, OwnerKind.World, ItemState.Swept, "level_shutdown", null));
        var e = await Assert.ThrowsAsync<HostRefusal>(() =>
            d.Write(tx => tx.Items.Move(new ItemMove(item.Id, OwnerKind.World, OwnerKind.Character, "ch1", "claim"))));
        Assert.Equal("wrong_owner", e.Reason);
    }

    [Fact]
    public async Task A_failed_transaction_leaves_no_move_and_no_event()
    {
        await using var d = new TestData();
        var item = await MintWorld(d);
        await Assert.ThrowsAsync<InvalidOperationException>(() => d.Write(async tx =>
        {
            await tx.Items.Move(new ItemMove(item.Id, OwnerKind.World, OwnerKind.Character, "ch1", "claim"));
            throw new InvalidOperationException("fails after the move");
        }));
        Assert.Equal(OwnerKind.World, (await d.Read(tx => tx.Items.Get(item.Id)))!.OwnerKind);
        Assert.Single(await d.Read(tx => tx.Items.Events(item.Id)));
    }

    [Fact]
    public async Task Totals_reconcile_minted_minus_terminal_to_live()
    {
        await using var d = new TestData();
        var a = await MintWorld(d);
        await MintWorld(d);
        await MintWorld(d);
        await d.Write(tx => tx.Items.Terminate(a.Id, OwnerKind.World, ItemState.Destroyed, "forged_reveal", null));
        var (minted, terminal, live) = await d.Read(tx => tx.Items.Totals());
        Assert.Equal((3L, 1L, 2L), (minted, terminal, live));
    }

    [Fact]
    public async Task Totals_survive_the_purge_of_old_terminal_rows()
    {
        await using var d = new TestData();
        var a = await MintWorld(d);
        await MintWorld(d);
        await d.Write(tx => tx.Items.Terminate(a.Id, OwnerKind.World, ItemState.Swept, "level_shutdown", null));
        d.Clock.Advance(TimeSpan.FromDays(31));
        Assert.Equal(1, await d.Write(tx => tx.Items.PurgeTerminalOlderThan(d.Clock.GetUtcNow() - TimeSpan.FromDays(30))));
        var (minted, terminal, live) = await d.Read(tx => tx.Items.Totals());
        Assert.Equal(minted - terminal, live);
    }

    [Fact]
    public async Task A_mutation_keeps_the_owner_and_flags_an_admin_edit()
    {
        await using var d = new TestData();
        var item = await MintWorld(d);
        var m = await d.Write(tx => tx.Items.Mutate(item.Id, 1, [9, 9], 2, identified: true, adminEdited: true, "admin_edit", null));
        Assert.Equal((OwnerKind.World, true, 2, 2L), (m.OwnerKind, m.AdminEdited, m.SchemaVersion, m.Version));
    }

    [Fact]
    public async Task Query_filters_by_owner_and_state()
    {
        await using var d = new TestData();
        var a = await MintWorld(d, "inst-a");
        await MintWorld(d, "inst-b");
        await d.Write(tx => tx.Items.Terminate(a.Id, OwnerKind.World, ItemState.Swept, "x", null));
        Assert.Empty(await d.Read(tx => tx.Items.Query(new ItemQuery(InstanceId: "inst-a"))));
        Assert.Single(await d.Read(tx => tx.Items.Query(new ItemQuery(InstanceId: "inst-a", State: ItemState.Swept))));
        Assert.Equal(1, await d.Read(tx => tx.Items.Count(new ItemQuery(OwnerKind: OwnerKind.World))));
    }
}
