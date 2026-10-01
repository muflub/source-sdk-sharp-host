using SourceSharp.Host.Abstractions;
using SourceSharp.Host.MapPool;

namespace SourceSharp.Host.MapPool.Tests;

/// <summary>§9.3 the pool worker, and the §9.4 facts that need it.</summary>
public sealed class MapPoolWorkerFacts
{
    [Fact]
    public async Task Until_the_rules_module_is_known_the_pool_queues_nothing_and_says_why()
    {
        await using var h = new PoolHarness();
        await h.UploadAndActivate();
        h.Rules.Current = null;
        await h.Worker.PlanAsync();
        Assert.Empty(await h.Jobs());
        Assert.True(h.Worker.Status.WaitingForRules);
        Assert.Equal("waiting for the hub's rules module", h.Worker.Status.Message);
    }

    [Fact]
    public async Task Once_the_rules_module_is_known_the_pool_queues_its_deficit()
    {
        await using var h = new PoolHarness(depths: 3, k: 2);
        await h.UploadAndActivate();
        h.Rules.Current = null;
        await h.Worker.PlanAsync();
        Assert.Empty(await h.Jobs());
        h.Rules.Current = new SourceSharp.Host.Testing.FakeGameRules();
        await h.Worker.PlanAsync();
        Assert.Equal(6, (await h.Jobs(MapJobState.Queued)).Count);
        Assert.False(h.Worker.Status.WaitingForRules);
    }

    [Fact]
    public async Task The_pool_fills_K_ready_levels_per_depth_of_the_assigned_version()
    {
        await using var h = new PoolHarness(depths: 3, k: 2);
        var v1 = await h.UploadAndActivate();
        await h.Fill();
        foreach (var d in new[] { 1, 2, 3 }) Assert.Equal(2, await h.Ready(d, v1.Id));
        Assert.Equal(6, h.Compiler.Links.Count);
    }

    [Fact]
    public async Task A_second_pass_over_a_full_pool_links_nothing()
    {
        await using var h = new PoolHarness(depths: 2, k: 2);
        await h.UploadAndActivate();
        await h.Fill();
        var before = h.Compiler.Links.Count;
        await h.Fill();
        Assert.Equal(4, before);
        Assert.Equal(before, h.Compiler.Links.Count);
    }

    [Fact]
    public async Task A_depth_without_an_assigned_pack_gets_no_levels()
    {
        await using var h = new PoolHarness(depths: 3, k: 1);
        await h.UploadAndActivate(1);
        await h.Fill();
        Assert.Equal((1, 0, 0), (await h.Ready(1), await h.Ready(2), await h.Ready(3)));
        Assert.Equal("no pack assigned", h.Worker.Status.Depths.Single(d => d.Depth == 2).Note);
    }

    static IEnumerable<int> LinkedDepths(PoolHarness h) =>
        h.Events.Where(e => e.StartsWith("link:")).Select(e => int.Parse(e.Split(':')[1]));

    [Fact]
    public async Task Without_demand_the_shallowest_depth_links_first()
    {
        await using var h = new PoolHarness(depths: 3, k: 1, concurrent: 1);
        await h.UploadAndActivate();
        await h.Fill();
        Assert.Equal([1, 2, 3], LinkedDepths(h));
    }

    [Fact]
    public async Task Depth_N_plus_1_of_a_party_on_N_outranks_a_selectable_depth_which_outranks_the_rest()
    {
        await using var h = new PoolHarness(depths: 4, k: 1, concurrent: 1);
        await h.UploadAndActivate();
        h.Demand.Parties.Add(2);       // a party on 2: depth 3 at priority 1
        h.Demand.Selectable.Add(4);    // the church offers 4: priority 2
        await h.Fill();
        Assert.Equal([3, 4, 1, 2], LinkedDepths(h));
    }

