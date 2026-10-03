using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Data;
using SourceSharp.Host.Testing;
using TF2.Descent.Service.Tests.Ledger;
using TF2.Descent.Service.Travel;

namespace TF2.Descent.Service.Tests.Travel;

/// <summary>D-H18: a player who leaves the party keeps their levels and teleporter on copies of their own.</summary>
public class LevelForkFacts
{
    sealed class World(LedgerHarness h, KeyValueStore store, string ann, string bo) : IAsyncDisposable
    {
        public LedgerHarness H => h;
        public KeyValueStore Store => store;
        public string Ann => ann; // leaves
        public string Bo => bo;   // stays
        public Task<IReadOnlyList<(string From, string To)>> Leave() => h.W(tx => LevelForks.ForLeaver(tx, store, ann, [bo], default));
        public async ValueTask DisposeAsync() { await store.DisposeAsync(); await h.DisposeAsync(); }
    }

    /// <summary>Ann and Bo both claim L3 at depth 3; the harness's level instance runs it.</summary>
    static async Task<World> Start()
    {
        var h = await LedgerHarness.Create();
        var ann = await h.Character("Ann");
        var bo = await h.Character("Bo", account: "76561198000000002");
        await h.W(tx => tx.Instances.Update(h.Level.Id, r => r with { LevelHash = "L3", LevelId = "L3" }));
        await h.W(tx => tx.LevelClaims.Set(ann, 3, "L3"));
        await h.W(tx => tx.LevelClaims.Set(bo, 3, "L3"));
        return new World(h, new KeyValueStore(new StoreOptions { Path = ":memory:" }), ann, bo);
    }

    static async Task<string> Claim(World w, string who, int depth) => (await w.H.R(tx => tx.LevelClaims.Get(who, depth)))!.LevelId;

    [Fact]
    public async Task The_leaver_gets_a_copy_of_a_level_shared_with_the_party()
    {
        await using var w = await Start();
        await w.Leave();
        var mine = await Claim(w, w.Ann, 3);
        Assert.NotEqual("L3", mine);
        Assert.Equal("L3", LevelIds.HashOf(mine));
    }

    [Fact]
    public async Task The_party_keeps_the_original()
    {
        await using var w = await Start();
        await w.Leave();
        Assert.Equal("L3", await Claim(w, w.Bo, 3));
    }

    [Fact]
    public async Task A_level_only_the_leaver_claims_is_not_copied()
    {
        await using var w = await Start();
        await w.H.W(tx => tx.LevelClaims.Set(w.Ann, 4, "L4"));
        await w.Leave();
        Assert.Equal("L4", await Claim(w, w.Ann, 4));
    }

    [Fact]
    public async Task The_leavers_teleporter_moves_to_the_copy_and_the_partys_stays()
    {
        await using var w = await Start();
        await w.H.W(tx => tx.Anchors.Set(new AnchorRecord(w.Ann, "L3", 3, 1, 2, 3, 0, DateTimeOffset.UtcNow)));
        await w.H.W(tx => tx.Anchors.Set(new AnchorRecord(w.Bo, "L3", 3, 4, 5, 6, 0, DateTimeOffset.UtcNow)));
        await w.Leave();
        Assert.Equal((await Claim(w, w.Ann, 3), "L3"), ((await w.H.R(tx => tx.Anchors.Get(w.Ann)))!.LevelId, (await w.H.R(tx => tx.Anchors.Get(w.Bo)))!.LevelId));
    }

    [Fact]
    public async Task The_copy_starts_with_the_levels_store_data_and_the_original_keeps_its_own()
    {
        await using var w = await Start();
        await w.Store.Set(new StoreOwner(StoreScope.Level, "L3"), "door", [1], null);
        await w.Leave();
        await w.Store.Set(new StoreOwner(StoreScope.Level, "L3"), "door", [2], null);
        Assert.Equal([1], (await w.Store.Get(new StoreOwner(StoreScope.Level, await Claim(w, w.Ann, 3)), "door"))!.Value);
    }

    [Fact]
    public async Task The_leavers_corpse_waits_for_the_copy_with_its_australium()
    {
        await using var w = await Start();
        var item = await w.H.W(tx => tx.Items.Mint(TestData.Item(), OwnerKind.Corpse, w.Ann, w.H.Level.Id, 0, "t", null));
        await w.H.W(tx => tx.Characters.AddAustralium(w.Ann, 50, "kill", null));
        await w.H.W(tx => tx.Characters.AddAustralium(w.Ann, -50, "death", $"corpse:{w.H.Level.Id}"));
        await w.Leave();
        var pending = LevelForks.Pending(await Claim(w, w.Ann, 3));
        var moved = (await w.H.R(tx => tx.Items.Get(item.Id)))!;
        Assert.Equal((OwnerKind.Corpse, w.Ann, pending), (moved.OwnerKind, moved.OwnerId, moved.InstanceId));
        var ledger = await w.H.R(tx => tx.Characters.AustraliumLedger(w.Ann));
        Assert.Equal((0L, -50L), (ledger.Where(e => e.Ref == $"corpse:{w.H.Level.Id}").Sum(e => e.Delta), ledger.Where(e => e.Ref == $"corpse:{pending}").Sum(e => e.Delta)));
    }

