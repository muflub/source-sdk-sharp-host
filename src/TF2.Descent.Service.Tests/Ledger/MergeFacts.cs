using TF2.Descent.Service.Ledger;
using Microsoft.Extensions.Options;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;
using SourceSharp.Host.Testing;

namespace TF2.Descent.Service.Tests.Ledger;

/// <summary>plan_sdk_2_8 §4: two stacks become one; the host keeps the count.</summary>
public class MergeFacts
{
    const string Potion = "health_small";

    /// <summary>A rules module whose merge makes one more than it was given.</summary>
    sealed class DuplicatingMerge(FakeGameRules f) : Contract11RulesFacts.Rules11(f), IGameRules
    {
        public ItemChange Merge(RolledItem source, RolledItem target) =>
            new([0], [target with { Count = source.Count + target.Count + 1 }], 0);
    }

    static async Task<(LedgerHarness H, string Ch)> On()
    {
        var h = await LedgerHarness.Create();
        var ch = await h.Character();
        await h.Lease(ch, h.Level);
        return (h, ch);
    }

    static Task<IReadOnlyList<ItemRecord>> Merge(LedgerHarness h, string ch, string from, string into, DescentLedger? ledger = null) =>
        h.W(tx => (ledger ?? h.Ledger).Merge(tx, h.Level, ch, h.Token(ch), from, into, h.Req()));

    static async Task<int> CountOf(LedgerHarness h, string itemId) => (await h.R(tx => tx.Items.Get(itemId)))!.Count;

    [Fact]
    public async Task Two_partial_stacks_become_one()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var from = await h.Give(ch, baseType: Potion, count: 2);
        var into = await h.Give(ch, baseType: Potion, count: 2);
        var after = await Merge(h, ch, from.Id, into.Id);
        Assert.Equal((4, ItemState.Live, ItemState.Consumed), (after[0].Count, after[0].State, after[1].State));
        Assert.Equal(4, h.Rules.Items.Decode(after[0].Instance, after[0].SchemaVersion).Count);
        await h.AssertBalanced();
    }

    [Fact]
    public async Task What_does_not_fit_stays_in_the_source_stack()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var from = await h.Give(ch, baseType: Potion, count: 3);
        var into = await h.Give(ch, baseType: Potion, count: 4);
        await Merge(h, ch, from.Id, into.Id);
        Assert.Equal((FakeGameRules.MaxStack, 2), (await CountOf(h, into.Id), await CountOf(h, from.Id)));
    }

    [Fact]
    public async Task Stacks_of_two_kinds_are_refused_by_the_rules()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var from = await h.Give(ch, baseType: Potion, count: 2);
        var into = await h.Give(ch, baseType: "mana_small", count: 2);
        var e = await Assert.ThrowsAsync<HostRefusal>(() => Merge(h, ch, from.Id, into.Id));
        Assert.Equal("not_same_kind", e.Reason);
        Assert.Equal((2, 2), (await CountOf(h, from.Id), await CountOf(h, into.Id)));
    }

    [Fact]
    public async Task A_merge_that_makes_more_than_it_was_given_is_refused()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var ledger = new DescentLedger(new SingleRulesProvider(new DuplicatingMerge(h.Rules)), Options.Create(h.Options));
        var from = await h.Give(ch, baseType: Potion, count: 2);
        var into = await h.Give(ch, baseType: Potion, count: 2);
        var e = await Assert.ThrowsAsync<HostRefusal>(() => Merge(h, ch, from.Id, into.Id, ledger));
        Assert.Equal("rules_bad_merge", e.Reason);
        Assert.Equal((2, 2), (await CountOf(h, from.Id), await CountOf(h, into.Id)));
    }

    [Fact]
    public async Task Rules_built_on_contract_11_refuse_as_unsupported()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var ledger = new DescentLedger(new SingleRulesProvider(new Contract11RulesFacts.Rules11(h.Rules)), Options.Create(h.Options));
        var from = await h.Give(ch, baseType: Potion, count: 2);
        var into = await h.Give(ch, baseType: Potion, count: 2);
        var e = await Assert.ThrowsAsync<HostRefusal>(() => Merge(h, ch, from.Id, into.Id, ledger));
        Assert.Equal("unsupported", e.Reason);
    }

    [Fact]
    public async Task Merging_a_stack_into_itself_is_refused()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var stack = await h.Give(ch, baseType: Potion, count: 2);
        var e = await Assert.ThrowsAsync<HostRefusal>(() => Merge(h, ch, stack.Id, stack.Id));
        Assert.Equal("bad_items", e.Reason);
    }

    [Fact]
    public async Task Merging_another_characters_stack_is_refused()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var mine = await h.Give(ch, baseType: Potion, count: 2);
        var other = await h.Character("Bo", account: "2");
        var theirs = await h.Give(other, baseType: Potion, count: 2);
        await Assert.ThrowsAsync<HostRefusal>(() => Merge(h, ch, theirs.Id, mine.Id));
        Assert.Equal(2, await CountOf(h, theirs.Id));
    }
}
