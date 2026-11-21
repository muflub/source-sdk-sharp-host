using TF2.Descent.Service.Ledger;
using Microsoft.Extensions.DependencyInjection;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;

namespace SourceSharp.Host.Sdk.Tests;

/// <summary>IItems' typed calls, each once, against the real ledger.</summary>
public class ItemFacts
{
    const string Account = "76561198000000001";

    sealed record World(SdkHarness H, HostSdk Hub, HostSdk Level, HostCharacter Character);

    static async Task<World> Start(SdkHarness h)
    {
        await h.Instance("hub-1", InstanceKind.Hub);
        await h.Instance("lvl-1", InstanceKind.Level, 3);
        var hub = h.Sdk("hub-1");
        var c = (await hub.Pumped(hub.Characters.Create(Account, "scout", "Ann"))).Value;
        await h.Data.WriteAsync((tx, _) => tx.Characters.AddAustralium(c.Id, 100_000, "test", null));
        return new World(h, hub, h.Sdk("lvl-1"), c);
    }

    static async Task<ICharacterLease> Lease(HostSdk sdk, string id) => (await sdk.Pumped(sdk.Characters.Lease(id))).Value;

    /// <summary>An item minted straight into the ledger (as a test fixture; the SDK never mints).</summary>
    static Task<ItemRecord> Mint(SdkHarness h, OwnerKind owner, string ownerId, string? instance, int slot, int tier = 0, bool identified = true)
    {
        var r = h.Rules.RollItem(new ItemRollContext("scout", 1, 1, 5, tier, "depth1", "test"), (ulong)Random.Shared.NextInt64());
        return h.Data.WriteAsync((tx, _) => tx.Items.Mint(new NewItem(r.Seed, r.BaseType, r.Rarity, r.ItemLevel, 1, identified,
            FakeGameRules_Encode(r with { Identified = identified }), 1, tier, 1), owner, ownerId, instance, slot, "test", null));
    }

    static byte[] FakeGameRules_Encode(RolledItem r) => SourceSharp.Host.Testing.FakeGameRules.ItemCodec.Encode(r);

    static Task<ItemRecord?> Row(SdkHarness h, string id) => h.Data.ReadAsync((tx, _) => tx.Items.Get(id));

    [Fact]
    public async Task Claim_takes_a_world_item_into_the_backpack()
    {
        await using var h = await SdkHarness.Start();
        var w = await Start(h);
        var lease = await Lease(w.Level, w.Character.Id);
        var item = await Mint(h, OwnerKind.World, "lvl-1", "lvl-1", -1);
        var r = await w.Level.Pumped(w.Level.Items.Claim(lease, item.Id));
        Assert.Equal((HostOwnerKind.Character, w.Character.Id), (r.Value.Owner, r.Value.OwnerId));
    }

    [Fact]
    public async Task Drop_puts_a_backpack_item_into_the_world()
    {
        await using var h = await SdkHarness.Start();
        var w = await Start(h);
        var lease = await Lease(w.Level, w.Character.Id);
        var item = await Mint(h, OwnerKind.Character, w.Character.Id, null, 0);
        var r = await w.Level.Pumped(w.Level.Items.Drop(lease, item.Id, new System.Numerics.Vector3(1, 2, 3)));
        Assert.Equal((HostOwnerKind.World, "lvl-1"), (r.Value.Owner, r.Value.InstanceId));
    }

    [Fact]
    public async Task Move_changes_the_slot()
    {
        await using var h = await SdkHarness.Start();
        var w = await Start(h);
        var lease = await Lease(w.Level, w.Character.Id);
        var item = await Mint(h, OwnerKind.Character, w.Character.Id, null, 0);
        var r = await w.Level.Pumped(w.Level.Items.Move(lease, item.Id, 5));
        Assert.Equal(5, r.Value.Slot);
    }

    [Fact]
    public async Task Move_into_an_equip_slot_reports_the_container()
    {
        await using var h = await SdkHarness.Start();
        var w = await Start(h);
        var lease = await Lease(w.Level, w.Character.Id);
        var item = await Mint(h, OwnerKind.Character, w.Character.Id, null, 0);
        var r = await w.Level.Pumped(w.Level.Items.Move(lease, item.Id, HostContainer.Equipped, 2));
        Assert.Equal((HostContainer.Equipped, 2), (r.Value.Container, r.Value.Slot));
    }

