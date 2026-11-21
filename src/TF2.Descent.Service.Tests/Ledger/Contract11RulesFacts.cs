using TF2.Descent.Service.Ledger;
using Microsoft.Extensions.Options;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;
using SourceSharp.Host.Testing;

namespace TF2.Descent.Service.Tests.Ledger;

/// <summary>
/// A rules module built on Host.Contracts 1.1 implements none of 1.2's members; the defaults
/// must keep its 1.1 behavior (plan_sdk_2_8: every new member has a default body).
/// </summary>
public class Contract11RulesFacts
{
    /// <summary>Exactly the 1.1 surface, delegated to FakeGameRules; no 1.2 member is implemented.</summary>
    internal class Rules11(FakeGameRules f) : Contract10RulesFacts.Rules10(f), IGameRules
    {
        public IReadOnlyList<string> EquipSlots => Fake.EquipSlots;
        public int ContainerSlots(CharacterSheet sheet, ItemContainer container) => Fake.ContainerSlots(sheet, container);
        public string? PlaceRefusal(CharacterSheet sheet, RolledItem item, ItemContainer container, int slot) => Fake.PlaceRefusal(sheet, item, container, slot);
        public ItemChange Consume(CharacterSheet sheet, RolledItem item, int count) => Fake.Consume(sheet, item, count);
        public IReadOnlyList<StartingItem> StartingKit(string className, CharacterSheet sheet, ulong seed) => Fake.StartingKit(className, sheet, seed);
        public IReadOnlyList<RolledItem> ContainerDrops(ulong instanceSeed, uint containerSeq, int depth, CharacterSheet opener) =>
            Fake.ContainerDrops(instanceSeed, containerSeq, depth, opener);
        public ItemChange Repair(RolledItem item, RepairKind kind, RolledItem? kit, ulong seed) => Fake.Repair(item, kind, kit, seed);
        public ItemChange Wear(RolledItem item, int amount) => Fake.Wear(item, amount);
    }

    internal static async Task<(LedgerHarness H, DescentLedger Ledger, string Ch)> With11Rules(int level = 10)
    {
        var h = await LedgerHarness.Create();
        var ledger = new DescentLedger(new SingleRulesProvider(new Rules11(h.Rules)), Options.Create(h.Options));
        var ch = await h.Character(level: level);
        await h.Lease(ch, h.Hub);
        await h.W(tx => tx.Characters.AddAustralium(ch, 10, "test", null));
        return (h, ledger, ch);
    }

    [Fact]
    public async Task A_town_repair_by_a_hero_the_12_rules_would_waive_still_costs()
    {
        var (h, ledger, ch) = await With11Rules(level: FakeGameRules.TownFreeLevel);
        await using var _ = h;
        var item = await h.Give(ch);
        await h.W(tx => ledger.Repair(tx, h.Hub, ch, h.Token(ch), item.Id, RepairKind.Town, null, h.Req()));
        Assert.Equal(9, (await h.R(tx => tx.Characters.Get(ch)))!.Australium);
    }
}
