using System.Text.Json.Nodes;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Testing;

namespace SourceSharp.Host.Admin.Tests;

/// <summary>Items and stash (§11): edit through the rules codec, move, give (a mint), destroy; stash move and remove.</summary>
public sealed class ItemActionFacts : WorldFacts
{
    string Json(ItemRecord i) => W.Rules.Items.ToJson(W.Rules.Items.Decode(i.Instance, i.SchemaVersion));

    static string With(string json, string field, JsonNode value)
    {
        var n = JsonNode.Parse(json)!;
        n[field] = value;
        return n.ToJsonString();
    }

    [Fact]
    public async Task An_edit_that_still_replays_is_not_flagged()
    {
        var c = await W.Character();
        var i = await W.Mint(OwnerKind.Character, c.Id, 0);
        var r = await A.EditItem(i.Id, i.Version, With(Json(i), "Identified", !i.Identified));
        Assert.True(r.Ok, r.Message);
        Assert.False((await W.Item(i.Id))!.AdminEdited);
    }

    [Fact]
    public async Task An_edit_that_no_longer_replays_is_flagged_admin_edited_and_audited()
    {
        var c = await W.Character();
        var i = await W.Mint(OwnerKind.Character, c.Id, 0);
        var json = With(Json(i), "ItemLevel", 99);
        Assert.False(W.Rules.Replays(W.Rules.Items.FromJson(json))); // the stimulus really breaks the replay

        var r = await A.EditItem(i.Id, i.Version, json);

        Assert.True(r.Ok, r.Message);
        var after = (await W.Item(i.Id))!;
        Assert.True(after.AdminEdited);
        Assert.Equal(99, W.Rules.Items.Decode(after.Instance, after.SchemaVersion).ItemLevel);
        var audit = Assert.Single(await W.Audit("item.edit"));
        Assert.Equal(("admin@localhost", i.Id), (audit.Actor, audit.Target));
        Assert.Contains("\"replays\":false", audit.AfterJson);
        Assert.Contains(await W.D.Read(tx => tx.Items.Events(i.Id)), e => e.Kind == "admin_edit" && e.Actor == "admin@localhost");
    }

    [Fact]
    public async Task An_item_of_a_leased_character_is_refused_with_kick_and_edit()
    {
        var c = await W.Character();
        var i = await W.Mint(OwnerKind.Character, c.Id, 0);
        await W.Lease(c.Id);
        var r = await A.EditItem(i.Id, i.Version, With(Json(i), "ItemLevel", 99));
        Assert.Equal(AdminOutcome.Leased, r.Outcome);
        Assert.Contains(AdminResult.KickAndEdit, r.Message);
        Assert.False((await W.Item(i.Id))!.AdminEdited);
    }

    [Fact]
    public async Task A_world_item_dropped_for_a_leased_character_is_refused()
    {
        var c = await W.Character();
        var i = await W.Mint(OwnerKind.World, "lvl-1", instanceId: "lvl-1", forCharacter: c.Id);
        await W.Lease(c.Id);
        Assert.Equal(AdminOutcome.Leased, (await A.DestroyItem(i.Id, "test")).Outcome);
    }

    [Fact]
    public async Task Move_puts_the_item_with_the_new_owner()
    {
        var c = await W.Character();
        var i = await W.Mint(OwnerKind.Character, c.Id, 0);
        var stash = W.Ledger.StashOwner(c.Account, false);
        Assert.True((await A.MoveItem(i.Id, new AdminItemTarget(OwnerKind.Stash, stash, 3))).Ok);
        var after = (await W.Item(i.Id))!;
        Assert.Equal((OwnerKind.Stash, stash, 3), (after.OwnerKind, after.OwnerId, after.Slot));
    }

    [Fact]
    public async Task Move_into_a_leased_characters_backpack_is_refused()
    {
        var a = await W.Character("Ann");
        var b = await W.Character("Bob", account: "76561198000000002");
        var i = await W.Mint(OwnerKind.Character, a.Id, 0);
        await W.Lease(b.Id);
        var r = await A.MoveItem(i.Id, new AdminItemTarget(OwnerKind.Character, b.Id, 0));
        Assert.Equal((AdminOutcome.Leased, b.Id), (r.Outcome, r.CharacterId));
    }

    [Fact]
    public async Task Move_to_the_world_is_refused()
    {
        var c = await W.Character();
        var i = await W.Mint(OwnerKind.Character, c.Id, 0);
        Assert.Equal("bad_owner", (await A.MoveItem(i.Id, new AdminItemTarget(OwnerKind.World, "lvl-1"))).Reason);
    }