    [Fact]
    public async Task Move_refused_by_the_rules_carries_their_reason()
    {
        await using var h = await SdkHarness.Start();
        var w = await Start(h);
        var lease = await Lease(w.Level, w.Character.Id);
        var item = await Mint(h, OwnerKind.Character, w.Character.Id, null, 0);
        var r = await w.Level.Pumped(w.Level.Items.Move(lease, item.Id, HostContainer.Belt, 3)); // 0-2 hold the starting kit
        Assert.Equal("potions_only", r.Refusal!.Reason);
    }

    [Fact]
    public async Task StashMove_puts_an_item_in_the_shared_stash_and_GetStash_lists_it()
    {
        await using var h = await SdkHarness.Start();
        var w = await Start(h);
        var lease = await Lease(w.Hub, w.Character.Id);
        var item = await Mint(h, OwnerKind.Character, w.Character.Id, null, 0);
        var moved = await w.Hub.Pumped(w.Hub.Items.StashMove(lease, item.Id, toStash: true));
        Assert.Equal(HostOwnerKind.Stash, moved.Value.Owner);
        var stash = await w.Hub.Pumped(w.Hub.Items.GetStash(Account, hardcore: false));
        Assert.Equal([item.Id], stash.Value.Select(i => i.Id));
    }

    [Fact]
    public async Task GetBackpack_lists_the_characters_items()
    {
        await using var h = await SdkHarness.Start();
        var w = await Start(h);
        var item = await Mint(h, OwnerKind.Character, w.Character.Id, null, 0);
        var r = await w.Hub.Pumped(w.Hub.Items.GetBackpack(w.Character.Id));
        // Everything carried comes back; the starting kit is on the belt.
        Assert.Equal([item.Id], r.Value.Where(i => i.Container == HostContainer.Backpack).Select(i => i.Id));
        Assert.Equal(3, r.Value.Count(i => i.Container == HostContainer.Belt));
    }

    [Fact]
    public async Task ListWorldItems_lists_this_instances_world()
    {
        await using var h = await SdkHarness.Start();
        var w = await Start(h);
        var item = await Mint(h, OwnerKind.World, "lvl-1", "lvl-1", -1);
        var r = await w.Level.Pumped(w.Level.Items.ListWorldItems());
        Assert.Equal([item.Id], r.Value.Select(i => i.Id));
    }

    async Task<(HostItem Stock, ICharacterLease Lease)> VendorSetup(World w)
    {
        var hubRow = (await w.H.Data.ReadAsync((tx, _) => tx.Instances.Get("hub-1")))!;
        var ledger = w.H.App.Services.GetRequiredService<DescentLedger>();
        await w.H.Data.WriteAsync((tx, _) => ledger.RollVendor(tx, hubRow, "smith"));
        var stock = await w.Hub.Pumped(w.Hub.Items.GetVendorStock("smith"));
        return (stock.Value[0], await Lease(w.Hub, w.Character.Id));
    }

    [Fact]
    public async Task GetVendorStock_lists_the_rolled_stock()
    {
        await using var h = await SdkHarness.Start();
        var w = await Start(h);
        var (stock, _) = await VendorSetup(w);
        Assert.Equal(HostOwnerKind.Vendor, stock.Owner);
    }

    [Fact]
    public async Task Buy_moves_vendor_stock_into_the_backpack()
    {
        await using var h = await SdkHarness.Start();
        var w = await Start(h);
        var (stock, lease) = await VendorSetup(w);
        var r = await w.Hub.Pumped(w.Hub.Items.Buy(lease, stock.Id, "smith"));
        Assert.Equal((stock.Id, HostOwnerKind.Character), (r.Value.Id, r.Value.Owner));
    }

    [Fact]
    public async Task Sell_pays_and_BuyBack_returns_the_item()
    {
        await using var h = await SdkHarness.Start();
        var w = await Start(h);
        var (_, lease) = await VendorSetup(w);
        var item = await Mint(h, OwnerKind.Character, w.Character.Id, null, 0);
        var sold = await w.Hub.Pumped(w.Hub.Items.Sell(lease, item.Id, "smith"));
        Assert.True(sold.Value > 100_000);
        var back = await w.Hub.Pumped(w.Hub.Items.BuyBack(lease, item.Id, "smith"));
        Assert.Equal(HostOwnerKind.Character, back.Value.Owner);
    }

