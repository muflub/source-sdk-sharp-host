using Bunit;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Admin.Components.Pages;

namespace SourceSharp.Host.Admin.Tests;

/// <summary>plan_steam_auth on the admin side: bans by address or SteamID, the gateway's live options, Steam's state.</summary>
public sealed class SteamAdminActionFacts : WorldFacts
{
    [Fact]
    public async Task A_ban_by_address_is_stored_audited_and_pushed()
    {
        var r = await A.AddBan("address", "198.51.100.0/24", null, "abuse");
        Assert.True(r.Ok, r.Message);
        var b = Assert.Single(await W.D.Read(tx => tx.Bans.List()));
        Assert.Equal(("address", "198.51.100.0/24", (DateTimeOffset?)null, "admin", "abuse"), (b.Kind, b.Value, b.Until, b.Source, b.Reason));
        Assert.Single(await W.Audit("ban.add"));
        Assert.Equal(["push-bans"], W.Gateway.Calls);
    }

    [Fact]
    public async Task A_temporary_ban_ends_after_its_duration()
    {
        await A.AddBan("steamid", AdminWorld.Steam, TimeSpan.FromMinutes(30), "cooldown");
        var b = Assert.Single(await W.D.Read(tx => tx.Bans.List()));
        Assert.Equal(W.D.Clock.GetUtcNow().AddMinutes(30), b.Until);
    }

    [Theory]
    [InlineData("address", "not-an-ip")]
    [InlineData("address", "2001:db8::1")]
    [InlineData("steamid", "STEAM_0:1:2")]
    [InlineData("steamid", "0")]
    [InlineData("nonsense", "1")]
    public async Task A_bad_ban_is_refused_and_neither_stored_nor_pushed(string kind, string value)
    {
        var r = await A.AddBan(kind, value, null, null);
        Assert.False(r.Ok);
        Assert.Empty(await W.D.Read(tx => tx.Bans.List()));
        Assert.Empty(W.Gateway.Calls);
    }

    [Fact]
    public async Task Lifting_a_steamid_ban_clears_the_accounts_flag_and_pushes()
    {
        await W.Character();
        await A.BanAccount(AdminWorld.Steam, "cheating");
        var r = await A.LiftBan("steamid", AdminWorld.Steam);
        Assert.True(r.Ok, r.Message);
        Assert.Empty(await W.D.Read(tx => tx.Bans.List()));
        Assert.False((await W.D.Read(tx => tx.Characters.GetAccount(AdminWorld.Steam)))!.Banned);
        Assert.Equal(["push-bans", "push-bans"], W.Gateway.Calls);
    }

    [Fact]
    public async Task Lifting_a_ban_that_is_not_there_is_refused() =>
        Assert.False((await A.LiftBan("address", "203.0.113.7")).Ok);

    [Fact]
    public async Task Live_options_the_gateway_accepts_are_applied_then_stored()
    {
        const string json = "{\"presenceTimeoutMs\":\"20000\"}";
        var r = await A.SetSetting("gateway.options", json);
        Assert.True(r.Ok, r.Message);
        Assert.Equal([$"options:{json}"], W.Gateway.Calls);
        Assert.Equal(json, (await W.D.Read(tx => tx.Settings.Get("gateway.options")))!.ValueJson);
    }

    [Fact]
    public async Task Live_options_the_gateway_refuses_are_not_stored()
    {
        W.Gateway.RefuseOptionsContaining = "\"0\"";
        var r = await A.SetSetting("gateway.options", "{\"presenceTimeoutMs\":\"0\"}");
        Assert.False(r.Ok);
        Assert.Null(await W.D.Read(tx => tx.Settings.Get("gateway.options")));
    }
}

public sealed class SteamAdminPageFacts : PageFacts
{
    [Fact]
    public async Task Bans_lists_each_ban_and_lift_asks_first()
    {
        await W.D.Write(tx => tx.Bans.Set(new BanRecord("address", "203.0.113.7", null, "admin", "abuse", tx.Now)));
        var cut = Page<Bans>("#ban-address-203_0_113_7");
        Assert.Contains("permanent", cut.Find("#ban-address-203_0_113_7").TextContent);
        cut.Find("#ban-address-203_0_113_7 .lift").Click();
        Assert.Empty(Rec.Calls);
        Confirm(cut, "abuse");
        WaitResult(cut, "lifted");
        Assert.Equal(["address", "203.0.113.7"], Rec.Single(nameof(IAdminActions.LiftBan)));
    }

    [Fact]
    public async Task Bans_add_calls_AddBan_with_the_typed_fields()
    {
        var cut = Page<Bans>("#ban-add");
        cut.Find("#ban-kind").Change("steamid");
        cut.Find("#ban-value").Change(AdminWorld.Steam);
        cut.Find("#ban-minutes").Change("15");
        cut.Find("#ban-reason").Change("griefing");
        cut.Find("#ban-add").Click();
        WaitResult(cut, "banned");
        Assert.Equal(["steamid", AdminWorld.Steam, TimeSpan.FromMinutes(15), "griefing"], Rec.Single(nameof(IAdminActions.AddBan)));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Bans_hides_expired_bans_unless_asked()
    {
        await W.D.Write(tx => tx.Bans.Set(new BanRecord("address", "203.0.113.8", tx.Now.AddMinutes(-1), "auto", "strikes", tx.Now.AddMinutes(-11))));
        var cut = Page<Bans>("#bans");
        Assert.Empty(cut.FindAll("#ban-address-203_0_113_8"));
        cut.Find("#show-expired").Change(true);
        Assert.Single(cut.FindAll("#ban-address-203_0_113_8"));
    }

    [Fact]
    public async Task Gateway_shows_steams_state_and_each_routes_auth()
    {
        W.Gateway.Current = W.Gateway.Current with
        {
            Routes = [new AdminRoute("s-1", "203.0.113.9:27005", AdminWorld.Steam, "10.42.0.9:5010", 1, 2, DateTimeOffset.UnixEpoch, "reauthing")],
            Steam = new AdminSteamState("Steamworks", true, "90294096598126611", "122d1a4308bd"),
        };
        var cut = Page<Gateway>("#gw-steam");
        Assert.Contains("logged on as 90294096598126611", cut.Find("#gw-steam").TextContent);
        Assert.Equal("reauthing", cut.Find("#route-s-1 .auth").TextContent);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Dashboard_says_when_steam_is_not_logged_on()
    {
        W.Gateway.Current = W.Gateway.Current with { Steam = new AdminSteamState("Steamworks", false, "", "") };
        var cut = Page<Dashboard>("#steam");
        Assert.Contains("NOT logged on", cut.Find("#steam").TextContent);
        await Task.CompletedTask;
    }
}
