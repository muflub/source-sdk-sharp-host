using TF2.Descent.Service.Ledger;
using Microsoft.Extensions.Options;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;
using SourceSharp.Host.Testing;

namespace TF2.Descent.Service.Tests.Ledger;

/// <summary>
/// Ruling 2026-11-22: a respawning character always gets the class's starting kit, whatever they
/// carried. The death mints it on the ledger, in the same write that moves the carried items to the corpse.
/// </summary>
public class RespawnKitFacts
{
    static readonly (ItemContainer, int, string)[] FakeKit =
        [(ItemContainer.Belt, 0, "health_small"), (ItemContainer.Belt, 1, "health_small"), (ItemContainer.Belt, 2, "mana_small")];

    static Task<IReadOnlyList<ItemRecord>> Carried(LedgerHarness h, string characterId) =>
        h.R(tx => tx.Items.Query(new ItemQuery(OwnerKind: OwnerKind.Character, OwnerId: characterId)));

    static Task<IReadOnlyList<ItemRecord>> Corpse(LedgerHarness h, string characterId) =>
        h.R(tx => tx.Items.Query(new ItemQuery(OwnerKind: OwnerKind.Corpse, OwnerId: characterId)));

    static Task<IReadOnlyList<ItemRecord>> Die(LedgerHarness h, string characterId, DescentLedger? ledger = null) =>
        h.W(tx => (ledger ?? h.Ledger).RecordDeath(tx, h.Level, characterId, h.Token(characterId), h.Req()));

    static async Task<(LedgerHarness, string)> Leased(bool hardcore = false)
    {
        var h = await LedgerHarness.Create();
        var ch = await h.Character(hardcore: hardcore);
        await h.Lease(ch, h.Level);
        return (h, ch);
    }

    [Fact]
    public async Task A_softcore_death_mints_the_class_starting_kit_to_the_character()
    {
        var (h, ch) = await Leased();
        await using var _ = h;
        Assert.Empty(await Carried(h, ch)); // the harness character starts with nothing
        await h.Give(ch);
        await Die(h, ch);
        Assert.Equal(FakeKit, (await Carried(h, ch)).OrderBy(i => i.Slot).Select(i => (i.Container, i.Slot, i.BaseType)));
    }

    [Fact]
    public async Task The_death_result_is_the_corpse_and_not_the_kit()
    {
        var (h, ch) = await Leased();
        await using var _ = h;
        var gun = await h.Give(ch);
        var moved = await Die(h, ch);
        Assert.Equal([gun.Id], moved.Select(i => i.Id));
    }

    [Fact]
    public async Task A_hardcore_death_mints_no_kit()
    {
        var (h, ch) = await Leased(hardcore: true);
        await using var _ = h;
        await h.Give(ch);
        await Die(h, ch);
        Assert.Empty(await Carried(h, ch));
        Assert.Single(await Corpse(h, ch));
    }

    [Fact]
    public async Task The_old_kit_goes_to_the_corpse_and_a_new_one_is_minted()
    {
        var h = await LedgerHarness.Create();
        await using var _ = h;
        var created = await h.W(tx => h.Ledger.CreateCharacter(tx, h.Hub, "76561198000000001", "scout", "Ann", false));
        var first = (await Carried(h, created.Id)).Select(i => i.Id).Order().ToList();
        Assert.Equal(3, first.Count);
        await h.Lease(created.Id, h.Level);
        await Die(h, created.Id);
        Assert.Equal(first, (await Corpse(h, created.Id)).Select(i => i.Id).Order());
        var second = await Carried(h, created.Id);
        Assert.Equal(3, second.Count);
        Assert.Empty(second.Select(i => i.Id).Intersect(first));
    }

    [Fact]
    public async Task Each_death_mints_one_kit_and_the_ledger_balances()
    {
        var (h, ch) = await Leased();
        await using var _ = h;
        await h.Give(ch);
        await Die(h, ch);
        await h.AssertBalanced();
        await Die(h, ch);
        await h.AssertBalanced();
        // 1 given + 3 per death; nothing ended yet: the first kit and the gun lie on the corpse.
        Assert.Equal((7L, 0L, 7L), await h.Totals());
        Assert.Equal(4, (await Corpse(h, ch)).Count);
        Assert.Equal(2, (await h.R(tx => tx.Audit.List(new AuditQuery(Action: DescentLedger.DeathAction, Target: ch)))).Count);
    }

    [Fact]
    public async Task A_kit_its_own_rules_refuse_refuses_the_death_and_moves_nothing()
    {
        var (h, ch) = await Leased();
        await using var _ = h;
        var gun = await h.Give(ch);
        var ledger = new DescentLedger(new SingleRulesProvider(new StartingKitFacts.GunOnTheBelt(h.Rules)), Options.Create(h.Options));
        var e = await Assert.ThrowsAsync<HostRefusal>(() => Die(h, ch, ledger));
        Assert.Equal("rules_bad_kit", e.Reason);
        Assert.Equal([gun.Id], (await Carried(h, ch)).Select(i => i.Id));
        Assert.Equal((1L, 0L, 1L), await h.Totals());
    }

    [Fact]
    public async Task Rules_built_on_contract_10_respawn_with_nothing()
    {
        var (h, ch) = await Leased();
        await using var _ = h;
        await h.Give(ch);
        var ledger = new DescentLedger(new SingleRulesProvider(new Contract10RulesFacts.Rules10(h.Rules)), Options.Create(h.Options));
        await Die(h, ch, ledger);
        Assert.Empty(await Carried(h, ch));
    }
}