    [Fact]
    public async Task OpenCrate_consumes_the_crate_and_returns_its_content()
    {
        await using var h = await SdkHarness.Start();
        var w = await Start(h);
        var lease = await Lease(w.Hub, w.Character.Id);
        var crate = await Mint(h, OwnerKind.Character, w.Character.Id, null, 0);
        var r = await w.Hub.Pumped(w.Hub.Items.OpenCrate(lease, crate.Id));
        Assert.NotEqual(crate.Id, r.Value.Id);
        Assert.Equal(ItemState.Consumed, (await Row(h, crate.Id))!.State);
    }

    [Fact]
    public async Task Craft_combines_three_items_into_one()
    {
        await using var h = await SdkHarness.Start();
        var w = await Start(h);
        var lease = await Lease(w.Hub, w.Character.Id);
        var inputs = new List<string>();
        for (var s = 0; s < 3; s++) inputs.Add((await Mint(h, OwnerKind.Character, w.Character.Id, null, s)).Id);
        var r = await w.Hub.Pumped(w.Hub.Items.Craft(lease, "combine3", inputs));
        Assert.Equal(HostOwnerKind.Character, r.Value.Owner);
        Assert.DoesNotContain(r.Value.Id, inputs);
    }

    [Fact]
    public async Task Identify_identifies_an_unidentified_item()
    {
        await using var h = await SdkHarness.Start();
        var w = await Start(h);
        var lease = await Lease(w.Hub, w.Character.Id);
        var item = await Mint(h, OwnerKind.Character, w.Character.Id, null, 0, tier: 2, identified: false);
        var r = await w.Hub.Pumped(w.Hub.Items.Identify(lease, item.Id));
        Assert.True(r.Value.Identified);
    }

    [Fact]
    public async Task Repair_keeps_the_item()
    {
        await using var h = await SdkHarness.Start();
        var w = await Start(h);
        var lease = await Lease(w.Hub, w.Character.Id);
        var item = await Mint(h, OwnerKind.Character, w.Character.Id, null, 0);
        var r = await w.Hub.Pumped(w.Hub.Items.Repair(lease, item.Id));
        Assert.Equal(item.Id, r.Value.Id);
    }

    [Fact]
    public async Task A_field_repair_keeps_the_item_and_consumes_the_kit()
    {
        await using var h = await SdkHarness.Start();
        var w = await Start(h);
        var lease = await Lease(w.Level, w.Character.Id);
        var item = await Mint(h, OwnerKind.Character, w.Character.Id, null, 0);
        var kit = await MintKit(h, w.Character.Id, 1);
        var r = await w.Level.Pumped(w.Level.Items.Repair(lease, item.Id, HostRepairKind.Field, kit.Id));
        Assert.True(r.Ok, r.ToString());
        Assert.Equal(item.Id, r.Value.Id);
        Assert.Equal(ItemState.Consumed, (await Row(h, kit.Id))!.State);
    }

    [Fact]
    public async Task A_town_repair_on_a_level_is_refused()
    {
        await using var h = await SdkHarness.Start();
        var w = await Start(h);
        var lease = await Lease(w.Level, w.Character.Id);
        var item = await Mint(h, OwnerKind.Character, w.Character.Id, null, 0);
        var r = await w.Level.Pumped(w.Level.Items.Repair(lease, item.Id));
        Assert.Equal("not_a_hub", r.Refusal!.Reason);
    }

    [Fact]
    public async Task Wear_lowers_each_items_durability()
    {
        await using var h = await SdkHarness.Start();
        var w = await Start(h);
        var lease = await Lease(w.Level, w.Character.Id);
        var a = await Mint(h, OwnerKind.Character, w.Character.Id, null, 0);
        var b = await Mint(h, OwnerKind.Character, w.Character.Id, null, 1);
        var r = await w.Level.Pumped(w.Level.Items.Wear(lease, [new HostWear(a.Id, 10), new HostWear(b.Id, 20)]));
        Assert.True(r.Ok, r.ToString());
        var durability = r.Value.Select(i => SourceSharp.Host.Testing.FakeGameRules.ItemCodec.DurabilityOf(
            h.Rules.Items.Decode(i.Instance.Data, (int)i.Instance.SchemaVersion)).Durability).ToList();
        Assert.Equal([90, 80], durability);
    }

