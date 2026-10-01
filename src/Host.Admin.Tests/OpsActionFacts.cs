using SourceSharp.Host.Abstractions;
using SourceSharp.Host.MapPool;
using SourceSharp.Host.Testing;

namespace SourceSharp.Host.Admin.Tests;

/// <summary>Instances, gateway, pool, packs, modules, reconcile, backups and config (§11).</summary>
public sealed class OpsActionFacts : WorldFacts
{
    [Fact]
    public async Task Drain_goes_to_the_instance_manager_as_the_admin()
    {
        await W.Instance();
        Assert.True((await A.DrainInstance("lvl-1", "maintenance")).Ok);
        Assert.Equal(["drain:lvl-1"], W.Lifecycle.Calls);
        Assert.Equal("admin@localhost", Assert.Single(await W.Audit("instance.draining")).Actor);
    }

    [Fact]
    public async Task Exec_sends_the_command_and_the_manager_audits_it_once()
    {
        await W.Instance();
        Assert.True((await A.Exec("lvl-1", "sv_cheats 1")).Ok);
        Assert.Equal(["exec:lvl-1:sv_cheats 1"], W.Lifecycle.Calls);
        Assert.Single(await W.Audit("instance.exec"));
    }

    [Fact]
    public async Task Exec_to_an_instance_with_no_stream_is_refused()
    {
        await W.Instance();
        W.Lifecycle.HasStream = false;
        Assert.Equal("no_stream", (await A.Exec("lvl-1", "status")).Reason);
    }

    [Fact]
    public async Task Create_hub_lets_the_manager_recreate_hubs()
    {
        Assert.True((await A.CreateHub()).Ok);
        Assert.Equal(["resume_hubs"], W.Lifecycle.Calls);
    }

    [Fact]
    public async Task Delete_pod_deletes_by_name_and_uid()
    {
        var pod = W.Pods.Seed("descent-lvl-1", new Dictionary<string, string> { [GamePodLabels.App] = GamePodLabels.AppValue });
        await W.D.Write(tx => tx.Instances.Add(new InstanceRecord("lvl-1", InstanceKind.Level, InstanceState.Live, 3, null, null, pod.Name, pod.Uid,
            "10.42.0.9", 27015, "hash", null, null, "fake", 1, W.D.Clock.GetUtcNow(), null, null, null, null, null, null, null, 0, 27015)));
        Assert.True((await A.DeletePod("lvl-1")).Ok);
        Assert.Contains(W.Pods.Calls, c => c.Op == "delete" && c.Name == pod.Name && c.Uid == pod.Uid);
        Assert.Single(await W.Audit("instance.delete_pod"));
    }

    [Fact]
    public async Task Close_session_goes_to_the_gateway_and_is_audited()
    {
        Assert.True((await A.CloseSession("s-1")).Ok);
        Assert.Equal(["close:s-1"], W.Gateway.Calls);
        Assert.Equal("admin@localhost", Assert.Single(await W.Audit("session.close")).Actor);
    }

    [Fact]
    public async Task A_gateway_failure_is_refused_unreachable_and_not_audited()
    {
        W.Gateway.Fail = new HttpRequestException("connection refused");
        Assert.Equal("unreachable", (await A.BanAddress("203.0.113.9", null)).Reason);
        Assert.Empty(await W.Audit("gateway.ban_address"));
    }

    [Fact]
    public async Task Release_pin_goes_to_the_gateway()
    {
        Assert.True((await A.ReleasePin(AdminWorld.Steam)).Ok);
        Assert.Equal([$"unpin:{AdminWorld.Steam}"], W.Gateway.Calls);
    }

    Task<LevelRecord> Level(string hash, LevelState state, int depth = 2) => W.D.Write(tx =>
        tx.Levels.Add(new LevelRecord(hash, 1, depth, "crypt", 1, 0, state, tx.Now, state == LevelState.HandedOut ? "lvl-1" : null, 10, null, "fake")));

