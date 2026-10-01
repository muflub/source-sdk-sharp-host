using Descent.Service.Ledger;
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
        Assert.Equal([item.Id], r.Value.Select(i => i.Id));
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
        Assert.Equal(HostOwnerKind.Corpse, Assert.Single(died.Value).Owner);
        var looted = await w.Level.Pumped(w.Level.Items.LootCorpse(lease));
        Assert.Equal((item.Id, HostOwnerKind.Character), (Assert.Single(looted.Value).Id, looted.Value[0].Owner));
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
