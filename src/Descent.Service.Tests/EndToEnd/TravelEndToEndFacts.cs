using Microsoft.Extensions.DependencyInjection;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.FakeGame.Control;
using Client = SourceSharp.Host.FakeClient.FakeClient;

namespace Descent.Service.Tests.EndToEnd;

/// <summary>
/// Plan §10 (H7) as in-process facts: the real service, gateway and travel with fake game pods and
/// toy clients. Every client only ever talks to the gateway's public endpoint.
/// </summary>
public class TravelEndToEndFacts
{
    const ulong Ann = 76561198000000001, Bob = 76561198000000002, Cid = 76561198000000003;

    sealed record Hero(ulong SteamId, Client Client, string CharacterId);

    static async Task<(E2eWorld W, Hero A, Hero B, string Party)> PartyOnTheHub(Dictionary<string, string?>? settings = null)
    {
        var w = await E2eWorld.Start(settings);
        await w.SeedLevels(1, "L1-a", "L1-b");
        await w.SeedLevels(2, "L2-a");
        var (ca, a) = await w.Joined(Ann);
        var (cb, b) = await w.Joined(Bob);
        var party = await (await w.HubControl()).PartyAsync(new PartyRequest { Steamids = { Ann.ToString(), Bob.ToString() } });
        Assert.True(party.Ok, party.Reason);
        return (w, new Hero(Ann, ca, a), new Hero(Bob, cb, b), party.PartyId);
    }

    /// <summary>A trip asked of <paramref name="from"/>'s fake by the leader; waits until every hero is on the target and leased there.</summary>
    static async Task<InstanceRecord> Trip(E2eWorld w, string from, Func<FakeGameControl.FakeGameControlClient, Task<TravelReply>> ask, params Hero[] heroes)
    {
        var reply = await ask(w.Control(from));
        Assert.True(reply.Ok, $"{reply.State} {reply.Reason}");
        Assert.Equal("started", reply.State);
        var target = reply.InstanceId;
        foreach (var h in heroes)
        {
            if (!await h.Client.WaitOnInstanceAsync(target, E2eWorld.T))
                Assert.Fail($"{h.SteamId} never landed on {target}: {string.Join(",", h.Client.Steps.TakeLast(6))}; target {(await w.Instance(target))?.State}; "
                    + $"source events: {string.Join(" | ", w.Pods.Game(from)?.Server.Events.TakeLast(10) ?? [])}; hops {w.Hops.Seen.Count}; "
                    + $"waiting {w.Travel.Waiting}; stream open {w.Service.Services.GetRequiredService<Descent.Service.Api.InstanceStreams>().IsOpen(from)}; "
                    + $"sdk connected {w.Pods.Game(from)?.Server.Sdk.Session.Connected} err {w.Pods.Game(from)?.Server.Sdk.Session.LastStreamError} "
                    + $"reconnects {w.Pods.Game(from)?.Server.Sdk.Metrics.Reconnects} failures {w.Pods.Game(from)?.Server.Sdk.Metrics.ConnectFailures} acked {w.Pods.Game(from)?.Server.Sdk.Metrics.HeartbeatsAcked}; audit {string.Join(",", (await w.Data.ReadAsync((tx, _) => tx.Audit.List(new AuditQuery(Target: target)))).Select(x => x.Action))}");
            await w.Until(async () => (await w.Data.ReadAsync((tx, _) => tx.Leases.Get(h.CharacterId)))?.InstanceId == target, $"{h.SteamId} leased on {target}");
        }
        return (await w.Instance(target))!;
    }

    static Task<InstanceRecord> Descend(E2eWorld w, string from, Hero leader, int depth, string party, params Hero[] heroes) =>
        Trip(w, from, c => c.DescendAsync(new TravelRequest { Steamid = leader.SteamId.ToString(), Depth = depth, PartyId = party }).ResponseAsync, heroes);