    [Fact]
    public async Task Give_mints_one_item_minted_by_admin()
    {
        var c = await W.Character();
        var before = await W.D.Read(tx => tx.Items.Totals());
        var r = await A.GiveItem(new AdminItemTarget(OwnerKind.Character, c.Id, 2), W.Rules.Items.ToJson(W.Rolled()));
        Assert.True(r.Ok, r.Message);
        var minted = Assert.IsType<ItemRecord>(r.Value);
        Assert.Equal(("admin", OwnerKind.Character, c.Id), (minted.MintedBy, minted.OwnerKind, minted.OwnerId));
        Assert.Equal(before.Minted + 1, (await W.D.Read(tx => tx.Items.Totals())).Minted);
    }

    [Fact]
    public async Task Give_of_an_item_the_rules_would_not_roll_is_flagged_admin_edited()
    {
        var c = await W.Character();
        var json = With(W.Rules.Items.ToJson(W.Rolled()), "ItemLevel", 500);
        var r = await A.GiveItem(new AdminItemTarget(OwnerKind.Character, c.Id, 2), json);
        Assert.True(Assert.IsType<ItemRecord>(r.Value).AdminEdited);
    }

    [Fact]
    public async Task Give_to_a_leased_character_is_refused()
    {
        var c = await W.Character();
        await W.Lease(c.Id);
        var before = await W.D.Read(tx => tx.Items.Totals());
        Assert.Equal(AdminOutcome.Leased, (await A.GiveItem(new AdminItemTarget(OwnerKind.Character, c.Id), W.Rules.Items.ToJson(W.Rolled()))).Outcome);
        Assert.Equal(before, await W.D.Read(tx => tx.Items.Totals()));
    }

    [Fact]
    public async Task Destroy_ends_the_item_destroyed()
    {
        var c = await W.Character();
        var i = await W.Mint(OwnerKind.Character, c.Id, 0);
        Assert.True((await A.DestroyItem(i.Id, "dupe")).Ok);
        var after = (await W.Item(i.Id))!;
        Assert.Equal(ItemState.Destroyed, after.State);
        Assert.Equal("admin: dupe", after.TerminalReason);
    }

    [Fact]
    public async Task Stash_move_changes_the_slot()
    {
        var c = await W.Character();
        var i = await W.Mint(OwnerKind.Stash, W.Ledger.StashOwner(c.Account, false), 0);
        Assert.True((await A.StashMove(i.Id, 7)).Ok);
        Assert.Equal(7, (await W.Item(i.Id))!.Slot);
    }

    [Fact]
    public async Task Stash_move_onto_a_taken_slot_is_refused()
    {
        var c = await W.Character();
        var stash = W.Ledger.StashOwner(c.Account, false);
        var i = await W.Mint(OwnerKind.Stash, stash, 0);
        await W.Mint(OwnerKind.Stash, stash, 7);
        Assert.Equal("slot_taken", (await A.StashMove(i.Id, 7)).Reason);
    }

    [Fact]
    public async Task Stash_move_past_the_capacity_is_refused()
    {
        var c = await W.Character();
        var i = await W.Mint(OwnerKind.Stash, W.Ledger.StashOwner(c.Account, false), 0);
        Assert.Equal("bad_slot", (await A.StashMove(i.Id, W.Ledger.Capacity)).Reason);
    }

    [Fact]
    public async Task Stash_edits_are_refused_while_a_character_of_that_ladder_is_leased()
    {
        var c = await W.Character();
        var i = await W.Mint(OwnerKind.Stash, W.Ledger.StashOwner(c.Account, false), 0);
        await W.Lease(c.Id);
        Assert.Equal(AdminOutcome.Leased, (await A.StashRemove(i.Id)).Outcome);
    }

    [Fact]
    public async Task Stash_edits_are_accepted_while_only_the_other_ladder_is_leased()
    {
        var sc = await W.Character("Soft");
        var hc = await W.Character("Hard", hardcore: true);
        var i = await W.Mint(OwnerKind.Stash, W.Ledger.StashOwner(sc.Account, false), 0);
        await W.Lease(hc.Id);
        Assert.True((await A.StashRemove(i.Id)).Ok);
    }

    [Fact]
    public async Task Stash_remove_hands_the_item_to_the_admin_owner()
    {
        var c = await W.Character();
        var i = await W.Mint(OwnerKind.Stash, W.Ledger.StashOwner(c.Account, false), 0);
        await A.StashRemove(i.Id);
        var after = (await W.Item(i.Id))!;
        Assert.Equal((OwnerKind.Admin, ItemState.Live), (after.OwnerKind, after.State));
    }
}