    [Fact]
    public async Task OpenContainer_mints_the_replayed_roll_to_the_floor_once()
    {
        await using var h = await SdkHarness.Start();
        var w = await Start(h);
        var lease = await Lease(w.Level, w.Character.Id);
        var level = (await h.Data.ReadAsync((tx, _) => tx.Instances.Get("lvl-1")))!;
        var c = (await h.Data.ReadAsync((tx, _) => tx.Characters.Get(w.Character.Id)))!;
        var seq = Enumerable.Range(0, 1000).Select(i => (uint)i)
            .First(s => h.Rules.ContainerDrops(level.Seed, s, level.Depth, new CharacterSheet(c.Sheet, c.SheetVersion)).Count > 0);
        var r = await w.Level.Pumped(w.Level.Items.OpenContainer(lease, seq));
        Assert.Equal((HostOwnerKind.World, w.Character.Id), (Assert.Single(r.Value).Owner, r.Value[0].ForCharacter));
        var again = await w.Level.Pumped(w.Level.Items.OpenContainer(lease, seq));
        Assert.Equal("already_opened", again.Refusal!.Reason);
    }

    [Fact]
    public async Task Swap_exchanges_two_items_places()
    {
        await using var h = await SdkHarness.Start();
        var w = await Start(h);
        var lease = await Lease(w.Level, w.Character.Id);
        var a = await Mint(h, OwnerKind.Character, w.Character.Id, null, 0);
        var b = await Mint(h, OwnerKind.Character, w.Character.Id, null, 1);
        await w.Level.Pumped(w.Level.Items.Move(lease, a.Id, HostContainer.Equipped, 2));
        var r = await w.Level.Pumped(w.Level.Items.Swap(lease, a.Id, b.Id));
        Assert.Equal([(HostContainer.Backpack, 1), (HostContainer.Equipped, 2)], r.Value.Select(i => (i.Container, i.Slot)));
    }

    [Fact]
    public async Task Merge_puts_two_stacks_into_one()
    {
        await using var h = await SdkHarness.Start();
        var w = await Start(h);
        var lease = await Lease(w.Level, w.Character.Id);
        var from = await MintStack(h, w.Character.Id, 2);
        var into = await MintStack(h, w.Character.Id, 1);
        var r = await w.Level.Pumped(w.Level.Items.Merge(lease, from.Id, into.Id));
        Assert.Equal((3, HostItemState.Consumed), (r.Value[0].Count, r.Value[1].State));
    }

    [Fact]
    public async Task Break_pays_the_hero_their_replayed_roll_once()
    {
        await using var h = await SdkHarness.Start();
        var w = await Start(h);
        var lease = await Lease(w.Level, w.Character.Id);
        var level = (await h.Data.ReadAsync((tx, _) => tx.Instances.Get("lvl-1")))!;
        var c = (await h.Data.ReadAsync((tx, _) => tx.Characters.Get(w.Character.Id)))!;
        var sheet = new CharacterSheet(c.Sheet, c.SheetVersion);
        var seq = Enumerable.Range(0, 1000).Select(i => (uint)i).First(s => h.Rules.BreakableDrops(level.Seed, s, level.Depth, sheet).Items.Count > 0);
        var r = await w.Level.Pumped(w.Level.Items.Break(lease, seq));
        Assert.Equal((HostOwnerKind.World, w.Character.Id), (Assert.Single(r.Value.Items).Owner, r.Value.Items[0].ForCharacter));
        Assert.Equal("already_broken", (await w.Level.Pumped(w.Level.Items.Break(lease, seq))).Refusal!.Reason);
    }

    [Fact]
    public async Task A_kills_pile_is_picked_up_and_credited()
    {
        await using var h = await SdkHarness.Start();
        var w = await Start(h);
        var lease = await Lease(w.Level, w.Character.Id);
        var before = (await h.Data.ReadAsync((tx, _) => tx.Characters.Get(w.Character.Id)))!.Australium;
        var pile = await w.Level.Pumped(w.Level.Items.KillAustralium(lease, 1, "heavy", partySize: 1));
        Assert.True(pile.Value.IsAustraliumPile, pile.ToString());
        var credits = await w.Level.Pumped(w.Level.Items.ClaimAustralium(lease, pile.Value.Id));
        Assert.Equal([new HostAustraliumCredit(w.Character.Id, pile.Value.Count)], credits.Value);
        Assert.Equal(before + pile.Value.Count, (await h.Data.ReadAsync((tx, _) => tx.Characters.Get(w.Character.Id)))!.Australium);
    }

