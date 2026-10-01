using System.Diagnostics;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Testing;

namespace SourceSharp.Host.Sdk.Tests;

/// <summary>The reserve cache of §5.2a through the SDK, against the real ledger.</summary>
public class ReserveFacts
{
    const ulong Seed = 4321; // SdkHarness.Instance rows
    const int Depth = 3;

    sealed record Level(SdkHarness H, HostSdk Hub, HostSdk Sdk, HostCharacter Character, ICharacterLease Lease);

    static async Task<Level> LevelWithLease(SdkHarness h, HostSdkOptions? o = null)
    {
        await h.Instance("hub-1", InstanceKind.Hub);
        await h.Instance("lvl-1", InstanceKind.Level, Depth);
        var hub = h.Sdk("hub-1");
        var c = (await hub.Pumped(hub.Characters.Create("76561198000000001", "scout", "Ann"))).Value;
        var sdk = h.Sdk("lvl-1", o);
        var lease = (await sdk.Pumped(sdk.Characters.Lease(c.Id))).Value;
        return new Level(h, hub, sdk, c, lease);
    }

    static async Task<IReserve> Filled(Level l)
    {
        var r = l.Sdk.Items.OpenReserve(l.Lease);
        await l.Sdk.PumpUntil(() => Enumerable.Range(0, 5).All(t => r.Count(t) == r.Size(t)), what: "the reserve filled");
        return r;
    }

    static uint KillFor(FakeGameRules rules, int tier, uint from = 0)
    {
        for (var k = from; ; k++)
        {
            var roll = rules.KillRoll(Seed, k, "heavy", Depth, 1);
            if (roll.Drop && roll.Tier == tier) return k;
        }
    }

    static Task<int> LiveReserve(SdkHarness h, string characterId) =>
        h.Data.ReadAsync((tx, _) => tx.Items.Count(new ItemQuery(OwnerKind: OwnerKind.Reserve, OwnerId: characterId)));

    [Fact]
    public async Task Opening_a_reserve_takes_the_configured_size_of_every_tier()
    {
        await using var h = await SdkHarness.Start();
        var l = await LevelWithLease(h);
        var r = await Filled(l);
        Assert.Equal([12, 12, 6, 2, 0], Enumerable.Range(0, 5).Select(r.Count));
        Assert.Equal(32, await LiveReserve(h, l.Character.Id));
        Assert.Equal(4, l.Sdk.Metrics.ReserveRefills); // one TakeReserve per tier with a size
    }

    [Fact]
    public async Task Reserve_items_land_in_the_queues_only_inside_Pump()
    {
        await using var h = await SdkHarness.Start();
        var l = await LevelWithLease(h);
        var r = l.Sdk.Items.OpenReserve(l.Lease);
        await Pumping.Until(() => LiveReserve(h, l.Character.Id).Result == 32, what: "minted at the service");
        await Task.Delay(100);
        Assert.Equal(0, r.Count(0));
        await l.Sdk.PumpUntil(() => r.Count(0) == 12, what: "delivered by Pump");
    }