    [Fact]
    public async Task Regenerate_retires_the_ready_levels_of_the_depth()
    {
        await Level("aa", LevelState.Ready);
        await Level("bb", LevelState.Ready, depth: 3);
        Assert.Equal(1, (await A.RegenerateDepth(2)).Value);
        Assert.Equal(LevelState.Retired, (await W.D.Read(tx => tx.Levels.Get("aa")))!.State);
        Assert.Equal(LevelState.Ready, (await W.D.Read(tx => tx.Levels.Get("bb")))!.State);
    }

    [Fact]
    public async Task Regenerate_never_touches_a_handed_out_level()
    {
        await Level("aa", LevelState.HandedOut);
        await A.RegenerateDepth(2);
        Assert.Equal(LevelState.HandedOut, (await W.D.Read(tx => tx.Levels.Get("aa")))!.State);
    }

    [Fact]
    public async Task Retire_of_a_handed_out_level_is_refused()
    {
        await Level("aa", LevelState.HandedOut);
        Assert.Equal("level_handed_out", (await A.RetireLevel("aa")).Reason);
    }

    [Fact]
    public async Task Delete_level_retires_it_and_deletes_its_files()
    {
        await Level("aa", LevelState.Ready);
        var storage = new LevelStorage(W.Options.MapPool);
        Directory.CreateDirectory(storage.LevelsDirectory);
        var bsp = storage.LevelFile(MapIdentity.MapName("descent", 2, "aa"), ".bsp");
        await File.WriteAllTextAsync(bsp, "x");
        Assert.True((await A.DeleteLevel("aa")).Ok);
        Assert.False(File.Exists(bsp));
        Assert.Equal(LevelState.Retired, (await W.D.Read(tx => tx.Levels.Get("aa")))!.State);
    }

    [Fact]
    public async Task Requeue_queues_a_failed_job_again_at_priority_zero()
    {
        var job = await W.D.Write(async tx =>
        {
            var j = await tx.MapJobs.Enqueue(MapJobKind.Link, "link:descent:2:1:0", 3);
            await tx.MapJobs.TakeNext();
            return await tx.MapJobs.Fail(j.Id, "boom", requeue: false);
        });
        var r = await A.RequeueJob(job.Id);
        Assert.True(r.Ok, r.Message);
        var again = Assert.IsType<MapJob>(r.Value);
        Assert.Equal((MapJobState.Queued, 0, job.Key), (again.State, again.Priority, again.Key));
    }

    [Fact]
    public async Task Set_per_depth_writes_the_runtime_setting()
    {
        Assert.True((await A.SetPerDepth(4, 5)).Ok);
        Assert.Equal("5", (await W.D.Read(tx => tx.Settings.Get("MapPool.PerDepth.4")))!.ValueJson);
    }

    [Fact]
    public async Task Upload_then_activate_assigns_the_version_and_audits_both()
    {
        using var s = new MemoryStream(FakePack.Bytes("crypt", ["start", "hall", "stairs"]));
        var up = await A.UploadPack("descent", "crypt", s, "{}", "first");
        Assert.True(up.Ok, up.Message);
        var pack = Assert.IsType<PackRecord>(up.Value);
        Assert.True((await A.ActivatePack("descent", [1, 2], pack.Id, true, OldLevelsPolicy.Drain)).Ok);
        Assert.Equal(pack.Id, (await W.D.Read(tx => tx.Library.Assignment("descent", 2)))!.PackId);
        Assert.Single(await W.Audit("pack.upload"));
        Assert.Equal("admin@localhost", Assert.Single(await W.Audit("pack.activate")).Actor);
    }

    [Fact]
    public async Task A_rejected_upload_is_refused_with_the_report()
    {
        using var s = new MemoryStream(FakePack.Bytes("crypt", ["start", "broken"]));
        var r = await A.UploadPack("descent", "crypt", s, "{}", null);
        Assert.Equal("rejected", r.Reason);
        Assert.Contains("lint", r.Message);
    }

