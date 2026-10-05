using Microsoft.Extensions.Logging.Abstractions;
using SourceSharp.Host.Abstractions;
using TF2.Descent.Service.Gateway;
using TF2.Descent.Service.Tests.Ledger;
using G = SourceSharp.Host.Proto.Gateway;
using P = SourceSharp.Host.Proto;

namespace TF2.Descent.Service.Tests.Gateway;

/// <summary>plan_steam_auth S-18: the server-browser entry, from the mod's block and the world as it is.</summary>
public class BrowserFacts
{
    static (BrowserPublisher Publisher, RecordingGatewayControl Control) Publisher(LedgerHarness h)
    {
        var control = new RecordingGatewayControl();
        return (new BrowserPublisher(h.D.Data, control, h.Options, h.D.Clock, NullLogger<BrowserPublisher>.Instance), control);
    }

    static Task Block(LedgerHarness h, P.Browser b) =>
        h.W(async tx => { await tx.Settings.Set(BrowserPublisher.BlockKey, Google.Protobuf.JsonFormatter.Default.Format(b), "test"); return true; });

    [Fact]
    public void Unknown_placeholders_are_found_and_known_ones_are_not()
    {
        var b = new P.Browser { Name = "Descent {players}", Map = "{bogus}", Tags = "{deepest},x" };
        b.Rules["r"] = "{also_bogus} {pool_ready}";
        Assert.Equal(["bogus", "also_bogus"], BrowserTemplate.UnknownKeys(b));
    }

    [Fact]
    public void Render_fills_known_keys_and_leaves_text_alone()
    {
        var values = new Dictionary<string, string> { ["deepest"] = "7", ["players"] = "3" };
        Assert.Equal("Descent: depth 7, 3 playing {x}", BrowserTemplate.Render("Descent: depth {deepest}, {players} playing {x}", values));
    }

    [Fact]
    public async Task The_entry_renders_the_block_over_the_world_as_it_is()
    {
        await using var h = await LedgerHarness.Create(depth: 7);
        await Block(h, new P.Browser { Name = "Descent ({levels_open} open)", Map = "depth {deepest}", Tags = "rpg,deepest{deepest}", Region = "eu",
                                      Rules = { ["players_by_level"] = "{players_by_level}" } });
        await h.W(tx => tx.Sessions.Upsert(new SessionRecord("s1", "1.2.3.4:5", "76561198000000001", "hub-1", "", "", "open", h.D.Clock.GetUtcNow(), h.D.Clock.GetUtcNow(), h.D.Clock.GetUtcNow())));
        var (p, _) = Publisher(h);

        var info = await p.Build(default);

        Assert.Equal(("Descent (1 open)", "depth 7", "rpg,deepest7", "eu", 1), (info.Name, info.Map, info.Tags, info.Region, info.Players));
        Assert.Equal("7:0", info.Rules["players_by_level"]);
        Assert.Equal(h.Options.Instances.HubMaxPlayers + h.Options.Instances.LevelMaxPlayers, info.MaxPlayers);
    }

    [Fact]
    public async Task Without_a_block_the_entry_is_the_mod_and_its_town()
    {
        await using var h = await LedgerHarness.Create();
        var info = await Publisher(h).Publisher.Build(default);
        Assert.Equal((h.Options.Mod.Name, h.Options.Mod.TownMap), (info.Name, info.Map));
    }

    [Fact]
    public async Task It_pushes_when_the_world_changes_and_not_otherwise()
    {
        await using var h = await LedgerHarness.Create();
        await Block(h, new P.Browser { Name = "{players} playing" });
        var (p, control) = Publisher(h);

        Assert.True(await p.PublishOnce(default));
        Assert.False(await p.PublishOnce(default));
        await h.W(tx => tx.Sessions.Upsert(new SessionRecord("s1", "1.2.3.4:5", "76561198000000001", "hub-1", "", "", "open", h.D.Clock.GetUtcNow(), h.D.Clock.GetUtcNow(), h.D.Clock.GetUtcNow())));
        Assert.True(await p.PublishOnce(default));

        Assert.Equal(["0 playing", "1 playing"], control.ServerInfos.Select(i => i.Name));
    }
}
