using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Admin.Components.Pages;
using SourceSharp.Host.Testing;
using InstancesPage = SourceSharp.Host.Admin.Components.Pages.Instances;

namespace SourceSharp.Host.Admin.Tests;

/// <summary>Dashboard, Gateway, Instances, Map pool, Room packs, Modules, Audit log, Config and Backups pages.</summary>
public sealed class OpsPageFacts : PageFacts
{
    [Fact]
    public async Task Dashboard_counts_instances_by_state()
    {
        await W.Instance("lvl-1");
        await W.Instance("lvl-2");
        var cut = Page<Dashboard>("#instances");
        cut.WaitForAssertion(() => Assert.Contains("LevelLive2", cut.Find("#instances").TextContent.Replace(" ", "").Replace("\n", "")));
    }

    [Fact]
    public void Dashboard_run_reconcile_calls_RunReconcile_with_the_fix_box()
    {
        var cut = Page<Dashboard>("#run-reconcile");
        cut.Find("#fix-sums").Change(true);
        cut.Find("#run-reconcile").Click();
        WaitResult(cut, "reconcile: 0 finding(s)");
        Assert.Equal([true], Rec.Single(nameof(IAdminActions.RunReconcile)));
        cut.WaitForAssertion(() => Assert.Contains("0 finding(s)", cut.Find("#reconcile").TextContent));
    }

    [Fact]
    public void Dashboard_run_backup_calls_RunBackup_and_shows_it_as_the_last_backup()
    {
        var cut = Page<Dashboard>("#run-backup");
        cut.Find("#run-backup").Click();
        WaitResult(cut, "backup written");
        Assert.Single(Rec.Calls, c => c.Method == nameof(IAdminActions.RunBackup));
        cut.WaitForAssertion(() => Assert.StartsWith("host-", cut.Find("#last-backup").TextContent));
    }

    Task<SessionRecord> Session(string id = "s-1") => W.D.Write(tx => tx.Sessions.Upsert(new SessionRecord(id, "203.0.113.9:27005", AdminWorld.Steam,
        "hub-1", "10.42.0.9:5010", "127.1.0.2", "routed", tx.Now, tx.Now, tx.Now)));

    [Fact]
    public async Task Gateway_lists_open_sessions_and_the_gateways_state()
    {
        await Session();
        var cut = Page<Gateway>("#session-s-1");
        Assert.Contains("203.0.113.9:27005", cut.Find("#session-s-1").TextContent);
        Assert.Contains("Descent hub", cut.Find("#gw-state").TextContent);
    }

    [Fact]
    public async Task Gateway_close_calls_CloseSession_with_the_row_after_confirm()
    {
        await Session();
        var cut = Page<Gateway>(".close");
        cut.Find(".close").Click();
        Confirm(cut, "closed");
        WaitResult(cut, "closed session");
        Assert.Equal(["s-1"], Rec.Single(nameof(IAdminActions.CloseSession)));
        Assert.Equal(["close:s-1"], W.Gateway.Calls);
    }

    [Fact]
    public async Task Gateway_ban_from_a_row_fills_the_address_then_calls_BanAddress()
    {
        await Session();
        var cut = Page<Gateway>(".ban-session");
        cut.Find(".ban-session").Click();
        cut.Find("#ban-note").Change("spam");
        cut.Find("#ban").Click();
        WaitResult(cut, "banned 203.0.113.9");
        Assert.Equal(["203.0.113.9", "spam"], Rec.Single(nameof(IAdminActions.BanAddress)));
    }

    [Fact]
    public async Task Gateway_release_pin_calls_ReleasePin_for_that_player()
    {
        await W.D.Write(tx => tx.Sessions.SetPin(new PlayerPin(AdminWorld.Steam, "127.1.0.2", "sidecar", tx.Now, tx.Now, [])));
        var cut = Page<Gateway>(".unpin");
        cut.Find(".unpin").Click();
        Confirm(cut);
        WaitResult(cut, "released");
        Assert.Equal([AdminWorld.Steam], Rec.Single(nameof(IAdminActions.ReleasePin)));
    }

    [Fact]
    public void Gateway_says_when_the_gateway_is_unreachable()
    {
        W.Gateway.Current = AdminGatewaySnapshot.Unreachable("connection refused");
        var cut = Page<Gateway>("#gw-state");
        Assert.Contains("unreachable: connection refused", cut.Find("#gw-state").TextContent);
    }

