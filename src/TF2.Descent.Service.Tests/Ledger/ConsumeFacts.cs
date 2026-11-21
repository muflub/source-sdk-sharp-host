using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;
using SourceSharp.Host.Testing;
using TF2.Descent.Service.Ledger;

namespace TF2.Descent.Service.Tests.Ledger;

/// <summary>plan_sdk_2_7 §1: Consume ends an item or lowers its stack, on any instance kind.</summary>
public class ConsumeFacts
{
    const string Potion = "health_small";

    static async Task<(LedgerHarness H, string Ch)> On()
    {
        var h = await LedgerHarness.Create();
        var ch = await h.Character();
        await h.Lease(ch, h.Level);
        return (h, ch);
    }

    static Task<ItemRecord> Consume(LedgerHarness h, string ch, string itemId, int count = 1) =>
        h.W(tx => h.Ledger.Consume(tx, h.Level, ch, h.Token(ch), itemId, count, h.Req()));

    [Fact]
    public async Task Consuming_a_single_item_ends_it()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var potion = await h.Give(ch, baseType: Potion);
        var after = await Consume(h, ch, potion.Id);
        Assert.Equal(ItemState.Consumed, after.State);
        Assert.Equal(ItemState.Consumed, (await h.R(tx => tx.Items.Get(potion.Id)))!.State);
        await h.AssertBalanced();
    }

    [Fact]
    public async Task Consuming_part_of_a_stack_lowers_its_count_in_place()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var stack = await h.Give(ch, slot: 4, baseType: Potion, count: 5);
        var after = await Consume(h, ch, stack.Id, 2);
        Assert.Equal((ItemState.Live, 3, 4), (after.State, after.Count, after.Slot));
        Assert.Equal(3, h.Rules.Items.Decode(after.Instance, after.SchemaVersion).Count);
    }

    [Fact]
    public async Task Consuming_the_rest_of_a_stack_ends_it()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var stack = await h.Give(ch, baseType: Potion, count: 2);
        Assert.Equal(ItemState.Consumed, (await Consume(h, ch, stack.Id, 2)).State);
    }

    [Fact]
    public async Task Consuming_more_than_the_stack_holds_is_refused()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var stack = await h.Give(ch, baseType: Potion, count: 2);
        var e = await Assert.ThrowsAsync<HostRefusal>(() => Consume(h, ch, stack.Id, 3));
        Assert.Equal("bad_count", e.Reason);
        Assert.Equal(2, (await h.R(tx => tx.Items.Get(stack.Id)))!.Count);
    }

    [Fact]
    public async Task Consuming_zero_is_refused()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var potion = await h.Give(ch, baseType: Potion);
        var e = await Assert.ThrowsAsync<HostRefusal>(() => Consume(h, ch, potion.Id, 0));
        Assert.Equal("bad_count", e.Reason);
    }

    [Fact]
    public async Task Consuming_another_characters_item_is_refused()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var other = await h.Character("Bo", account: "2");
        var theirs = await h.Give(other, baseType: Potion);
        await Assert.ThrowsAsync<HostRefusal>(() => Consume(h, ch, theirs.Id));
        Assert.Equal(ItemState.Live, (await h.R(tx => tx.Items.Get(theirs.Id)))!.State);
    }

    [Fact]
    public async Task A_belt_potion_is_consumed_where_it_sits()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var stack = await h.Give(ch, baseType: Potion, count: 3);
        await h.W(tx => h.Ledger.Move(tx, h.Level, ch, h.Token(ch), stack.Id, ItemContainer.Belt, 1, h.Req()));
        var after = await Consume(h, ch, stack.Id);
        Assert.Equal((ItemContainer.Belt, 1, 2), (after.Container, after.Slot, after.Count));
    }

    [Fact]
    public async Task Consume_runs_in_the_hub()
    {
        var h = await LedgerHarness.Create();
        await using var _ = h;
        var ch = await h.Character();
        await h.Lease(ch, h.Hub);
        var potion = await h.Give(ch, baseType: Potion);
        var after = await h.W(tx => h.Ledger.Consume(tx, h.Hub, ch, h.Token(ch), potion.Id, 1, h.Req()));
        Assert.Equal(ItemState.Consumed, after.State);
    }

    [Fact]
    public async Task Consume_is_audited_with_its_count()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var stack = await h.Give(ch, baseType: Potion, count: 4);
        await Consume(h, ch, stack.Id, 3);
        var entry = Assert.Single(await h.R(tx => tx.Audit.List(new AuditQuery(Action: DescentLedger.ConsumeAction, Target: stack.Id))));
        Assert.Contains("\"count\":3", entry.AfterJson);
    }
}
