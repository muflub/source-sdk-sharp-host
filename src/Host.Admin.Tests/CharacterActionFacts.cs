using System.Text.Json;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;
using SourceSharp.Host.Testing;

namespace SourceSharp.Host.Admin.Tests;

public abstract class WorldFacts : IAsyncLifetime
{
    protected AdminWorld W { get; } = new();
    protected IAdminActions A => W.Actions;
    public virtual Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await W.DisposeAsync();

    protected static string SheetJson(string name = "Ann", int level = 10, long xp = 0, int reached = 0, bool fallen = false, string cls = "scout") =>
        JsonSerializer.Serialize(new FakeGameRules.Sheet(cls, name, level, xp, reached, false, fallen));
}

/// <summary>Accounts and characters (§11): each action changes the row, is audited as the admin, and refuses a leased target.</summary>
public sealed class CharacterActionFacts : WorldFacts
{
    [Fact]
    public async Task Ban_sets_the_account_banned()
    {
        await W.Character();
        var r = await A.BanAccount(AdminWorld.Steam, "cheating");
        Assert.True(r.Ok, r.Message);
        Assert.True((await W.D.Read(tx => tx.Characters.GetAccount(AdminWorld.Steam)))!.Banned);
    }

    [Fact]
    public async Task Ban_writes_an_audit_row_as_the_admin_with_before_and_after()
    {
        await W.Character();
        await A.BanAccount(AdminWorld.Steam, "cheating");
        var row = Assert.Single(await W.Audit("account.ban"));
        Assert.Equal("admin@localhost", row.Actor);
        Assert.Contains("\"Banned\":false", row.BeforeJson);
        Assert.Contains("\"Banned\":true", row.AfterJson);
    }

    [Fact]
    public async Task Unban_clears_the_ban()
    {
        await W.Character();
        await A.BanAccount(AdminWorld.Steam, null);
        await A.UnbanAccount(AdminWorld.Steam);
        Assert.False((await W.D.Read(tx => tx.Characters.GetAccount(AdminWorld.Steam)))!.Banned);
    }

    [Fact]
    public async Task Delete_account_marks_every_character_deleted()
    {
        var a = await W.Character("Ann");
        var b = await W.Character("Bob");
        Assert.True((await A.DeleteAccount(AdminWorld.Steam)).Ok);
        Assert.True((await W.Get(a.Id))!.Deleted);
        Assert.True((await W.Get(b.Id))!.Deleted);
    }

    [Fact]
    public async Task Delete_account_is_refused_while_one_of_its_characters_is_leased()
    {
        var a = await W.Character("Ann");
        var b = await W.Character("Bob");
        await W.Lease(b.Id);
        var r = await A.DeleteAccount(AdminWorld.Steam);
        Assert.Equal(AdminOutcome.Leased, r.Outcome);
        Assert.False((await W.Get(a.Id))!.Deleted); // the whole transaction rolled back
    }

    [Fact]
    public async Task Edit_character_writes_the_sheet_and_its_columns()
    {
        var c = await W.Character();
        var r = await A.EditCharacter(c.Id, c.Version, SheetJson(level: 12, xp: 500, reached: 4));
        Assert.True(r.Ok, r.Message);
        var after = (await W.Get(c.Id))!;
        Assert.Equal((12, 500L, 4), (after.Level, after.Xp, after.ReachedDepth));
        Assert.Equal(12, FakeGameRules.SheetCodec.Read(new CharacterSheet(after.Sheet, after.SheetVersion)).Level);
    }

    [Fact]
    public async Task Edit_character_is_refused_with_the_rule_named()
    {
        var c = await W.Character(level: 10);
        var r = await A.EditCharacter(c.Id, c.Version, SheetJson(level: 9));
        Assert.Equal(AdminOutcome.Refused, r.Outcome);
        Assert.Equal("rule:level_decreased", r.Reason);
        Assert.Equal(10, (await W.Get(c.Id))!.Level);
    }

    [Fact]
    public async Task Edit_character_with_json_the_codec_cannot_read_is_refused()
    {
        var c = await W.Character();
        var r = await A.EditCharacter(c.Id, c.Version, "{ not json");
        Assert.Equal(("bad_json", AdminOutcome.Refused), (r.Reason, r.Outcome));
    }

