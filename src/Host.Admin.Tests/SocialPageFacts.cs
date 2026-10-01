using Bunit;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Admin.Components.Pages;

namespace SourceSharp.Host.Admin.Tests;

/// <summary>Parties, Trades and Lost &amp; Found pages.</summary>
public sealed class SocialPageFacts : PageFacts
{
    async Task<Party> Party(string leader, params string[] members) => await W.D.Write(async tx =>
    {
        var p = await tx.Parties.Create(leader);
        foreach (var m in members) { await tx.Parties.Invite(p.Id, m); p = await tx.Parties.Join(p.Id, m); }
        return p;
    });

    [Fact]
    public async Task Parties_shows_members_by_name_and_the_live_instance()
    {
        var a = await W.Character("Ann");
        var p = await Party(a.Id);
        await W.Lease(a.Id);
        var cut = Page<Parties>($"#party-{p.Id}");
        var row = cut.Find($"#party-{p.Id}").TextContent;
        Assert.Contains("Ann", row);
        Assert.Contains("lvl-1", row);
    }

    [Fact]
    public async Task Parties_disband_calls_DisbandParty_after_confirm()
    {
        var a = await W.Character("Ann");
        var p = await Party(a.Id);
        var cut = Page<Parties>(".disband");
        cut.Find(".disband").Click();
        Confirm(cut);
        WaitResult(cut, "disbanded");
        Assert.Equal([p.Id], Rec.Single(nameof(IAdminActions.DisbandParty)));
    }

    [Fact]
    public async Task Parties_kick_calls_KickFromParty_with_that_member()
    {
        var a = await W.Character("Ann");
        var b = await W.Character("Bob", account: "76561198000000002");
        var p = await Party(a.Id, b.Id);
        var cut = Page<Parties>(".kick");
        cut.FindAll($"#party-{p.Id} span").Single(s => s.TextContent.Contains("Bob")).QuerySelector(".kick")!.Click();
        Confirm(cut);
        WaitResult(cut, "removed");
        Assert.Equal([p.Id, b.Id], Rec.Single(nameof(IAdminActions.KickFromParty)));
    }

    [Fact]
    public async Task Parties_disband_of_a_leased_party_is_refused_with_kick_and_edit()
    {
        var a = await W.Character("Ann");
        await Party(a.Id);
        await W.Lease(a.Id);
        var cut = Page<Parties>(".disband");
        cut.Find(".disband").Click();
        Confirm(cut);
        WaitResult(cut, AdminResult.KickAndEdit);
    }

    async Task<(TradeRecord, ItemRecord)> Trade()
    {
        var a = await W.Character("Ann");
        var b = await W.Character("Bob", account: "76561198000000002");
        var t = await W.D.Write(tx => tx.Trades.Open("hub-1", a.Id, b.Id));
        var i = await W.Mint(OwnerKind.TradeEscrow, t.Id, 0);
        return (await W.D.Write(tx => tx.Trades.Save(t with { OfferA = [i.Id], LockedA = true })), i);
    }

    [Fact]
    public async Task Trades_shows_the_escrow()
    {
        var (t, i) = await Trade();
        var cut = Page<Trades>($"#trade-{t.Id}");
        Assert.Contains(i.Id, cut.Find($"#trade-{t.Id}").TextContent);
    }

    [Fact]
    public async Task Trades_cancel_calls_CancelTrade_after_confirm()
    {
        var (t, _) = await Trade();
        var cut = Page<Trades>(".cancel");
        cut.Find(".cancel").Click();
        Confirm(cut, "Cancelled");
        WaitResult(cut, "returned from escrow");
        Assert.Equal([t.Id], Rec.Single(nameof(IAdminActions.CancelTrade)));
    }

    async Task<LostAndFoundRecord> Lost()
    {
        var c = await W.Character("Ann");
        var i = await W.Mint(OwnerKind.LostAndFound, c.Id);
        return await W.D.Write(tx => tx.LostAndFound.Add(c.Id, i.Id, 40, "lvl-1"));
    }

    [Fact]
    public async Task Lost_and_found_shows_the_fee_and_origin()
    {
        var e = await Lost();
        var cut = Page<LostAndFound>($"#lf-{e.Id}");
        var row = cut.Find($"#lf-{e.Id}").TextContent;
        Assert.Contains("40", row);
        Assert.Contains("lvl-1", row);
    }

    [Fact]
    public async Task Lost_and_found_waive_calls_WaiveFee_for_the_row()
    {
        var e = await Lost();
        var cut = Page<LostAndFound>(".waive");
        cut.Find(".waive").Click();
        WaitResult(cut, "waived");
        Assert.Equal([e.Id], Rec.Single(nameof(IAdminActions.WaiveFee)));
    }

    [Fact]
    public async Task Lost_and_found_release_calls_ReleaseToStash_for_the_row()
    {
        var e = await Lost();
        var cut = Page<LostAndFound>(".release");
        cut.Find(".release").Click();
        WaitResult(cut, "stash");
        Assert.Equal([e.Id], Rec.Single(nameof(IAdminActions.ReleaseToStash)));
    }

    [Fact]
    public async Task Lost_and_found_delete_calls_DeleteLostAndFound_after_confirm()
    {
        var e = await Lost();
        var cut = Page<LostAndFound>(".delete");
        cut.Find(".delete").Click();
        Confirm(cut, "Destroyed");
        WaitResult(cut, "deleted");
        Assert.Equal([e.Id], Rec.Single(nameof(IAdminActions.DeleteLostAndFound)));
    }
}
