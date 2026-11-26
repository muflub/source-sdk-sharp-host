using TF2.Descent.Service.Ledger;
using Microsoft.Extensions.Options;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;
using SourceSharp.Host.Testing;

namespace TF2.Descent.Service.Tests.Ledger;

/// <summary>
/// A rules module built on Host.Contracts 1.0 implements none of 1.1's members; the defaults
/// must keep its 1.0 behavior (plan_sdk_2_7: every new member has a default body).
/// </summary>
public class Contract10RulesFacts
{
    /// <summary>Exactly the 1.0 surface (<see cref="Contract10Rules"/>); no 1.1 member is implemented.</summary>
    internal class Rules10(FakeGameRules f) : Contract10Rules(f);

    static async Task<(LedgerHarness H, DescentLedger Ledger, string Ch)> With10Rules()
    {
        var h = await LedgerHarness.Create();
        var ledger = new DescentLedger(new SingleRulesProvider(new Rules10(h.Rules)), Options.Create(h.Options));
        var ch = await h.Character();
        await h.Lease(ch, h.Hub);
        await h.W(tx => tx.Characters.AddAustralium(ch, 10, "test", null));
        return (h, ledger, ch);
    }

    [Fact]
    public async Task A_town_repair_runs_the_10_repair()
    {
        var (h, ledger, ch) = await With10Rules();
        await using var _ = h;
        var item = await h.Give(ch);
        var repaired = await h.W(tx => ledger.Repair(tx, h.Hub, ch, h.Token(ch), item.Id, RepairKind.Town, null, h.Req()));
        Assert.Equal(item.Id, repaired.Id);
    }

    [Fact]
    public async Task A_field_repair_is_refused_as_unsupported()
    {
        var (h, ledger, ch) = await With10Rules();
        await using var _ = h;
        var item = await h.Give(ch);
        var kit = await h.Give(ch, baseType: FakeGameRules.RepairKit);
        // The positive arm: the same call is accepted by rules that implement it.
        await h.W(tx => h.Ledger.Repair(tx, h.Hub, ch, h.Token(ch), item.Id, RepairKind.Field, kit.Id, h.Req()));
        var kit2 = await h.Give(ch, baseType: FakeGameRules.RepairKit);
        var e = await Assert.ThrowsAsync<HostRefusal>(() => h.W(tx => ledger.Repair(tx, h.Hub, ch, h.Token(ch), item.Id, RepairKind.Field, kit2.Id, h.Req())));
        Assert.Equal("unsupported", e.Reason);
    }

    [Fact]
    public async Task Wear_is_refused_as_unsupported()
    {
        var (h, ledger, ch) = await With10Rules();
        await using var _ = h;
        var item = await h.Give(ch);
        await h.W(tx => h.Ledger.Wear(tx, h.Hub, ch, h.Token(ch), [(item.Id, 1)], h.Req()));
        var e = await Assert.ThrowsAsync<HostRefusal>(() => h.W(tx => ledger.Wear(tx, h.Hub, ch, h.Token(ch), [(item.Id, 1)], h.Req())));
        Assert.Equal("unsupported", e.Reason);
    }

    [Fact]
    public async Task Equipping_is_refused_because_10_rules_have_no_equip_slots()
    {
        var (h, ledger, ch) = await With10Rules();
        await using var _ = h;
        var item = await h.Give(ch, slot: 0);
        // The positive arm: the same call is accepted by rules that have equip slots.
        await h.W(tx => h.Ledger.Move(tx, h.Hub, ch, h.Token(ch), item.Id, ItemContainer.Equipped, 0, h.Req()));
        var other = await h.Give(ch, slot: 1);
        var e = await Assert.ThrowsAsync<HostRefusal>(() => h.W(tx => ledger.Move(tx, h.Hub, ch, h.Token(ch), other.Id, ItemContainer.Equipped, 1, h.Req())));
        Assert.Equal("bad_slot", e.Reason);
    }

    [Fact]
    public async Task A_backpack_move_still_works()
    {
        var (h, ledger, ch) = await With10Rules();
        await using var _ = h;
        var item = await h.Give(ch, slot: 0);
        var moved = await h.W(tx => ledger.Move(tx, h.Hub, ch, h.Token(ch), item.Id, ItemContainer.Backpack, 4, h.Req()));
        Assert.Equal((ItemContainer.Backpack, 4), (moved.Container, moved.Slot));
    }

    [Fact]
    public async Task Consuming_a_whole_item_works()
    {
        var (h, ledger, ch) = await With10Rules();
        await using var _ = h;
        var item = await h.Give(ch);
        var after = await h.W(tx => ledger.Consume(tx, h.Hub, ch, h.Token(ch), item.Id, 1, h.Req()));
        Assert.Equal(ItemState.Consumed, after.State);
    }

    [Fact]
    public async Task Consuming_part_of_a_stack_is_refused_as_unsupported()
    {
        var (h, ledger, ch) = await With10Rules();
        await using var _ = h;
        var stack = await h.Give(ch, count: 3);
        // The positive arm: the same call is accepted by rules that re-encode stacks.
        await h.W(tx => h.Ledger.Consume(tx, h.Hub, ch, h.Token(ch), stack.Id, 1, h.Req()));
        var e = await Assert.ThrowsAsync<HostRefusal>(() => h.W(tx => ledger.Consume(tx, h.Hub, ch, h.Token(ch), stack.Id, 1, h.Req())));
        Assert.Equal("unsupported", e.Reason);
    }

    [Fact]
    public async Task A_container_gives_nothing()
    {
        var (h, ledger, ch) = await With10Rules();
        await using var _ = h;
        Assert.Empty(await h.W(tx => ledger.OpenContainer(tx, h.Hub, ch, h.Token(ch), 0, h.Req())));
    }
}
