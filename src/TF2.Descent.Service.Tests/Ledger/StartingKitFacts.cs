using TF2.Descent.Service.Ledger;
using Microsoft.Extensions.Options;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;
using SourceSharp.Host.Testing;

namespace TF2.Descent.Service.Tests.Ledger;

/// <summary>plan_sdk_2_7 §1: a new character is minted its rules' starting kit, in the creating transaction.</summary>
public class StartingKitFacts
{
    /// <summary>FakeGameRules' containers with a kit the rules themselves would refuse (a gun on the belt).</summary>
    internal sealed class GunOnTheBelt(FakeGameRules f) : Contract10RulesFacts.Rules10(f), IGameRules
    {
        public int ContainerSlots(CharacterSheet sheet, ItemContainer container) => Fake.ContainerSlots(sheet, container);
        public string? PlaceRefusal(CharacterSheet sheet, RolledItem item, ItemContainer container, int slot) => Fake.PlaceRefusal(sheet, item, container, slot);
        public IReadOnlyList<StartingItem> StartingKit(string className, CharacterSheet sheet, ulong seed) =>
            [new(Fake.RollItem(new ItemRollContext(className, 1, 0, 1, 0, "kit", FakeGameRules.NamedBase + "scattergun"), seed), ItemContainer.Belt, 0)];
    }

    static Task<CharacterRecord> Create(LedgerHarness h, DescentLedger? ledger = null) =>
        h.W(tx => (ledger ?? h.Ledger).CreateCharacter(tx, h.Hub, "76561198000000001", "scout", "Ann", false));

    static Task<IReadOnlyList<ItemRecord>> Carried(LedgerHarness h, string characterId) =>
        h.R(tx => tx.Items.Query(new ItemQuery(OwnerKind: OwnerKind.Character, OwnerId: characterId)));

    [Fact]
    public async Task A_new_character_starts_with_potions_on_the_belt()
    {
        var h = await LedgerHarness.Create();
        await using var _ = h;
        var ch = await Create(h);
        var kit = (await Carried(h, ch.Id)).OrderBy(i => i.Slot).Select(i => (i.Container, i.Slot, i.BaseType));
        Assert.Equal(
            [(ItemContainer.Belt, 0, "health_small"), (ItemContainer.Belt, 1, "health_small"), (ItemContainer.Belt, 2, "mana_small")],
            kit);
    }

    [Fact]
    public async Task The_kit_replays_at_the_first_checkpoint()
    {
        var h = await LedgerHarness.Create();
        await using var _ = h;
        var ch = await Create(h);
        await h.Lease(ch.Id, h.Hub);
        var (after, _) = await h.W(tx => h.Ledger.Checkpoint(tx, h.Hub, ch.Id, h.Token(ch.Id), ch.Sheet, ch.SheetVersion, [], 1, ch.Version));
        Assert.True(after.Version > ch.Version);
    }

    [Fact]
    public async Task The_kit_takes_no_backpack_cell()
    {
        var h = await LedgerHarness.Create();
        await using var _ = h;
        var ch = await Create(h);
        var item = await h.Give(ch.Id);
        Assert.Equal(0, item.Slot);
    }

    [Fact]
    public async Task A_kit_its_own_rules_refuse_refuses_the_creation()
    {
        var h = await LedgerHarness.Create();
        await using var _ = h;
        var ledger = new DescentLedger(new SingleRulesProvider(new GunOnTheBelt(h.Rules)), Options.Create(h.Options));
        var e = await Assert.ThrowsAsync<HostRefusal>(() => Create(h, ledger));
        Assert.Equal("rules_bad_kit", e.Reason);
        Assert.Empty(await h.R(tx => tx.Characters.List("76561198000000001")));
        Assert.Equal(0, (await h.Totals()).Minted);
    }

    [Fact]
    public async Task Rules_built_on_contract_10_start_with_nothing()
    {
        var h = await LedgerHarness.Create();
        await using var _ = h;
        var ledger = new DescentLedger(new SingleRulesProvider(new Contract10RulesFacts.Rules10(h.Rules)), Options.Create(h.Options));
        var ch = await Create(h, ledger);
        Assert.Empty(await Carried(h, ch.Id));
    }
}