    [Fact]
    public async Task Links_run_at_most_ConcurrentLinks_at_once()
    {
        await using var h = new PoolHarness(depths: 3, k: 2, concurrent: 2);
        await h.UploadAndActivate();
        h.Compiler.Gate = new SemaphoreSlim(0);
        await h.Worker.PlanAsync();
        var run = h.Worker.RunPendingAsync();
        while (h.Compiler.Links.Count < 2) await Task.Delay(5);
        await Task.Delay(50);
        Assert.Equal(2, h.Compiler.Links.Count);
        h.Compiler.Gate.Release(100);
        await run;
        Assert.Equal(2, h.Compiler.MaxConcurrent);
        Assert.Equal(6, h.Compiler.Links.Count);
    }

    [Fact]
    public async Task Two_concurrent_requests_get_two_different_levels()
    {
        await using var h = new PoolHarness(depths: 1, k: 2);
        await h.UploadAndActivate();
        await h.Fill();
        var a = h.Worker.RequestLevelAsync(1, "inst-a");
        var b = h.Worker.RequestLevelAsync(1, "inst-b");
        var (ra, rb) = (await a, await b);
        Assert.NotNull(ra.Level);
        Assert.NotNull(rb.Level);
        Assert.NotEqual(ra.Level!.Hash, rb.Level!.Hash);
    }

    [Fact]
    public async Task A_level_is_handed_out_once_ever()
    {
        await using var h = new PoolHarness(depths: 1, k: 1);
        await h.UploadAndActivate();
        await h.Fill();
        var first = await h.Worker.RequestLevelAsync(1, "inst-a");
        Assert.True(first.Ready);
        await h.Worker.RetireLevelAsync(first.Level!.Hash);
        var second = await h.Worker.RequestLevelAsync(1, "inst-b");
        Assert.False(second.Ready);
        Assert.Equal(LevelState.Retired, (await h.Db.Read(tx => tx.Levels.Get(first.Level.Hash)))!.State);
    }

    [Fact]
    public async Task A_handed_out_level_records_its_instance()
    {
        await using var h = new PoolHarness(depths: 1, k: 1);
        await h.UploadAndActivate();
        await h.Fill();
        var r = await h.Worker.RequestLevelAsync(1, "inst-a");
        Assert.Equal((LevelState.HandedOut, "inst-a"), (r.Level!.State, r.Level.HandedTo));
    }

    [Fact]
    public async Task An_empty_depth_answers_waiting_and_queues_a_priority_0_link()
    {
        await using var h = new PoolHarness(depths: 2, k: 1);
        await h.UploadAndActivate();
        var r = await h.Worker.RequestLevelAsync(2, "inst-a");
        Assert.False(r.Ready);
        Assert.Contains("no ready level", r.Waiting);
        var job = Assert.Single(await h.Jobs(MapJobState.Queued));
        Assert.Equal((0, MapPoolWorker.LinkJobKey("descent", 2, (await h.Catalog.MatrixAsync("descent")).Rows[0].PackId, 0)), (job.Priority, job.Key));
    }

    [Fact]
    public async Task The_fallback_link_is_handed_out_once_linked()
    {
        await using var h = new PoolHarness(depths: 2, k: 1);
        await h.UploadAndActivate();
        Assert.False((await h.Worker.RequestLevelAsync(2, "inst-a")).Ready);
        await h.Worker.RunPendingAsync();
        Assert.True((await h.Worker.RequestLevelAsync(2, "inst-a")).Ready);
    }

    [Fact]
    public async Task The_fallback_raises_a_queued_link_of_that_depth_rather_than_adding_one()
    {
        await using var h = new PoolHarness(depths: 2, k: 1);
        await h.UploadAndActivate();
        await h.Worker.PlanAsync();
        Assert.Equal(3, (await h.Jobs(MapJobState.Queued)).Single(j => j.Key.Contains(":2:")).Priority);
        await h.Worker.RequestLevelAsync(2, "inst-a");
        var jobs = await h.Jobs(MapJobState.Queued);
        Assert.Equal(2, jobs.Count);
        Assert.Equal(0, jobs.Single(j => j.Key.Contains(":2:")).Priority);
    }