    static Task<ItemRecord> MintStack(SdkHarness h, string characterId, int count)
    {
        var r = h.Rules.RollItem(new ItemRollContext("scout", 1, 1, 5, 0, "depth1", "test"), (ulong)Random.Shared.NextInt64())
            with { BaseType = "health_small", Count = count };
        return h.Data.WriteAsync((tx, _) => tx.Items.Mint(new NewItem(r.Seed, r.BaseType, r.Rarity, r.ItemLevel, count, true,
            FakeGameRules_Encode(r), 1, 0, 1), OwnerKind.Character, characterId, null, 0, "test", null));
    }

    [Fact]
    public async Task Consume_lowers_a_stack()
    {
        await using var h = await SdkHarness.Start();
        var w = await Start(h);
        var lease = await Lease(w.Level, w.Character.Id);
        var stack = await MintStack(h, w.Character.Id, 3);
        var r = await w.Level.Pumped(w.Level.Items.Consume(lease, stack.Id));
        Assert.Equal((HostItemState.Live, 2), (r.Value.State, r.Value.Count));
    }

    [Fact]
    public async Task Consume_of_the_last_one_reports_it_consumed()
    {
        await using var h = await SdkHarness.Start();
        var w = await Start(h);
        var lease = await Lease(w.Level, w.Character.Id);
        var stack = await MintStack(h, w.Character.Id, 2);
        var r = await w.Level.Pumped(w.Level.Items.Consume(lease, stack.Id, 2));
        Assert.Equal(HostItemState.Consumed, r.Value.State);
    }

    static Task<ItemRecord> MintKit(SdkHarness h, string characterId, int slot)
    {
        var r = h.Rules.RollItem(new ItemRollContext("scout", 1, 1, 5, 0, "depth1", "test"), (ulong)Random.Shared.NextInt64())
            with { BaseType = SourceSharp.Host.Testing.FakeGameRules.RepairKit };
        return h.Data.WriteAsync((tx, _) => tx.Items.Mint(new NewItem(r.Seed, r.BaseType, r.Rarity, r.ItemLevel, 1, true,
            FakeGameRules_Encode(r), 1, 0, 1), OwnerKind.Character, characterId, null, slot, "test", null));
    }

    [Fact]
    public async Task Salvage_turns_an_item_into_metal()
    {
        await using var h = await SdkHarness.Start();
        var w = await Start(h);
        var lease = await Lease(w.Hub, w.Character.Id);
        var item = await Mint(h, OwnerKind.Character, w.Character.Id, null, 0);
        var r = await w.Hub.Pumped(w.Hub.Items.Salvage(lease, item.Id));
        Assert.Equal("metal", Assert.Single(r.Value).BaseType);
    }

    [Fact]
    public async Task RecordDeath_moves_the_backpack_to_the_corpse_and_LootCorpse_takes_it_back()
    {
        await using var h = await SdkHarness.Start();
        var w = await Start(h);
        var lease = await Lease(w.Level, w.Character.Id);
        var item = await Mint(h, OwnerKind.Character, w.Character.Id, null, 0);
        var died = await w.Level.Pumped(w.Level.Items.RecordDeath(lease));
        // The item and the starting kit on the belt.
        Assert.Equal(4, died.Value.Count);
        Assert.All(died.Value, i => Assert.Equal(HostOwnerKind.Corpse, i.Owner));
        var looted = await w.Level.Pumped(w.Level.Items.LootCorpse(lease));
        Assert.Contains(item.Id, looted.Value.Select(i => i.Id));
        Assert.All(looted.Value, i => Assert.Equal((HostOwnerKind.Character, HostContainer.Backpack), (i.Owner, i.Container)));
    }

    [Fact]
    public async Task A_call_on_an_item_the_character_does_not_own_is_a_typed_refusal()
    {
        await using var h = await SdkHarness.Start();
        var w = await Start(h);
        var lease = await Lease(w.Level, w.Character.Id);
        var item = await Mint(h, OwnerKind.World, "lvl-1", "lvl-1", -1);
        var r = await w.Level.Pumped(w.Level.Items.Drop(lease, item.Id));
        Assert.Equal(HostError.WrongOwner, r.Error);
    }
}
