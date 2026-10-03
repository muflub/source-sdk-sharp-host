using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Data;
using TF2.Descent.Service.Tests.Ledger;
using TF2.Descent.Service.Workers;

namespace TF2.Descent.Service.Tests.Workers;

/// <summary>D-H17: store data lives as long as its owner in host.db, swept across the two files.</summary>
public class StoreSweepFacts
{
    sealed class World(LedgerHarness h, KeyValueStore store) : IAsyncDisposable
    {
        public LedgerHarness H => h;
        public KeyValueStore Store => store;
        public StoreSweep Sweep { get; } = new(h.D.Data, store);
        public Task Put(StoreScope scope, string id) => store.Set(new StoreOwner(scope, id), "k", [1], null);
        public async Task<bool> Has(StoreScope scope, string id) => await store.Get(new StoreOwner(scope, id), "k") is not null;
        public async ValueTask DisposeAsync() { await store.DisposeAsync(); await h.DisposeAsync(); }
    }

    static async Task<World> Start() => new(await LedgerHarness.Create(), new KeyValueStore(new StoreOptions { Path = ":memory:" }));

    [Fact]
    public async Task An_ended_instances_data_goes_and_a_running_ones_stays()
    {
        await using var w = await Start();
        await w.Put(StoreScope.Instance, w.H.Level.Id);
        await w.Put(StoreScope.Instance, w.H.Hub.Id);
        await w.H.W(tx => tx.Instances.Update(w.H.Level.Id, r => r with { State = InstanceState.Reaped }));
        await w.Sweep.Run(default);
        Assert.Equal((false, true), (await w.Has(StoreScope.Instance, w.H.Level.Id), await w.Has(StoreScope.Instance, w.H.Hub.Id)));
    }

    [Fact]
    public async Task A_level_copy_nothing_claims_or_runs_loses_its_data()
    {
        await using var w = await Start();
        await w.Put(StoreScope.Level, "L1~X");
        await w.Sweep.Run(default);
        Assert.False(await w.Has(StoreScope.Level, "L1~X"));
    }

    [Fact]
    public async Task A_claimed_level_copy_keeps_its_data()
    {
        await using var w = await Start();
        await w.Put(StoreScope.Level, "L1~X");
        await w.H.W(tx => tx.LevelClaims.Set("c1", 1, "L1~X"));
        await w.Sweep.Run(default);
        Assert.True(await w.Has(StoreScope.Level, "L1~X"));
    }

    [Fact]
    public async Task A_running_level_copy_keeps_its_data()
    {
        await using var w = await Start();
        await w.Put(StoreScope.Level, "L1~X");
        await w.H.W(tx => tx.Instances.Update(w.H.Level.Id, r => r with { LevelHash = "L1", LevelId = "L1~X" }));
        await w.Sweep.Run(default);
        Assert.True(await w.Has(StoreScope.Level, "L1~X"));
    }

    [Fact]
    public async Task A_deleted_characters_data_goes_and_a_live_ones_stays()
    {
        await using var w = await Start();
        var gone = await w.H.Character("Gone");
        var kept = await w.H.Character("Kept");
        await w.Put(StoreScope.Character, gone);
        await w.Put(StoreScope.Character, kept);
        await w.H.D.Write(tx => tx.Characters.MarkDeleted(gone));
        await w.Sweep.Run(default);
        Assert.Equal((false, true), (await w.Has(StoreScope.Character, gone), await w.Has(StoreScope.Character, kept)));
    }

    [Fact]
    public async Task Town_data_is_never_swept()
    {
        await using var w = await Start();
        await w.Store.Set(StoreOwner.Town, "k", [1], null);
        await w.Sweep.Run(default);
        Assert.NotNull(await w.Store.Get(StoreOwner.Town, "k"));
    }

    [Fact]
    public async Task A_sweep_that_removed_data_audits_its_counts()
    {
        await using var w = await Start();
        await w.Put(StoreScope.Level, "L1~X");
        await w.Put(StoreScope.Instance, "never-was");
        var r = await w.Sweep.Run(default);
        Assert.Equal(new StoreSweep.Result(Instances: 1, Levels: 1, Characters: 0, Keys: 2), r);
        Assert.Single(await w.H.R(tx => tx.Audit.List(new AuditQuery(Action: StoreSweep.SweptAction))));
    }

    [Fact]
    public async Task A_sweep_that_removed_nothing_writes_no_audit()
    {
        await using var w = await Start();
        await w.Put(StoreScope.Instance, w.H.Hub.Id);
        await w.Sweep.Run(default);
        Assert.Empty(await w.H.R(tx => tx.Audit.List(new AuditQuery(Action: StoreSweep.SweptAction))));
    }
}