    [Fact]
    public async Task A_failed_link_is_retried_with_a_fresh_seed()
    {
        await using var h = new PoolHarness(depths: 1, k: 1, concurrent: 1);
        await h.UploadAndActivate();
        ulong? firstSeed = null;
        h.FailWhen = r => { firstSeed ??= r.Seed; return r.Seed == firstSeed; };
        await h.Fill();
        var seeds = h.Compiler.Links.Select(l => l.Seed).ToList();
        Assert.Equal(2, seeds.Count);
        Assert.NotEqual(seeds[0], seeds[1]);
        Assert.Equal(1, await h.Ready(1));
        var failed = await h.Db.Read(tx => tx.Levels.List(state: LevelState.Failed));
        Assert.Equal(seeds[0], Assert.Single(failed).Seed);
    }

    [Fact]
    public async Task A_link_that_fails_LinkAttempts_times_is_failed_with_its_log()
    {
        await using var h = new PoolHarness(depths: 1, k: 1, concurrent: 1);
        await h.UploadAndActivate();
        h.FailWhen = _ => true;
        await h.Fill();
        var job = Assert.Single(await h.Jobs(MapJobState.Failed));
        Assert.Equal(3, job.Attempts);
        Assert.Contains("fake link refused", job.Log);
        Assert.Equal(3, h.Compiler.Links.Select(l => l.Seed).Distinct().Count());
    }

    [Fact]
    public async Task Activating_a_new_version_retargets_the_pool_to_it()
    {
        await using var h = new PoolHarness(depths: 1, k: 2);
        await h.UploadAndActivate();
        await h.Fill();
        var v2 = await h.Upload();
        Assert.Equal(0, await h.Ready(1, v2.Id));
        await h.Catalog.ActivateAsync("descent", [1], v2.Id, "admin");
        await h.Fill();
        Assert.Equal(2, await h.Ready(1, v2.Id));
    }

    [Fact]
    public async Task Drain_hands_out_the_old_versions_levels_until_the_new_ones_exist()
    {
        await using var h = new PoolHarness(depths: 1, k: 2);
        var v1 = await h.UploadAndActivate();
        await h.Fill();
        var v2 = await h.Upload();
        await h.Catalog.ActivateAsync("descent", [1], v2.Id, "admin", policy: OldLevelsPolicy.Drain);
        var r = await h.Worker.RequestLevelAsync(1, "inst-a");
        Assert.Equal(v1.Id, r.Level!.PackId);
    }

    [Fact]
    public async Task Drain_retires_the_old_versions_levels_once_the_new_version_meets_K()
    {
        await using var h = new PoolHarness(depths: 1, k: 2);
        var v1 = await h.UploadAndActivate();
        await h.Fill();
        var v2 = await h.Upload();
        await h.Catalog.ActivateAsync("descent", [1], v2.Id, "admin", policy: OldLevelsPolicy.Drain);
        Assert.Equal(2, await h.Ready(1, v1.Id));
        await h.Fill();
        await h.Worker.PlanAsync();
        Assert.Equal((0, 2), (await h.Ready(1, v1.Id), await h.Ready(1, v2.Id)));
    }

    [Fact]
    public async Task Retire_now_links_the_new_version_at_priority_0()
    {
        await using var h = new PoolHarness(depths: 1, k: 1);
        await h.UploadAndActivate();
        await h.Fill();
        var v2 = await h.Upload();
        await h.Catalog.ActivateAsync("descent", [1], v2.Id, "admin", policy: OldLevelsPolicy.Retire);
        Assert.False((await h.Worker.RequestLevelAsync(1, "inst-a")).Ready);
        Assert.All(await h.Jobs(MapJobState.Queued), j => Assert.Equal(0, j.Priority));
    }