    [Fact]
    public async Task A_party_mates_corpse_stays_on_the_original()
    {
        await using var w = await Start();
        var item = await w.H.W(tx => tx.Items.Mint(TestData.Item(), OwnerKind.Corpse, w.Bo, w.H.Level.Id, 0, "t", null));
        await w.Leave();
        Assert.Equal(w.H.Level.Id, (await w.H.R(tx => tx.Items.Get(item.Id)))!.InstanceId);
    }

    [Fact]
    public async Task The_copys_first_instance_takes_over_the_waiting_corpse()
    {
        await using var w = await Start();
        var item = await w.H.W(tx => tx.Items.Mint(TestData.Item(), OwnerKind.Corpse, w.Ann, w.H.Level.Id, 0, "t", null));
        await w.Leave();
        var fork = await Claim(w, w.Ann, 3);
        var fresh = await w.H.AddInstance("lvl-fork", InstanceKind.Level, 3);
        fresh = await w.H.W(tx => tx.Instances.Update(fresh.Id, r => r with { LevelHash = "L3", LevelId = fork }));
        Assert.Equal(1, await w.H.W(tx => LevelForks.AdoptCorpses(tx, fresh)));
        Assert.Equal("lvl-fork", (await w.H.R(tx => tx.Items.Get(item.Id)))!.InstanceId);
    }

    [Fact]
    public async Task A_copy_that_ends_unplayed_sends_its_waiting_corpse_to_lost_and_found()
    {
        await using var w = await Start();
        var item = await w.H.W(tx => tx.Items.Mint(TestData.Item(), OwnerKind.Corpse, w.Ann, w.H.Level.Id, 0, "t", null));
        await w.Leave();
        var fork = await Claim(w, w.Ann, 3); // read first: a read inside the :memory: writer would wait behind it
        await w.H.W(tx => LevelForks.EndPending(tx, fork));
        var after = (await w.H.R(tx => tx.Items.Get(item.Id)))!;
        Assert.Equal((OwnerKind.LostAndFound, w.Ann), (after.OwnerKind, after.OwnerId));
        Assert.Single(await w.H.R(tx => tx.LostAndFound.ForCharacter(w.Ann)));
    }

    [Fact]
    public async Task A_corpse_through_fork_and_adoption_keeps_its_count_and_australium()
    {
        await using var w = await Start();
        for (var i = 0; i < 3; i++) await w.H.W(tx => tx.Items.Mint(TestData.Item(), OwnerKind.Corpse, w.Ann, w.H.Level.Id, i, "t", null));
        await w.H.W(tx => tx.Characters.AddAustralium(w.Ann, 70, "kill", null));
        await w.H.W(tx => tx.Characters.AddAustralium(w.Ann, -70, "death", $"corpse:{w.H.Level.Id}"));
        async Task<(int Live, long Sum, long Cached)> Count() => (
            await w.H.R(tx => tx.Items.Count(new ItemQuery(OwnerId: w.Ann))),
            await w.H.R(tx => tx.Characters.AustraliumLedgerSum(w.Ann)),
            (await w.H.R(tx => tx.Characters.Get(w.Ann)))!.Australium);
        var before = await Count();
        await w.Leave();
        var fresh = await w.H.AddInstance("lvl-fork", InstanceKind.Level, 3);
        var fork = await Claim(w, w.Ann, 3);
        fresh = await w.H.W(tx => tx.Instances.Update(fresh.Id, r => r with { LevelHash = "L3", LevelId = fork }));
        await w.H.W(tx => LevelForks.AdoptCorpses(tx, fresh));
        Assert.Equal((3, before), (await w.H.R(tx => tx.Items.Count(new ItemQuery(OwnerKind: OwnerKind.Corpse, InstanceId: "lvl-fork"))), await Count()));
    }

    [Fact]
    public async Task Leaving_writes_one_audit_with_the_copies()
    {
        await using var w = await Start();
        var forks = await w.Leave();
        Assert.Single(forks);
        Assert.Single(await w.H.R(tx => tx.Audit.List(new AuditQuery(Action: LevelForks.ForkedAction, Target: w.Ann))));
    }

    [Fact]
    public async Task The_last_member_leaving_copies_nothing()
    {
        await using var w = await Start();
        var forks = await w.H.W(tx => LevelForks.ForLeaver(tx, w.Store, w.Ann, [], default));
        Assert.Empty(forks);
        Assert.Equal("L3", await Claim(w, w.Ann, 3));
    }
}