    async Task<InstanceRecord> RunningInstance()
    {
        var pod = W.Pods.Seed("descent-lvl-1", new Dictionary<string, string> { [GamePodLabels.App] = GamePodLabels.AppValue });
        W.Pods.AppendLog(pod.Name, "Map descent-3-abc loaded\n");
        return await W.D.Write(tx => tx.Instances.Add(new InstanceRecord("lvl-1", InstanceKind.Level, InstanceState.Live, 3, null, null, pod.Name, pod.Uid,
            "10.42.0.9", 27015, "hash", null, null, "fake", 1, tx.Now, null, tx.Now, null, null, null, null, tx.Now, 2, 27015)));
    }

    [Fact]
    public async Task Instances_shows_the_pod_phase_from_the_pod_host()
    {
        await RunningInstance();
        var cut = Page<InstancesPage>("#inst-lvl-1");
        cut.WaitForAssertion(() => Assert.Contains("Running ready", cut.Find("#inst-lvl-1").TextContent));
    }

    [Fact]
    public async Task Instances_drain_calls_DrainInstance_for_the_row_after_confirm()
    {
        await RunningInstance();
        var cut = Page<InstancesPage>(".drain");
        cut.Find(".drain").Click();
        Confirm(cut, "Draining");
        WaitResult(cut, "draining lvl-1");
        Assert.Equal("lvl-1", Rec.Single(nameof(IAdminActions.DrainInstance))[0]);
    }

    [Fact]
    public async Task Instances_open_tails_the_pod_log_and_links_the_devapi_proxy()
    {
        await RunningInstance();
        var cut = Page<InstancesPage>(".open");
        cut.Find(".open").Click();
        cut.WaitForAssertion(() => Assert.Contains("Map descent-3-abc loaded", cut.Find("#log").TextContent));
        Assert.Equal("admin/instances/lvl-1/devapi/", cut.Find("#devapi").GetAttribute("href"));
    }

    [Fact]
    public async Task Instances_console_calls_Exec_and_shows_sv_cheats_as_set()
    {
        await RunningInstance();
        var cut = Page<InstancesPage>(".open");
        cut.Find(".open").Click();
        cut.WaitForElement("#exec");
        Assert.Contains("not set", cut.Find("#sv-cheats").TextContent);
        cut.Find("#console").Change("sv_cheats 1");
        cut.Find("#exec").Click();
        WaitResult(cut, "sent: sv_cheats 1");
        Assert.Equal(["lvl-1", "sv_cheats 1"], Rec.Single(nameof(IAdminActions.Exec)));
        cut.WaitForAssertion(() => Assert.StartsWith("1", cut.Find("#sv-cheats").TextContent));
    }

    [Fact]
    public async Task Instances_kick_calls_KickPlayer_with_the_typed_steamid()
    {
        await RunningInstance();
        var cut = Page<InstancesPage>(".open");
        cut.Find(".open").Click();
        cut.WaitForElement("#kick");
        cut.Find("#kick-steamid").Change(AdminWorld.Steam);
        cut.Find("#kick").Click();
        WaitResult(cut, "kicked");
        Assert.Equal(["lvl-1", AdminWorld.Steam], Rec.Single(nameof(IAdminActions.KickPlayer))[..2]);
    }

    Task<LevelRecord> Level(string hash, LevelState state, int depth = 2) => W.D.Write(tx =>
        tx.Levels.Add(new LevelRecord(hash, 1, depth, "crypt", 1, 0, state, tx.Now, state == LevelState.HandedOut ? "lvl-1" : null, 10, null, "fake")));

    [Fact]
    public async Task Pool_counts_levels_per_depth()
    {
        await Level("aa", LevelState.Ready);
        await Level("bb", LevelState.HandedOut);
        var cut = Page<Pool>("#depth-2");
        var cells = cut.Find("#depth-2").QuerySelectorAll("td").Select(td => td.TextContent.Trim()).ToList();
        Assert.Equal(["2", "3", "1", "1"], cells.Take(4));
    }