    [Fact]
    public async Task A_queued_link_for_a_version_no_longer_assigned_is_dropped_when_it_runs()
    {
        await using var h = new PoolHarness(depths: 1, k: 1);
        var v1 = await h.UploadAndActivate();
        await h.Worker.PlanAsync();
        var v2 = await h.Upload();
        await h.Catalog.ActivateAsync("descent", [1], v2.Id, "admin");
        await h.Worker.RunPendingAsync();
        Assert.Equal(0, await h.Ready(1, v1.Id));
        Assert.Empty(h.Compiler.Links);
        Assert.Contains((await h.Jobs(MapJobState.Done)), j => j.Log!.StartsWith("dropped"));
    }

    [Fact]
    public async Task Roll_back_relinks_from_the_older_version()
    {
        await using var h = new PoolHarness(depths: 1, k: 1);
        var v1 = await h.UploadAndActivate();
        var v2 = await h.Upload();
        await h.Catalog.ActivateAsync("descent", [1], v2.Id, "admin", policy: OldLevelsPolicy.Retire);
        await h.Fill();
        Assert.Equal(1, await h.Ready(1, v2.Id));
        await h.Catalog.RollBackAsync("descent", [1], v1.Id, "admin", OldLevelsPolicy.Retire);
        h.Events.Clear();
        await h.Fill();
        Assert.Equal(1, await h.Ready(1, v1.Id));
        Assert.Equal(["link:1:crypt/1.roompack"], h.Events);
    }

    [Fact]
    public async Task A_changed_library_source_queues_a_bake_that_runs_before_any_link()
    {
        await using var h = new PoolHarness(depths: 2, k: 1, concurrent: 1);
        await h.UploadAndActivate();
        var lib = Path.Combine(h.Options.MapPool.LibraryPath, "caves");
        Directory.CreateDirectory(lib);
        await File.WriteAllTextAsync(Path.Combine(lib, "rooms.vmf"), "versioninfo {}");
        await h.Fill();
        Assert.Equal("bake:caves", h.Events.First());
        Assert.Equal(3, h.Events.Count);
    }

    [Fact]
    public async Task A_library_whose_version_has_a_pack_is_not_baked()
    {
        await using var h = new PoolHarness(depths: 1, k: 1);
        var lib = Path.Combine(h.Options.MapPool.LibraryPath, "crypt");
        Directory.CreateDirectory(lib);
        await File.WriteAllTextAsync(Path.Combine(lib, "rooms.vmf"), "versioninfo {}");
        var version = MapIdentity.LibraryVersionOf(new LibrarySource("descent", "crypt", lib));
        await h.Db.Write(tx => tx.Library.AddPack(new PackRecord(0, "descent", "crypt", 1, "p", version, "bake", "t", tx.Now, 1, "s", null, null, PackState.Ready)));
        await h.Worker.PlanAsync();
        Assert.DoesNotContain(await h.Jobs(), j => j.Kind == MapJobKind.Bake);
    }

    [Fact]
    public async Task While_a_bake_runs_the_old_packs_levels_keep_handing_out()
    {
        await using var h = new PoolHarness(depths: 1, k: 2, concurrent: 1);
        await h.UploadAndActivate();
        await h.Fill();
        var lib = Path.Combine(h.Options.MapPool.LibraryPath, "crypt");
        Directory.CreateDirectory(lib);
        await File.WriteAllTextAsync(Path.Combine(lib, "rooms.vmf"), "versioninfo { changed }");
        h.Baker.Gate = new SemaphoreSlim(0);
        await h.Worker.PlanAsync();
        var run = h.Worker.RunPendingAsync();
        await h.Baker.Started.Task;
        var r = await h.Worker.RequestLevelAsync(1, "inst-a");
        h.Baker.Gate.Release();
        await run;
        Assert.True(r.Ready);
    }