    /// <summary>Kills until the hero has picked up <paramref name="count"/> items on this level.</summary>
    static async Task Loot(E2eWorld w, string level, Hero hero, int count)
    {
        var c = w.Control(level);
        var picked = 0;
        for (var i = 0; i < 200 && picked < count; i++)
        {
            var k = await c.KillAsync(new KillRequest { Steamid = hero.SteamId.ToString(), Robot = "heavy" });
            Assert.True(k.Ok, k.Reason);
            foreach (var id in k.ItemIds)
            {
                var p = await c.PickupAsync(new ItemRequest { Steamid = hero.SteamId.ToString(), ItemId = id });
                Assert.True(p.Ok, p.Reason);
                picked++;
            }
        }
        Assert.Equal(count, picked);
    }

    /// <summary>Each hero's backpack as one comparable string: "character: item,item; ...".</summary>
    static async Task<string> Inventories(E2eWorld w, params Hero[] heroes)
    {
        var parts = new List<string>();
        foreach (var h in heroes) parts.Add($"{h.CharacterId}: {string.Join(",", (await w.Backpack(h.CharacterId)).Select(i => i.Id).Order())}");
        return string.Join("; ", parts);
    }

    [Fact]
    public async Task A_party_descends_one_two_town_and_back_with_inventories_and_the_live_count_intact_across_every_hop()
    {
        var (w, a, b, party) = await PartyOnTheHub();
        await using var _ = w;
        var hub = (await w.Hub())!;

        var l1 = await Descend(w, hub.Id, a, 1, party, a, b);
        await Loot(w, l1.Id, a, 2);
        await Loot(w, l1.Id, b, 1);
        Assert.All(w.Hops.Seen, s => Assert.False(s.LeaseHeldBySource)); // PrepareHop: checkpoint + release before HopReady

        var before = (Inventories: await Inventories(w, a, b), Live: await w.LiveOutsideReserves());
        Assert.Equal(3, (await w.Backpack(a.CharacterId)).Count + (await w.Backpack(b.CharacterId)).Count);

        var l2 = await Descend(w, l1.Id, a, 2, party, a, b);
        Assert.Equal((2, "L2-a"), (l2.Depth, l2.LevelHash));
        Assert.Equal(before, (await Inventories(w, a, b), await w.LiveOutsideReserves()));

        var town = await Trip(w, l2.Id, c => c.TownPortalAsync(new TravelRequest { Steamid = a.SteamId.ToString() }).ResponseAsync, a, b);
        Assert.Equal(hub.Id, town.Id);
        Assert.Equal(before, (await Inventories(w, a, b), await w.LiveOutsideReserves()));

        var back = await Descend(w, hub.Id, a, 1, party, a, b);
        Assert.Equal(l1.Id, back.Id); // the level stayed live; the party's claim leads back to it
        Assert.Equal(before, (await Inventories(w, a, b), await w.LiveOutsideReserves()));

        Assert.Equal(8, w.Hops.Seen.Count); // four trips, two heroes
        Assert.All(w.Hops.Seen, s => Assert.False(s.LeaseHeldBySource));
        Assert.Equal([a.Client.LocalEndPoint.ToString()], (await w.Data.ReadAsync((tx, _) => tx.Sessions.ForSteamId(Ann.ToString()))).Select(s => s.ClientAddr).Distinct());
    }

    [Fact]
    public async Task A_party_returning_to_a_depth_in_the_same_session_gets_the_same_level_even_after_it_was_reaped()
    {
        var (w, a, b, party) = await PartyOnTheHub(new() { ["Instances:EmptyGrace"] = "00:00:01" });
        await using var _ = w;
        var hub = (await w.Hub())!;
        var first = await Descend(w, hub.Id, a, 1, party, a, b);
        await Trip(w, first.Id, c => c.TownPortalAsync(new TravelRequest { Steamid = a.SteamId.ToString() }).ResponseAsync, a, b);
        await w.Until(async () => (await w.Instance(first.Id))!.Terminal, "the empty level reaped");
        var again = await Descend(w, hub.Id, a, 1, party, a, b);
        Assert.NotEqual(first.Id, again.Id);                 // a new instance...
        Assert.Equal(first.LevelHash, again.LevelHash);      // ...of the same level (D-H12)
    }

