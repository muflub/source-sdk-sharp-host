using System.Text.Json.Nodes;
using Bunit;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Admin.Components.Pages;

namespace SourceSharp.Host.Admin.Tests;

/// <summary>Accounts, Characters, Items and Stash pages: render with real stores; each action calls IAdminActions with the shown values.</summary>
public sealed class PeoplePageFacts : PageFacts
{
    [Fact]
    public async Task Accounts_lists_each_account_with_its_characters()
    {
        await W.Character("Ann");
        var cut = Page<Accounts>($"#acc-{AdminWorld.Steam}");
        Assert.Contains("Ann (scout 10)", cut.Find($"#acc-{AdminWorld.Steam}").TextContent);
    }

    [Fact]
    public async Task Accounts_ban_calls_BanAccount_with_the_row_and_the_typed_note()
    {
        await W.Character("Ann");
        var cut = Page<Accounts>($"#acc-{AdminWorld.Steam}");
        cut.Find(".note-in").Change("griefing");
        cut.Find(".ban").Click();
        WaitResult(cut, "banned");
        Assert.Equal([AdminWorld.Steam, "griefing"], Rec.Single(nameof(IAdminActions.BanAccount)));
    }

    [Fact]
    public async Task Accounts_delete_asks_first_and_shows_the_diff()
    {
        await W.Character("Ann");
        var cut = Page<Accounts>($"#acc-{AdminWorld.Steam}");
        cut.Find(".delete").Click();
        Assert.Empty(Rec.Calls); // nothing happens before Confirm
        Confirm(cut, "-   \"Banned\": false");
        WaitResult(cut, "deleted");
        Assert.Equal([AdminWorld.Steam], Rec.Single(nameof(IAdminActions.DeleteAccount)));
    }

    [Fact]
    public async Task Accounts_delete_of_a_leased_account_shows_kick_and_edit()
    {
        var c = await W.Character("Ann");
        await W.Lease(c.Id);
        var cut = Page<Accounts>($"#acc-{AdminWorld.Steam}");
        cut.Find(".delete").Click();
        Confirm(cut);
        WaitResult(cut, AdminResult.KickAndEdit);
        Assert.NotNull(cut.Find("#kick-and-edit"));
    }

    [Fact]
    public async Task Characters_open_shows_the_sheet_decoded_by_the_rules()
    {
        var c = await W.Character("Ann");
        var cut = Page<Characters>(".open");
        cut.Find(".open").Click();
        cut.WaitForAssertion(() => Assert.Contains("\"Name\":\"Ann\"", cut.Find("#sheet").GetAttribute("value")));
        Assert.Contains(c.Id, cut.Find("#detail").TextContent);
    }

    [Fact]
    public async Task Characters_save_sheet_calls_EditCharacter_with_the_edited_json_after_confirm()
    {
        var c = await W.Character("Ann");
        var cut = Page<Characters>(".open");
        cut.Find(".open").Click();
        cut.WaitForElement("#sheet");
        var json = SheetJson(level: 12);
        cut.Find("#sheet").Change(json);
        cut.Find("#save-sheet").Click();
        Confirm(cut, "+   \"Level\": 12");
        WaitResult(cut, "edited Ann");
        Assert.Equal([c.Id, c.Version, json], Rec.Single(nameof(IAdminActions.EditCharacter)));
        Assert.Equal(12, (await W.Get(c.Id))!.Level);
    }

    [Fact]
    public async Task Characters_a_leased_edit_is_refused_and_kick_and_edit_kicks_then_saves()
    {
        var c = await W.Character("Ann");
        await W.Lease(c.Id);
        var cut = Page<Characters>(".open");
        cut.Find(".open").Click();
        cut.WaitForElement("#sheet");
        cut.Find("#sheet").Change(SheetJson(level: 12));
        cut.Find("#save-sheet").Click();
        Confirm(cut);
        WaitResult(cut, AdminResult.KickAndEdit);
        Assert.Equal(10, (await W.Get(c.Id))!.Level);

        cut.Find("#kick-and-edit").Click();

        WaitResult(cut, "edited Ann");
        Assert.Equal([c.Id], Rec.Single(nameof(IAdminActions.KickAndRelease)));
        Assert.Equal([$"kick:lvl-1:{AdminWorld.Steam}"], W.Lifecycle.Calls);
        Assert.Equal(12, (await W.Get(c.Id))!.Level);
    }

    [Fact]
    public async Task Characters_grant_calls_GrantAustralium_with_the_typed_amount_and_reason()
    {
        var c = await W.Character("Ann");
        var cut = Page<Characters>(".open");
        cut.Find(".open").Click();
        cut.WaitForElement("#grant");
        cut.Find("#grant-amount").Change("150");
        cut.Find("#grant-reason").Change("refund");
        cut.Find("#grant").Click();
        WaitResult(cut, "granted 150");
        Assert.Equal([c.Id, 150L, "refund"], Rec.Single(nameof(IAdminActions.GrantAustralium)));
    }

    string ItemJson(ItemRecord i) => W.Rules.Items.ToJson(W.Rules.Items.Decode(i.Instance, i.SchemaVersion));