    [Fact]
    public async Task Pool_regenerate_calls_RegenerateDepth_for_the_row_after_confirm()
    {
        await Level("aa", LevelState.Ready);
        var cut = Page<Pool>("#depth-2");
        cut.Find("#depth-2 .regen").Click();
        Confirm(cut);
        WaitResult(cut, "retired 1");
        Assert.Equal([2], Rec.Single(nameof(IAdminActions.RegenerateDepth)));
    }

    [Fact]
    public void Pool_set_k_calls_SetPerDepth_and_the_row_shows_the_runtime_k()
    {
        var cut = Page<Pool>("#depth-4");
        cut.Find("#depth-4 .k-in").Change("5");
        cut.Find("#depth-4 .set-k").Click();
        WaitResult(cut, "MapPool.PerDepth.4 = 5");
        Assert.Equal([4, 5], Rec.Single(nameof(IAdminActions.SetPerDepth)));
        cut.WaitForAssertion(() => Assert.Contains("5 (runtime)", cut.Find("#depth-4").TextContent));
    }

    [Fact]
    public async Task Pool_a_handed_out_level_offers_no_retire_or_delete()
    {
        await Level("aa", LevelState.HandedOut);
        var cut = Page<Pool>("#depth-2");
        cut.Find("#depth-2 .levels").Click();
        cut.WaitForElement("#level-aa");
        Assert.Empty(cut.Find("#level-aa").QuerySelectorAll("button"));
    }

    [Fact]
    public async Task Pool_requeue_calls_RequeueJob_for_a_failed_job()
    {
        var job = await W.D.Write(async tx =>
        {
            var j = await tx.MapJobs.Enqueue(MapJobKind.Link, "link:descent:2:1:0", 3);
            await tx.MapJobs.TakeNext();
            return await tx.MapJobs.Fail(j.Id, "boom", requeue: false);
        });
        var cut = Page<Pool>(".requeue");
        cut.Find(".requeue").Click();
        WaitResult(cut, "requeued");
        Assert.Equal([job.Id], Rec.Single(nameof(IAdminActions.RequeueJob)));
    }

    async Task<PackRecord> Upload()
    {
        using var s = new MemoryStream(FakePack.Bytes("crypt", ["start", "hall", "stairs"]));
        return (await W.Catalog.UploadAsync("descent", "crypt", s, "{}", null, "upload", "admin@localhost")).Pack;
    }