    [Fact]
    public async Task Retire_of_an_assigned_pack_is_refused()
    {
        using var s = new MemoryStream(FakePack.Bytes("crypt", ["start", "stairs"]));
        var pack = (PackRecord)(await A.UploadPack("descent", "crypt", s, "{}", null)).Value!;
        await A.ActivatePack("descent", [1], pack.Id, true, OldLevelsPolicy.Drain);
        Assert.Equal("pack_assigned", (await A.RetirePack(pack.Id)).Reason);
    }

    [Fact]
    public async Task Rebake_queues_a_bake_job_for_the_library()
    {
        var r = await A.RebakePack("descent", "crypt");
        var job = Assert.IsType<MapJob>(r.Value);
        Assert.Equal(MapJobKind.Bake, job.Kind);
        Assert.StartsWith("bake:descent:crypt:", job.Key);
    }

    Task<RulesModuleRecord> Module(string sha) => W.D.Write(tx => tx.Modules.Add(new RulesModuleRecord(sha, "Descent.Rules", "1.0", "1", "[]",
        tx.Now, "hub-1", ModuleState.Pending, 10)));

    [Fact]
    public async Task Approve_module_goes_to_the_registry_and_is_audited()
    {
        await Module("abc123");
        Assert.True((await A.ApproveModule("abc123")).Ok);
        Assert.Equal(ModuleState.Approved, (await W.D.Read(tx => tx.Modules.Get("abc123")))!.State);
        Assert.Contains("Approved", Assert.Single(await W.Audit("module.approve")).AfterJson);
    }

    [Fact]
    public async Task Quarantine_drains_the_instances_running_the_module()
    {
        await Module("abc123");
        await W.Instance("lvl-1", rules: "abc123");
        await W.Instance("lvl-2", rules: "other");
        var r = await A.QuarantineModule("abc123");
        Assert.True(r.Ok, r.Message);
        Assert.Equal(["drain:lvl-1"], W.Lifecycle.Calls);
    }

    [Fact]
    public async Task An_unknown_module_is_refused()
    {
        Assert.Equal("unknown_module", (await A.DeleteModule("nope")).Reason);
        Assert.Empty(W.Modules.Calls);
    }

    [Fact]
    public async Task Reconcile_returns_the_findings_and_audits_the_count()
    {
        var c = await W.Character();
        await W.Lease(c.Id, "gone"); // a lease on an instance that does not exist
        var r = await A.RunReconcile(false);
        var findings = Assert.IsAssignableFrom<IReadOnlyList<AdminFinding>>(r.Value);
        Assert.Equal("lease_orphan", Assert.Single(findings).Kind);
        Assert.Contains("\"findings\":1", Assert.Single(await W.Audit("ledger.reconcile")).AfterJson);
    }

    [Fact]
    public async Task Backup_runs_and_is_audited()
    {
        Assert.True((await A.RunBackup()).Ok);
        Assert.Single(await W.Backups.List());
        Assert.Single(await W.Audit("backup.run"));
    }

    [Fact]
    public async Task A_runtime_setting_is_written_with_before_and_after()
    {
        await A.SetSetting("Instances.MaxLevelPods", "16");
        Assert.True((await A.SetSetting("Instances.MaxLevelPods", "20")).Ok);
        var last = (await W.Audit("config.set"))[0];
        Assert.Contains("16", last.BeforeJson);
        Assert.Contains("20", last.AfterJson);
    }

    [Fact]
    public async Task A_setting_that_is_not_runtime_is_refused()
    {
        Assert.Equal("not_runtime", (await A.SetSetting("Listen.Admin", "\"0.0.0.0:5000\"")).Reason);
    }

    [Fact]
    public async Task A_setting_that_is_not_json_is_refused()
    {
        Assert.Equal("bad_json", (await A.SetSetting("Instances.MaxLevelPods", "twenty")).Reason);
    }
}
