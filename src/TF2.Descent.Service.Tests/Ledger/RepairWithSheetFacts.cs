using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;
using SourceSharp.Host.Testing;

namespace TF2.Descent.Service.Tests.Ledger;

/// <summary>plan_sdk_2_8 §2: the repair sees the repairer's sheet; a waived town price is the rules' Cost 0.</summary>
public class RepairWithSheetFacts
{
    static async Task<(LedgerHarness H, string Ch)> Hero(int level)
    {
        var h = await LedgerHarness.Create();
        var ch = await h.Character(level: level);
        await h.Lease(ch, h.Hub);
        await h.W(tx => tx.Characters.AddAustralium(ch, 10, "test", null));
        return (h, ch);
    }

    static async Task<int> FieldRepaired(LedgerHarness h, string ch, TF2.Descent.Service.Ledger.DescentLedger? ledger = null)
    {
        var item = await h.Give(ch);
        var kit = await h.Give(ch, baseType: FakeGameRules.RepairKit);
        await h.W(tx => h.Ledger.Wear(tx, h.Hub, ch, h.Token(ch), [(item.Id, 60)], h.Req()));
        await h.W(tx => (ledger ?? h.Ledger).Repair(tx, h.Hub, ch, h.Token(ch), item.Id, RepairKind.Field, kit.Id, h.Req()));
        var row = (await h.R(tx => tx.Items.Get(item.Id)))!;
        return FakeGameRules.ItemCodec.DurabilityOf(h.Rules.Items.Decode(row.Instance, row.SchemaVersion)).Durability;
    }

    [Fact]
    public async Task A_field_repair_restores_more_for_a_higher_level_hero()
    {
        // Worn to 40 of 100, item level 15: the kit alone restores 15..29; a level-40 hero adds 40 (to the lowered max, 96).
        var (h, ch) = await Hero(level: 40);
        await using var _ = h;
        Assert.True(await FieldRepaired(h, ch) >= 95);
    }

    [Fact]
    public async Task Rules_built_on_contract_11_repair_by_the_kit_alone()
    {
        var (h, ledger, ch) = await Contract11RulesFacts.With11Rules(level: 40);
        await using var _ = h;
        Assert.True(await FieldRepaired(h, ch, ledger) <= 69);
    }

    [Fact]
    public async Task A_town_repair_the_rules_waive_costs_nothing()
    {
        var (h, ch) = await Hero(level: FakeGameRules.TownFreeLevel);
        await using var _ = h;
        var item = await h.Give(ch);
        await h.W(tx => h.Ledger.Repair(tx, h.Hub, ch, h.Token(ch), item.Id, RepairKind.Town, null, h.Req()));
        Assert.Equal(10, (await h.R(tx => tx.Characters.Get(ch)))!.Australium);
    }

    [Fact]
    public async Task A_town_repair_below_the_waiver_still_costs()
    {
        var (h, ch) = await Hero(level: FakeGameRules.TownFreeLevel - 1);
        await using var _ = h;
        var item = await h.Give(ch);
        await h.W(tx => h.Ledger.Repair(tx, h.Hub, ch, h.Token(ch), item.Id, RepairKind.Town, null, h.Req()));
        Assert.Equal(9, (await h.R(tx => tx.Characters.Get(ch)))!.Australium);
    }
}