    [Fact]
    public async Task Items_search_by_owner_lists_that_owners_items()
    {
        var a = await W.Character("Ann");
        var b = await W.Character("Bob", account: "76561198000000002");
        var mine = await W.Mint(OwnerKind.Character, a.Id, 0);
        var theirs = await W.Mint(OwnerKind.Character, b.Id, 0);
        var cut = Page<Items>("#items");
        cut.Find("#f-owner-id").Change(a.Id);
        cut.Find("#search").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Contains(mine.Id, cut.Find("#items").TextContent);
            Assert.DoesNotContain(theirs.Id, cut.Find("#items").TextContent);
        });
    }

    [Fact]
    public async Task Items_an_edit_that_no_longer_replays_is_flagged_admin_edited_with_an_audit_row()
    {
        var c = await W.Character("Ann");
        var i = await W.Mint(OwnerKind.Character, c.Id, 0);
        var cut = Page<Items>(".open");
        cut.Find(".open").Click();
        cut.WaitForElement("#instance-json");
        var n = JsonNode.Parse(ItemJson(i))!;
        n["ItemLevel"] = 99;
        cut.Find("#instance-json").Change(n.ToJsonString());
        cut.Find("#save-item").Click();
        Confirm(cut, "\"ItemLevel\": 99");
        WaitResult(cut, "admin_edited");

        Assert.Equal([i.Id, i.Version, n.ToJsonString()], Rec.Single(nameof(IAdminActions.EditItem)));
        Assert.True((await W.Item(i.Id))!.AdminEdited);
        Assert.Equal("admin@localhost", Assert.Single(await W.Audit("item.edit")).Actor);
        cut.WaitForAssertion(() => Assert.Contains("admin_edited", cut.Find("#detail").TextContent));
    }

    [Fact]
    public async Task Items_move_calls_MoveItem_with_the_chosen_owner_and_slot()
    {
        var c = await W.Character("Ann");
        var i = await W.Mint(OwnerKind.Character, c.Id, 0);
        var cut = Page<Items>(".open");
        cut.Find(".open").Click();
        cut.WaitForElement("#move");
        cut.Find("#move-kind").Change("Stash");
        cut.Find("#move-owner").Change($"{AdminWorld.Steam}:sc");
        cut.Find("#move-slot").Change("4");
        cut.Find("#move").Click();
        Confirm(cut);
        WaitResult(cut, "moved");
        Assert.Equal([i.Id, new AdminItemTarget(OwnerKind.Stash, $"{AdminWorld.Steam}:sc", 4)], Rec.Single(nameof(IAdminActions.MoveItem)));
    }

    [Fact]
    public async Task Items_give_calls_GiveItem_with_the_target_and_json()
    {
        var c = await W.Character("Ann");
        var cut = Page<Items>("#give");
        var json = W.Rules.Items.ToJson(W.Rolled());
        cut.Find("#give-owner").Change(c.Id);
        cut.Find("#give-slot").Change("2");
        cut.Find("#give-json").Change(json);
        cut.Find("#give").Click();
        WaitResult(cut, "gave");
        Assert.Equal([new AdminItemTarget(OwnerKind.Character, c.Id, 2), json], Rec.Single(nameof(IAdminActions.GiveItem)));
    }

    [Fact]
    public async Task Items_destroy_calls_DestroyItem_with_the_reason()
    {
        var c = await W.Character("Ann");
        var i = await W.Mint(OwnerKind.Character, c.Id, 0);
        var cut = Page<Items>(".open");
        cut.Find(".open").Click();
        cut.WaitForElement("#destroy");
        cut.Find("#destroy-reason").Change("dupe");
        cut.Find("#destroy").Click();
        Confirm(cut, "Destroyed");
        WaitResult(cut, "destroyed");
        Assert.Equal([i.Id, "dupe"], Rec.Single(nameof(IAdminActions.DestroyItem)));
    }

    [Fact]
    public async Task Stash_flags_an_overflowing_stash_withdraw_only()
    {
        var c = await W.Character("Ann");
        W.Ledger.Capacity = 1;
        await W.Mint(OwnerKind.Stash, W.Ledger.StashOwner(c.Account, false), 0);
        await W.Mint(OwnerKind.Stash, W.Ledger.StashOwner(c.Account, false), 1);
        var cut = Page<Stash>("#stashes");
        cut.WaitForAssertion(() => Assert.Contains("overflow: withdraw-only", cut.Find("#stashes").TextContent));
    }

    [Fact]
    public async Task Stash_move_calls_StashMove_with_the_typed_slot()
    {
        var c = await W.Character("Ann");
        var i = await W.Mint(OwnerKind.Stash, W.Ledger.StashOwner(c.Account, false), 0);
        var cut = Page<Stash>(".open");
        cut.Find(".open").Click();
        cut.WaitForElement(".move");
        cut.Find(".slot-in").Change("9");
        cut.Find(".move").Click();
        Confirm(cut, "+   \"Slot\": 9");
        WaitResult(cut, "slot 9");
        Assert.Equal([i.Id, 9], Rec.Single(nameof(IAdminActions.StashMove)));
    }

    static string SheetJson(int level) =>
        System.Text.Json.JsonSerializer.Serialize(new SourceSharp.Host.Testing.FakeGameRules.Sheet("scout", "Ann", level, 0, 0, false, false));
}