    [Fact]
    public async Task Packs_lists_versions_and_the_upload_form_posts_with_an_antiforgery_token()
    {
        var p = await Upload();
        var cut = Page<Packs>($"#pack-{p.Id}");
        Assert.Contains("crypt", cut.Find($"#pack-{p.Id}").TextContent);
        var form = cut.Find("#upload");
        Assert.Equal(("admin/packs/upload", "multipart/form-data"), (form.GetAttribute("action"), form.GetAttribute("enctype")));
        Assert.Equal("test-token", form.QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value"));
    }

    [Fact]
    public async Task Packs_activate_calls_ActivatePack_with_the_range_version_and_policy()
    {
        var p = await Upload();
        var cut = Page<Packs>("#activate");
        cut.Find("#from").Change("2");
        cut.Find("#to").Change("4");
        cut.Find("#policy").Change("Retire");
        cut.Find("#activate").Click();
        Confirm(cut, "\"Depth\": 3");
        WaitResult(cut, "activated");
        var args = Rec.Single(nameof(IAdminActions.ActivatePack));
        Assert.Equal("descent", args[0]);
        Assert.Equal([2, 3, 4], (IEnumerable<int>)args[1]!);
        Assert.Equal((p.Id, true, OldLevelsPolicy.Retire), ((long)args[2]!, (bool)args[3]!, (OldLevelsPolicy)args[4]!));
        cut.WaitForAssertion(() => Assert.Contains("2,3,4", cut.Find($"#pack-{p.Id}").TextContent));
    }

    [Fact]
    public async Task Packs_upload_result_from_the_redirect_is_shown()
    {
        await Upload();
        var nav = Ctx.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(nav.GetUriWithQueryParameter("result", "refused: rejected: lint"));
        var cut = Page<Packs>("#upload-result");
        Assert.Contains("rejected: lint", cut.Find("#upload-result").TextContent);
    }

    Task<RulesModuleRecord> Module(string sha, string? instance = null) => W.D.Write(tx => tx.Modules.Add(new RulesModuleRecord(sha, "Descent.Rules", "1.0", "1", "[]",
        tx.Now, instance, ModuleState.Pending, 10)));

    [Fact]
    public async Task Modules_lists_the_instances_running_each_module()
    {
        await Module("abc123");
        await W.Instance("lvl-1", rules: "abc123");
        var cut = Page<SourceSharp.Host.Admin.Components.Pages.Modules>("#module-abc123");
        Assert.Contains("lvl-1", cut.Find("#module-abc123").TextContent);
    }

    [Fact]
    public async Task Modules_approve_calls_ApproveModule_for_the_row()
    {
        await Module("abc123");
        var cut = Page<SourceSharp.Host.Admin.Components.Pages.Modules>(".approve");
        cut.Find(".approve").Click();
        WaitResult(cut, "approved");
        Assert.Equal(["abc123"], Rec.Single(nameof(IAdminActions.ApproveModule)));
    }

    [Fact]
    public async Task Modules_quarantine_calls_QuarantineModule_after_confirm()
    {
        await Module("abc123");
        var cut = Page<SourceSharp.Host.Admin.Components.Pages.Modules>(".quarantine");
        cut.Find(".quarantine").Click();
        Confirm(cut, "Quarantined");
        WaitResult(cut, "quarantined");
        Assert.Equal(["abc123"], Rec.Single(nameof(IAdminActions.QuarantineModule)));
    }

    [Fact]
    public async Task Modules_delete_is_disabled_while_an_instance_runs_the_module()
    {
        await Module("abc123");
        await W.Instance("lvl-1", rules: "abc123");
        var cut = Page<SourceSharp.Host.Admin.Components.Pages.Modules>("#module-abc123");
        Assert.True(cut.Find("#module-abc123 .delete").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Audit_lists_rows_and_filters_by_action()
    {
        await W.D.Write(async tx => { await tx.Audit.Write("account.ban", "a", null, null, "admin@localhost"); await tx.Audit.Write("pack.retire", "b", null, null, "admin@localhost"); });
        var cut = Page<Audit>("#audit");
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("#audit tr").Count));
        cut.Find("#f-action").Change("pack.retire");
        cut.Find("#filter").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("#audit tr").Count));
        Assert.Contains("pack.retire", cut.Find("#audit").TextContent);
    }

    [Fact]
    public void Audit_offers_no_action()
    {
        var cut = Page<Audit>("#audit");
        Assert.DoesNotContain(cut.FindAll("button"), b => b.Id is not ("filter" or "prev" or "next"));
    }

    [Fact]
    public void Config_lists_every_option_and_offers_edit_only_for_runtime_ones()
    {
        var cut = Page<Config>("#options");
        Assert.NotNull(cut.Find("[id='opt-Listen.Admin']"));
        Assert.Empty(cut.Find("[id='opt-Listen.Admin']").QuerySelectorAll(".edit"));
        Assert.NotNull(cut.Find("[id='opt-Instances.MaxLevelPods'] .edit"));
    }

    [Fact]
    public void Config_save_calls_SetSetting_with_the_key_and_typed_json()
    {
        var cut = Page<Config>("#options");
        cut.Find("[id='opt-Instances.MaxLevelPods'] .edit").Click();
        cut.Find("#value").Change("20");
        cut.Find("#save").Click();
        Confirm(cut, "+ 20");
        WaitResult(cut, "Instances.MaxLevelPods = 20");
        Assert.Equal(["Instances.MaxLevelPods", "20"], Rec.Single(nameof(IAdminActions.SetSetting)));
        cut.WaitForAssertion(() => Assert.Contains("20 (admin@localhost", cut.Find("[id='opt-Instances.MaxLevelPods']").TextContent));
    }

    [Fact]
    public async Task Backups_lists_backups_and_restore_calls_RestoreBackupToFile()
    {
        var b = await W.Backups.RunNow();
        var cut = Page<Backups>($"[id='backup-{b.Name}']");
        Assert.Equal($"admin/backups/{b.Name}", cut.Find(".download").GetAttribute("href"));
        cut.Find(".restore").Click();
        WaitResult(cut, "restored");
        Assert.Equal([b.Name], Rec.Single(nameof(IAdminActions.RestoreBackupToFile)));
        Assert.Equal([b.Name], W.Backups.Restored);
    }
}
