using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;
using SourceSharp.Host.Testing;

namespace TF2.Descent.Service.Tests.Ledger;

/// <summary>plan_sdk_2_7 §3c: a world container is replayed from the instance seed, once per opener.</summary>
public class OpenContainerFacts
{
    static async Task<(LedgerHarness H, string Ch)> On()
    {
        var h = await LedgerHarness.Create();
        var ch = await h.Character();
        await h.Lease(ch, h.Level);
        return (h, ch);
    }

    static Task<IReadOnlyList<ItemRecord>> Open(LedgerHarness h, string ch, uint seq) =>
        h.W(tx => h.Ledger.OpenContainer(tx, h.Level, ch, h.Token(ch), seq, h.Req()));

    static async Task<CharacterSheet> SheetOf(LedgerHarness h, string ch)
    {
        var c = (await h.R(tx => tx.Characters.Get(ch)))!;
        return new CharacterSheet(c.Sheet, c.SheetVersion);
    }

    /// <summary>The first container on the level whose roll for <paramref name="ch"/> is (or is not) a drop.</summary>
    static async Task<uint> Container(LedgerHarness h, string ch, bool drops)
    {
        var sheet = await SheetOf(h, ch);
        return Enumerable.Range(0, 1000).Select(i => (uint)i)
            .First(s => h.Rules.ContainerDrops(h.Level.Seed, s, h.Level.Depth, sheet).Count > 0 == drops);
    }

    [Fact]
    public async Task A_container_that_rolls_a_potion_puts_it_on_the_floor_for_the_opener()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var item = Assert.Single(await Open(h, ch, await Container(h, ch, drops: true)));
        Assert.Equal((OwnerKind.World, h.Level.Id, ch, "health_small"), (item.OwnerKind, item.InstanceId, item.ForCharacter, item.BaseType));
    }

    [Fact]
    public async Task The_opener_can_pick_it_up()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var item = Assert.Single(await Open(h, ch, await Container(h, ch, drops: true)));
        var claimed = await h.W(tx => h.Ledger.Claim(tx, h.Level, ch, h.Token(ch), item.Id, h.Req()));
        Assert.Equal((OwnerKind.Character, ch), (claimed.OwnerKind, claimed.OwnerId));
    }

    [Fact]
    public async Task A_container_opened_twice_by_the_same_character_is_refused()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var seq = await Container(h, ch, drops: true);
        await Open(h, ch, seq);
        var e = await Assert.ThrowsAsync<HostRefusal>(() => Open(h, ch, seq));
        Assert.Equal("already_opened", e.Reason);
        Assert.Equal(1, (await h.Totals()).Minted);
    }

    [Fact]
    public async Task An_empty_container_mints_nothing_and_still_counts_as_opened()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var seq = await Container(h, ch, drops: false);
        Assert.Empty(await Open(h, ch, seq));
        var e = await Assert.ThrowsAsync<HostRefusal>(() => Open(h, ch, seq));
        Assert.Equal("already_opened", e.Reason);
    }

    [Fact]
    public async Task Each_player_opens_the_same_container_for_themselves()
    {
        var (h, ann) = await On();
        await using var _ = h;
        var bo = await h.Character("Bo", account: "2");
        await h.Lease(bo, h.Level);
        var seq = await Container(h, ann, drops: true);
        var forAnn = Assert.Single(await Open(h, ann, seq));
        var forBo = await h.W(tx => h.Ledger.OpenContainer(tx, h.Level, bo, h.Token(bo), seq, h.Req()));
        Assert.Equal(ann, forAnn.ForCharacter);
        Assert.All(forBo, i => Assert.Equal(bo, i.ForCharacter));
    }

    [Fact]
    public async Task The_same_container_on_another_instance_is_another_container()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var seq = await Container(h, ch, drops: true);
        await Open(h, ch, seq);
        var other = await h.AddInstance("lvl-other", InstanceKind.Level, h.Level.Depth);
        await h.Lease(ch, other);
        // A different seed rolls differently; the point is that it is not refused as already opened.
        await h.W(tx => h.Ledger.OpenContainer(tx, other, ch, h.Token(ch), seq, h.Req()));
    }

    [Fact]
    public async Task Opening_needs_the_lease_on_this_instance()
    {
        var (h, ch) = await On();
        await using var _ = h;
        var seq = await Container(h, ch, drops: true);
        await Assert.ThrowsAsync<HostRefusal>(() => h.W(tx => h.Ledger.OpenContainer(tx, h.Hub, ch, h.Token(ch), seq, h.Req())));
        Assert.Equal(0, (await h.Totals()).Minted);
    }
}