    [Fact]
    public async Task Edit_character_at_a_stale_version_is_refused()
    {
        var c = await W.Character();
        var r = await A.EditCharacter(c.Id, c.Version + 7, SheetJson(level: 11));
        Assert.Equal("version_conflict", r.Reason);
    }

    [Fact]
    public async Task Edit_character_while_leased_is_refused_with_kick_and_edit()
    {
        var c = await W.Character();
        await W.Lease(c.Id);
        var r = await A.EditCharacter(c.Id, c.Version, SheetJson(level: 12));
        Assert.Equal(AdminOutcome.Leased, r.Outcome);
        Assert.Contains(AdminResult.KickAndEdit, r.Message);
        Assert.Equal(("lvl-1", c.Id), (r.InstanceId, r.CharacterId));
    }

    [Fact]
    public async Task Edit_character_while_leased_changes_nothing_and_audits_nothing()
    {
        var c = await W.Character();
        await W.Lease(c.Id);
        await A.EditCharacter(c.Id, c.Version, SheetJson(level: 12));
        Assert.Equal(10, (await W.Get(c.Id))!.Level);
        Assert.Empty(await W.Audit("character.edit"));
    }

    [Fact]
    public async Task Kick_and_release_kicks_the_player_on_the_leasing_instance()
    {
        var c = await W.Character();
        await W.Lease(c.Id);
        Assert.True((await A.KickAndRelease(c.Id)).Ok);
        Assert.Equal([$"kick:lvl-1:{AdminWorld.Steam}"], W.Lifecycle.Calls);
    }

    [Fact]
    public async Task Kick_and_release_leaves_no_lease_and_the_edit_then_succeeds()
    {
        var c = await W.Character();
        await W.Lease(c.Id);
        await A.KickAndRelease(c.Id);
        Assert.Null(await W.D.Read(tx => tx.Leases.Get(c.Id)));
        Assert.True((await A.EditCharacter(c.Id, c.Version, SheetJson(level: 12))).Ok);
    }

    [Fact]
    public async Task Kick_and_release_still_releases_when_the_instance_has_no_stream()
    {
        var c = await W.Character();
        await W.Lease(c.Id);
        W.Lifecycle.HasStream = false;
        Assert.True((await A.KickAndRelease(c.Id)).Ok);
        Assert.Null(await W.D.Read(tx => tx.Leases.Get(c.Id)));
    }

    [Fact]
    public async Task Release_lease_force_releases_and_audits()
    {
        var c = await W.Character();
        await W.Lease(c.Id);
        Assert.True((await A.ReleaseLease(c.Id)).Ok);
        Assert.Null(await W.D.Read(tx => tx.Leases.Get(c.Id)));
        Assert.Equal("admin@localhost", Assert.Single(await W.Audit("lease.force_release")).Actor);
    }

    [Fact]
    public async Task Grant_australium_books_a_ledger_entry_and_the_cached_sum()
    {
        var c = await W.Character();
        Assert.True((await A.GrantAustralium(c.Id, 250, "refund")).Ok);
        Assert.Equal(250, (await W.Get(c.Id))!.Australium);
        Assert.Equal(250, await W.D.Read(tx => tx.Characters.AustraliumLedgerSum(c.Id)));
    }

    [Fact]
    public async Task Grant_australium_is_refused_while_leased()
    {
        var c = await W.Character();
        await W.Lease(c.Id);
        Assert.Equal(AdminOutcome.Leased, (await A.GrantAustralium(c.Id, 250, "refund")).Outcome);
        Assert.Equal(0, (await W.Get(c.Id))!.Australium);
    }

    [Fact]
    public async Task Set_fallen_goes_through_the_ledger_and_is_audited()
    {
        var c = await W.Character(hardcore: true);
        Assert.True((await A.SetFallen(c.Id, true)).Ok);
        Assert.Equal([$"set_fallen:{c.Id}:True"], W.Ledger.Calls);
        Assert.True((await W.Get(c.Id))!.Fallen);
        Assert.Single(await W.Audit("character.fallen"));
    }

    [Fact]
    public async Task Delete_character_marks_it_deleted()
    {
        var c = await W.Character();
        Assert.True((await A.DeleteCharacter(c.Id)).Ok);
        Assert.True((await W.Get(c.Id))!.Deleted);
    }

    [Fact]
    public async Task An_unknown_character_is_refused_not_found()
    {
        Assert.Equal("no_character", (await A.DeleteCharacter("nope")).Reason);
    }
}