    [Fact]
    public async Task Reap_retires_a_handed_out_level()
    {
        await using var h = new PoolHarness(depths: 1, k: 1);
        await h.UploadAndActivate();
        await h.Fill();
        var r = await h.Worker.RequestLevelAsync(1, "inst-a");
        await h.Worker.RetireLevelAsync(r.Level!.Hash);
        Assert.Equal(LevelState.Retired, (await h.Db.Read(tx => tx.Levels.Get(r.Level.Hash)))!.State);
    }

    [Fact]
    public async Task Reap_leaves_a_ready_level_ready()
    {
        await using var h = new PoolHarness(depths: 1, k: 1);
        await h.UploadAndActivate();
        await h.Fill();
        var level = (await h.Db.Read(tx => tx.Levels.List(state: LevelState.Ready))).Single();
        await h.Worker.RetireLevelAsync(level.Hash);
        Assert.Equal(LevelState.Ready, (await h.Db.Read(tx => tx.Levels.Get(level.Hash)))!.State);
    }

    [Fact]
    public async Task Gc_keeps_the_newest_10_retired_levels_per_depth_and_deletes_the_rest()
    {
        await using var h = new PoolHarness(depths: 1, k: 1);
        await h.UploadAndActivate();
        var names = new List<string>();
        for (var i = 0; i < 12; i++)
        {
            await h.Fill();
            var r = await h.Worker.RequestLevelAsync(1, $"inst-{i}");
            await h.Worker.RetireLevelAsync(r.Level!.Hash);
            names.Add(MapIdentity.MapName("descent", 1, r.Level.Hash));
            h.Db.Clock.Advance(TimeSpan.FromMinutes(1));
        }
        Assert.All(names, n => Assert.True(h.Storage.LevelExists(n)));
        var deleted = await h.Worker.CollectAsync();
        Assert.Equal(names.Take(2).Order(), deleted.Order());
        Assert.All(names.Skip(2), n => Assert.True(h.Storage.LevelExists(n)));
    }

    [Fact]
    public async Task Gc_deletes_a_failed_levels_files_after_7_days_and_not_before()
    {
        await using var h = new PoolHarness(depths: 1, k: 1);
        await h.UploadAndActivate();
        h.FailWhen = _ => true;
        await h.Fill();
        var failed = (await h.Db.Read(tx => tx.Levels.List(state: LevelState.Failed))).First();
        var name = MapIdentity.MapName("descent", 1, failed.Hash);
        Directory.CreateDirectory(h.Storage.LevelsDirectory);
        await File.WriteAllTextAsync(h.Storage.LevelFile(name, ".bsp.bz2"), "x");
        h.Db.Clock.Advance(TimeSpan.FromDays(6));
        Assert.Empty(await h.Worker.CollectAsync());
        h.Db.Clock.Advance(TimeSpan.FromDays(2));
        Assert.Contains(name, await h.Worker.CollectAsync());
    }

    [Fact]
    public async Task A_job_left_running_by_a_previous_process_is_queued_again()
    {
        await using var h = new PoolHarness(depths: 1, k: 1);
        await h.UploadAndActivate();
        await h.Worker.PlanAsync();
        await h.Db.Write(tx => tx.MapJobs.TakeNext());
        Assert.Single(await h.Jobs(MapJobState.Running));
        Assert.Equal(1, await h.Worker.RequeueOrphansAsync());
        Assert.Single(await h.Jobs(MapJobState.Queued));
    }

    [Fact]
    public async Task The_status_reports_each_depths_target_and_ready_count()
    {
        await using var h = new PoolHarness(depths: 2, k: 2);
        var v1 = await h.UploadAndActivate();
        await h.Fill();
        await h.Worker.PlanAsync();
        var d1 = h.Worker.Status.Depths.Single(d => d.Depth == 1);
        Assert.Equal((v1.Id, 1, 2, 2, 0), (d1.PackId!.Value, d1.PackVersion!.Value, d1.Target, d1.Ready, d1.InFlight));
    }
}