    [Fact]
    public async Task NextForTier_pops_without_the_network()
    {
        await using var h = await SdkHarness.Start();
        var l = await LevelWithLease(h);
        var r = await Filled(l);
        h.Proxy.Stall(); // any call that touches the network now hangs until its deadline
        var sw = Stopwatch.StartNew();
        var items = Enumerable.Range(0, 12).Select(_ => r.NextForTier(0)).ToList();
        sw.Stop();
        Assert.All(items, Assert.NotNull);
        Assert.Equal(12, items.Select(i => i!.Id).Distinct().Count());
        Assert.True(sw.ElapsedMilliseconds < 500, $"12 pops took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task A_tier_below_its_low_water_mark_is_refilled_in_the_background()
    {
        await using var h = await SdkHarness.Start();
        var l = await LevelWithLease(h);
        var r = await Filled(l);
        var refills = l.Sdk.Metrics.ReserveRefills;
        // Kills: each popped item is revealed, as the game does, so the service's count of this
        // character's reserve falls with the cache's (an unrevealed pop still counts there).
        var kill = 0u;
        for (var i = 0; i < 8; i++) // 12 → 4: not yet below a third
        {
            var item = r.NextForTier(0)!;
            kill = KillFor(h.Rules, 0, kill + 1);
            Assert.True((await l.Sdk.Pumped(r.Reveal(item, kill, "heavy"))).Ok);
        }
        Assert.Equal(refills, l.Sdk.Metrics.ReserveRefills);
        Assert.NotNull(r.NextForTier(0)); // 3 < 4
        Assert.Equal(refills + 1, l.Sdk.Metrics.ReserveRefills);
        // Size 12 less the one popped and not yet revealed: the service mints 8, the cache holds 11.
        await l.Sdk.PumpUntil(() => r.Count(0) == 11 && !r.Refilling(0), what: "refilled");
        Assert.Equal(12, await h.Data.ReadAsync((tx, _) => tx.Items.Count(new ItemQuery(OwnerKind: OwnerKind.Reserve, OwnerId: l.Character.Id, Rarity: 0))));
    }

    [Fact]
    public async Task An_empty_tier_is_a_miss_and_its_kill_falls_back_to_MintDrops()
    {
        await using var h = await SdkHarness.Start();
        var o = SdkHarness.FastOptions();
        o.ReserveSizes = [12, 12, 6, 0, 0]; // Unusual is never reserved here
        var l = await LevelWithLease(h, o);
        var r = await Filled(l);
        Assert.Null(r.NextForTier(3));
        Assert.Equal(1, l.Sdk.Metrics.ReserveMisses);
        var kill = KillFor(h.Rules, 3);
        var drops = await l.Sdk.Pumped(l.Sdk.Items.MintDrops(l.Lease, kill, "heavy", 3));
        var item = Assert.Single(drops.Value);
        Assert.Equal((HostOwnerKind.World, 3), (item.Owner, item.Tier));
        Assert.Equal(1, l.Sdk.Metrics.ReserveFallbacks);
    }

    [Fact]
    public async Task A_boss_kill_mints_synchronously_and_is_counted_apart()
    {
        await using var h = await SdkHarness.Start();
        var l = await LevelWithLease(h);
        var drops = await l.Sdk.Pumped(l.Sdk.Items.MintDrops(l.Lease, 7, "tank_boss", -1));
        Assert.Equal(2, drops.Value.Count);
        Assert.Equal((1L, 0L), (l.Sdk.Metrics.BossMints, l.Sdk.Metrics.ReserveFallbacks));
    }

    [Fact]
    public async Task Reveal_moves_the_popped_item_into_the_world()
    {
        await using var h = await SdkHarness.Start();
        var l = await LevelWithLease(h);
        var r = await Filled(l);
        var kill = KillFor(h.Rules, 0);
        var item = r.NextForTier(0)!;
        var revealed = await l.Sdk.Pumped(r.Reveal(item, kill, "heavy"));
        Assert.True(revealed.Ok, revealed.ToString());
        var row = await h.Data.ReadAsync((tx, _) => tx.Items.Get(item.Id));
        Assert.Equal((OwnerKind.World, "lvl-1"), (row!.OwnerKind, row.InstanceId));
    }

    [Fact]
    public async Task A_reveal_the_kill_roll_does_not_allow_is_a_typed_forged_reveal()
    {
        await using var h = await SdkHarness.Start();
        var l = await LevelWithLease(h);
        var r = await Filled(l);
        var item = r.NextForTier(0)!;
        var wrongTier = KillFor(h.Rules, 1);
        var revealed = await l.Sdk.Pumped(r.Reveal(item, wrongTier, "heavy"));
        Assert.Equal(HostError.ForgedReveal, revealed.Error);
    }

    [Fact]
    public async Task A_reveal_is_retried_with_the_same_request_id_until_the_host_answers()
    {
        await using var h = await SdkHarness.Start();
        var o = SdkHarness.FastOptions();
        o.RpcAttempts = 1;
        o.RpcTimeout = TimeSpan.FromMilliseconds(300);
        o.KeepAlivePingDelay = TimeSpan.FromSeconds(1);
        o.KeepAlivePingTimeout = TimeSpan.FromSeconds(1);
        var l = await LevelWithLease(h, o);
        var r = await Filled(l);
        var kill = KillFor(h.Rules, 0);
        var item = r.NextForTier(0)!;
        // A partition (not a closed stream, which the service treats as a crash): the connection
        // swallows everything until the keepalive drops it and a new one gets through.
        h.Proxy.Stall();
        var call = r.Reveal(item, kill, "heavy");
        await Pumping.Until(() => l.Sdk.Metrics.RevealRetries >= 2, what: "two reveal retries");
        var revealed = await l.Sdk.Pumped(call);
        Assert.True(revealed.Ok, revealed.ToString());
        var events = await h.Data.ReadAsync((tx, _) => tx.Items.Events(item.Id));
        Assert.Single(events, e => e.Kind.StartsWith("reveal:"));
    }

    [Fact]
    public async Task A_level_jump_replaces_the_stale_tiers_with_fresh_items()
    {
        await using var h = await SdkHarness.Start();
        var l = await LevelWithLease(h);
        var r = (Reserve)await Filled(l);
        var before = r.Peek(0);
        var up = FakeGameRules.SheetCodec.Write(new FakeGameRules.Sheet("scout", "Ann", 3, 500, 0, false, false));
        var cp = await l.Sdk.Pumped(l.Lease.Checkpoint(new OpaquePayload(up.Data, 1)));
        Assert.Equal([0, 1, 2, 3], cp.Value.StaleReserveTiers);
        await l.Sdk.PumpUntil(() => r.Count(0) == 12 && !r.Refilling(0), what: "tier 0 refilled");
        var after = r.Peek(0);
        Assert.Empty(before.Intersect(after));
        var rows = await h.Data.ReadAsync((tx, _) => tx.Items.Query(new ItemQuery(OwnerKind: OwnerKind.Reserve, OwnerId: l.Character.Id, Take: 100)));
        Assert.All(rows.Where(i => after.Contains(i.Id)), i => Assert.Equal(3, i.RolledForLevel));
    }

    [Fact]
    public async Task Releasing_the_lease_closes_the_reserve_and_the_service_sweeps_it()
    {
        await using var h = await SdkHarness.Start();
        var l = await LevelWithLease(h);
        var r = await Filled(l);
        Assert.True((await l.Sdk.Pumped(l.Lease.Release())).Ok);
        Assert.False(r.IsOpen);
        Assert.Null(r.NextForTier(0));
        Assert.Equal(0, await LiveReserve(h, l.Character.Id));
    }

    [Fact]
    public async Task A_lost_lease_closes_the_reserve()
    {
        await using var h = await SdkHarness.Start();
        var l = await LevelWithLease(h);
        var r = await Filled(l);
        await l.Sdk.PumpUntil(() => h.Streams.IsOpen("lvl-1"), what: "the stream");
        await h.Streams.Send("lvl-1", new SourceSharp.Host.Proto.ServerCommand
        {
            LeaseRevoked = new SourceSharp.Host.Proto.LeaseRevoked { CharacterId = l.Character.Id, Reason = "admin" },
        });
        await l.Sdk.PumpUntil(() => !r.IsOpen, what: "the reserve closed");
        Assert.Null(r.NextForTier(0));
    }

    [Fact]
    public async Task An_unreachable_host_mints_nothing()
    {
        await using var h = await SdkHarness.Start();
        var l = await LevelWithLease(h);
        h.Proxy.Block();
        var before = await h.Data.ReadAsync((tx, _) => tx.Items.Count(new ItemQuery()));
        var r = l.Sdk.Items.OpenReserve(l.Lease);
        var drops = await l.Sdk.Pumped(l.Sdk.Items.MintDrops(l.Lease, KillFor(h.Rules, 0), "heavy", 0));
        Assert.Equal(HostError.Unreachable, drops.Error);
        await l.Sdk.PumpUntil(() => !r.Refilling(0), what: "the refill gave up");
        Assert.Equal(0, r.Count(0));
        Assert.Null(r.NextForTier(0));
        Assert.Equal(before, await h.Data.ReadAsync((tx, _) => tx.Items.Count(new ItemQuery())));
    }
}
