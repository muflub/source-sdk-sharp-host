using TF2.Descent.Service.Ledger;
using Microsoft.Extensions.Options;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;
using SourceSharp.Host.Testing;

namespace TF2.Descent.Service.Tests.Ledger;

/// <summary>plan_sdk_2_8 §3: a breakable is replayed per hero, once each; Australium to them, items for them.</summary>
public class BreakFacts
{
    static async Task<(LedgerHarness H, string Ch)> On()
    {
        var h = await LedgerHarness.Create();
        var ch = await h.Character();
        await h.Lease(ch, h.Level);
        return (h, ch);
    }

    static Task<(long Australium, IReadOnlyList<ItemRecord> Items)> Break(LedgerHarness h, string ch, uint seq, DescentLedger? ledger = null) =>
        h.W(tx => (ledger ?? h.Ledger).Break(tx, h.Level, ch, h.Token(ch), seq, h.Req()));

    static async Task<CharacterSheet> SheetOf(LedgerHarness h, string ch)
    {
        var c = (await h.R(tx => tx.Characters.Get(ch)))!;
        return new CharacterSheet(c.Sheet, c.SheetVersion);
    }

    /// <summary>The first breakable on the level whose roll for <paramref name="ch"/> matches.</summary>
    static async Task<uint> Breakable(LedgerHarness h, string ch, Func<BreakableRoll, bool> kind)
    {
        var sheet = await SheetOf(h, ch);
        return Enumerable.Range(0, 1000).Select(i => (uint)i).First(s => kind(h.Rules.BreakableDrops(h.Level.Seed, s, h.Level.Depth, sheet)));
    }

    static async Task<long> Wallet(LedgerHarness h, string ch) => (await h.R(tx => tx.Characters.Get(ch)))!.Australium;

    [Fact]
    public async Task Australium_from_a_breakable_is_credited_to_the_breaker()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var seq = await Breakable(h, ch, r => r.Australium > 0);
        var (australium, items) = await Break(h, ch, seq);
        Assert.True(australium > 0);
        Assert.Empty(items);
        Assert.Equal(australium, await Wallet(h, ch));
    }

    [Fact]
    public async Task Items_from_a_breakable_land_on_the_floor_for_the_breaker()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var seq = await Breakable(h, ch, r => r.Items.Count > 0);
        var item = Assert.Single((await Break(h, ch, seq)).Items);
        Assert.Equal((OwnerKind.World, h.Level.Id, ch), (item.OwnerKind, item.InstanceId, item.ForCharacter));
    }

    [Fact]
    public async Task A_breakable_broken_twice_by_one_hero_is_refused_and_pays_once()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var seq = await Breakable(h, ch, r => r.Australium > 0);
        var (paid, _) = await Break(h, ch, seq);
        var e = await Assert.ThrowsAsync<HostRefusal>(() => Break(h, ch, seq));
        Assert.Equal("already_broken", e.Reason);
        Assert.Equal(paid, await Wallet(h, ch));
    }

    [Fact]
    public async Task An_empty_roll_still_counts_as_broken()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var seq = await Breakable(h, ch, r => r.Australium == 0 && r.Items.Count == 0);
        var (australium, items) = await Break(h, ch, seq);
        Assert.Equal((0L, 0), (australium, items.Count));
        Assert.Equal("already_broken", (await Assert.ThrowsAsync<HostRefusal>(() => Break(h, ch, seq))).Reason);
    }

    [Fact]
    public async Task Every_hero_in_the_room_breaks_it_for_themselves()
    {
        var (h, ann) = await On();
        await using var _ = h;
        var bo = await h.Character("Bo", account: "2", level: 20);
        await h.Lease(bo, h.Level);
        var seq = await Breakable(h, ann, r => r.Australium > 0);
        var (forAnn, _) = await Break(h, ann, seq);
        var (forBo, _) = await Break(h, bo, seq);
        // The kind is the breakable's (both Australium); the share is each hero's own (depth + level).
        Assert.Equal((h.Level.Depth + 10L, h.Level.Depth + 20L), (forAnn, forBo));
        Assert.Equal((forAnn, forBo), (await Wallet(h, ann), await Wallet(h, bo)));
        Assert.Equal("already_broken", (await Assert.ThrowsAsync<HostRefusal>(() => Break(h, bo, seq))).Reason);
    }

    [Fact]
    public async Task Breakables_and_containers_are_numbered_apart()
    {
        var (h, ch) = await On();
        await using var _ = h;
        await h.W(tx => h.Ledger.OpenContainer(tx, h.Level, ch, h.Token(ch), 3, h.Req()));
        await Break(h, ch, 3);
    }

    [Fact]
    public async Task Rules_built_on_contract_11_break_for_nothing()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var ledger = new DescentLedger(new SingleRulesProvider(new Contract11RulesFacts.Rules11(h.Rules)), Options.Create(h.Options));
        var seq = await Breakable(h, ch, r => r.Australium > 0);
        Assert.Equal((0L, 0), ((await Break(h, ch, seq, ledger)).Australium, (await h.R(tx => tx.Items.Count(new ItemQuery(OwnerKind: OwnerKind.World))))));
    }
}