    [Fact]
    public async Task A_solo_player_asking_for_the_same_depth_gets_a_different_level()
    {
        var (w, a, b, party) = await PartyOnTheHub();
        await using var _ = w;
        var hub = (await w.Hub())!;
        var partyLevel = await Descend(w, hub.Id, a, 1, party, a, b);
        var (cc, c) = await w.Joined(Cid);
        var solo = await Descend(w, hub.Id, new Hero(Cid, cc, c), 1, "", new Hero(Cid, cc, c));
        Assert.NotEqual(partyLevel.LevelHash, solo.LevelHash);
        Assert.Equal(["L1-a", "L1-b"], new[] { partyLevel.LevelHash, solo.LevelHash }.Order());
    }

    [Fact]
    public async Task A_dead_heros_items_go_to_the_corpse_then_to_lost_and_found_at_reap_and_reclaim_pays_the_fee()
    {
        var (w, a, b, party) = await PartyOnTheHub(new() { ["Instances:EmptyGrace"] = "00:00:01", ["Instances:CorpseGrace"] = "00:00:02" });
        await using var _ = w;
        var hub = (await w.Hub())!;
        var level = await Descend(w, hub.Id, a, 1, party, a, b);
        await Loot(w, level.Id, a, 2);
        var carried = (await w.Backpack(a.CharacterId)).Select(i => i.Id).Order().ToList();

        var died = await w.Control(level.Id).DieAsync(new PlayerRequest { Steamid = a.SteamId.ToString() });
        Assert.True(died.Ok, died.Reason);
        // Within the one RPC: everything carried is Corpse-owned.
        var owners = await w.Data.ReadAsync(async (tx, _) =>
        {
            var l = new List<OwnerKind>();
            foreach (var id in carried) l.Add((await tx.Items.Get(id))!.OwnerKind);
            return l;
        });
        Assert.All(owners, o => Assert.Equal(OwnerKind.Corpse, o));
        Assert.Equal(carried, died.ItemIds.Order());

        await Trip(w, level.Id, c => c.TownPortalAsync(new TravelRequest { Steamid = a.SteamId.ToString() }).ResponseAsync, a, b);
        await w.Until(async () => (await w.Instance(level.Id))!.Terminal, "the level reaped");
        var pod = (await w.Instance(level.Id))!.PodName!;
        await w.Until(() => Task.FromResult(w.Pods.Get(pod) is null), "the level's pod deleted");

        var hubControl = await w.HubControl();
        var entries = await hubControl.LostAndFoundAsync(new PlayerRequest { Steamid = a.SteamId.ToString() });
        var lf = await w.Data.ReadAsync(async (tx, _) => (await tx.LostAndFound.ForCharacter(a.CharacterId)).ToList());
        Assert.Equal(carried, lf.Select(e => e.ItemId).Order());
        Assert.Equal(lf.Select(e => e.Id).Order(), entries.ItemIds.Order());

        await w.Data.WriteAsync((tx, _) => tx.Characters.AddAustralium(a.CharacterId, 10_000, "e2e", null));
        var wallet = (await w.Data.ReadAsync((tx, _) => tx.Characters.Get(a.CharacterId)))!.Australium;
        var entry = lf[0];
        var reclaimed = await hubControl.ReclaimAsync(new ItemRequest { Steamid = a.SteamId.ToString(), ItemId = entry.Id });
        Assert.True(reclaimed.Ok, reclaimed.Reason);
        Assert.Equal([entry.ItemId], reclaimed.ItemIds);
        Assert.True(entry.Fee > 0);
        Assert.Equal(wallet - entry.Fee, (await w.Data.ReadAsync((tx, _) => tx.Characters.Get(a.CharacterId)))!.Australium);
    }
}
