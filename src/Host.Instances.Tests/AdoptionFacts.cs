using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Instances;

namespace SourceSharp.Host.Instances.Tests;

/// <summary>§7.4: a restarted service adopts pods by name AND uid, crashes the rest, deletes orphans.</summary>
public class AdoptionFacts
{
    static Dictionary<string, string> GameLabels(string id) => new()
    {
        [GamePodLabels.App] = GamePodLabels.AppValue, [GamePodLabels.Instance] = id, [GamePodLabels.Kind] = "level",
    };

    [Fact]
    public async Task Pod_with_the_same_name_and_uid_is_kept()
    {
        await using var h = new ManagerHarness();
        var row = await h.LiveLevel();
        h.NewManager();
        h.Log.Clear();
        await h.M.Start();
        Assert.Equal(InstanceState.Live, (await h.Row(row.Id)).State);
        Assert.Empty(h.Log);
        Assert.Contains("instance.adopted", await h.Actions(row.Id));
    }

    [Fact]
    public async Task Pod_with_the_same_name_and_another_uid_is_crashed_and_deleted()
    {
        await using var h = new ManagerHarness();
        var row = await h.LiveLevel();
        h.Pods.DeleteExternally(row.PodName!);       // while the service is down: never pumped
        var impostor = h.Pods.Seed(row.PodName!, GameLabels(row.Id));
        Assert.NotEqual(row.PodUid, impostor.Uid);
        h.NewManager();
        await h.M.Start();

        var after = await h.Row(row.Id);
        Assert.Equal(InstanceState.Crashed, after.State);
        Assert.Contains("uid", after.Reason);
        Assert.Contains($"delete {row.PodName} {impostor.Uid} 60s", h.Log);
        Assert.Contains("OnCrashed", h.Hooks.For(row.Id));
    }

    [Fact]
    public async Task Row_without_a_pod_is_crashed_and_its_transitions_run()
    {
        await using var h = new ManagerHarness();
        var row = await h.LiveLevel();
        h.Pods.DeleteExternally(row.PodName!);
        h.NewManager();
        await h.M.Start();
        var after = await h.Row(row.Id);
        Assert.Equal((InstanceState.Crashed, "adoption: no pod"), (after.State, after.Reason));
        Assert.Equal(["OnLive", "OnCrashed"], h.Hooks.For(row.Id));
        Assert.Contains("instance.crashed.done", await h.Actions(row.Id));
    }

    [Fact]
    public async Task Row_still_creating_without_a_uid_is_crashed_and_its_pod_deleted()
    {
        await using var h = new ManagerHarness();
        var r = await h.RequestLevel();
        var name = GamePodBuilder.PodName(InstanceKind.Level, r.Id);
        await h.D.Write(tx => tx.Instances.Update(r.Id, x => x with { State = InstanceState.Creating, PodName = name }));
        var pod = h.Pods.Seed(name, GameLabels(r.Id));
        await h.M.Start();
        Assert.Equal(InstanceState.Crashed, (await h.Row(r.Id)).State);
        Assert.Contains($"delete {name} {pod.Uid} 60s", h.Log);
    }

    [Fact]
    public async Task Labelled_pod_without_a_row_is_deleted()
    {
        await using var h = new ManagerHarness();
        var orphan = h.Pods.Seed("descent-level-orphan", GameLabels("01ORPHAN"));
        await h.M.Start();
        Assert.Contains($"delete descent-level-orphan {orphan.Uid} 60s", h.Log);
        Assert.Equal(1, await h.D.Read(tx => tx.Audit.Count(new AuditQuery(Action: "instance.orphan_deleted", Target: "descent-level-orphan"))));
    }

    [Fact]
    public async Task Pod_without_the_game_label_is_never_touched()
    {
        await using var h = new ManagerHarness();
        h.Pods.Seed("descent-service-0", new Dictionary<string, string> { ["app"] = "descent-service" });
        await h.M.Start();
        Assert.DoesNotContain(h.Log, l => l.StartsWith("delete", StringComparison.Ordinal));
        Assert.NotNull(h.Pods.Get("descent-service-0"));
    }

    [Fact]
    public async Task Adopted_pod_that_does_not_reconnect_within_the_window_is_crashed()
    {
        await using var h = new ManagerHarness();
        var row = await h.LiveLevel();
        h.NewManager();
        await h.M.Start();
        await h.Advance(h.Options.Instances.AdoptReconnect - TimeSpan.FromSeconds(1));
        await h.Tick();
        Assert.Equal(InstanceState.Live, (await h.Row(row.Id)).State);
        await h.Advance(TimeSpan.FromSeconds(1));
        await h.Tick();
        var after = await h.Row(row.Id);
        Assert.Equal((InstanceState.Crashed, "adoption: stream not reconnected"), (after.State, after.Reason));
    }

    [Fact]
    public async Task Adopted_pod_that_reconnects_stays_live()
    {
        await using var h = new ManagerHarness();
        var row = await h.LiveLevel();
        await h.Advance(TimeSpan.FromMinutes(2)); // the service was down: heartbeats look long lost
        h.NewManager();
        await h.M.Start();
        await h.Tick();
        Assert.Equal(InstanceState.Live, (await h.Row(row.Id)).State);
        await h.M.OnStreamOpened(row.Id);
        await h.Advance(h.Options.Instances.AdoptReconnect);
        await h.M.OnHeartbeat(row.Id, 1);
        await h.Tick();
        Assert.Equal(InstanceState.Live, (await h.Row(row.Id)).State);
        Assert.Contains("instance.reconnected", await h.Actions(row.Id));
    }

    [Fact]
    public async Task Boot_deadline_survives_a_restart()
    {
        await using var h = new ManagerHarness();
        var r = await h.RequestLevel();
        await h.Tick();
        await h.Advance(TimeSpan.FromSeconds(10));
        h.NewManager();
        await h.M.Start();
        await h.Advance(h.Options.Instances.BootTimeout - TimeSpan.FromSeconds(11));
        await h.Tick();
        Assert.Equal(InstanceState.Booting, (await h.Row(r.Id)).State);
        await h.Advance(TimeSpan.FromSeconds(1)); // BootTimeout after the creating row, not after the restart
        await h.Tick();
        Assert.Equal(InstanceState.Failed, (await h.Row(r.Id)).State);
    }

    [Fact]
    public async Task Restart_near_the_boot_deadline_still_leaves_the_reconnect_window()
    {
        await using var h = new ManagerHarness();
        var r = await h.RequestLevel();
        await h.Tick();
        await h.Advance(h.Options.Instances.BootTimeout - TimeSpan.FromSeconds(10));
        h.NewManager();
        await h.M.Start();
        await h.Advance(h.Options.Instances.AdoptReconnect - TimeSpan.FromSeconds(1));
        await h.Tick();
        Assert.Equal(InstanceState.Booting, (await h.Row(r.Id)).State);
        await h.Advance(TimeSpan.FromSeconds(1));
        await h.Tick();
        Assert.Equal(InstanceState.Failed, (await h.Row(r.Id)).State);
    }

    [Fact]
    public async Task Empty_grace_survives_a_restart()
    {
        await using var h = new ManagerHarness();
        var row = await h.LiveLevel(); // live with 0 players: empty from now
        await h.Advance(TimeSpan.FromMinutes(4));
        h.NewManager();
        await h.M.Start();
        await h.M.OnStreamOpened(row.Id);
        await h.Advance(TimeSpan.FromMinutes(1));
        await h.M.OnHeartbeat(row.Id, 0);
        await h.Tick();
        var after = await h.Row(row.Id);
        Assert.Equal((InstanceState.Draining, "empty"), (after.State, after.Reason));
    }

    [Fact]
    public async Task Occupied_edge_before_a_restart_restarts_the_empty_clock()
    {
        await using var h = new ManagerHarness();
        var row = await h.LiveLevel();
        await h.Advance(TimeSpan.FromMinutes(3));
        await h.M.OnHeartbeat(row.Id, 2);             // occupied
        await h.Advance(TimeSpan.FromMinutes(1));
        await h.M.OnHeartbeat(row.Id, 0);             // empty again, from here
        h.NewManager();
        await h.M.Start();
        await h.M.OnStreamOpened(row.Id);
        await h.Advance(TimeSpan.FromMinutes(2));
        await h.M.OnHeartbeat(row.Id, 0);
        await h.Tick();
        Assert.Equal(InstanceState.Live, (await h.Row(row.Id)).State);
        Assert.Equal(["instance.empty", "instance.occupied", "instance.empty"],
            (await h.Actions(row.Id)).Where(a => a is "instance.empty" or "instance.occupied"));
    }

    [Fact]
    public async Task Adopted_hub_is_not_created_twice()
    {
        await using var h = new ManagerHarness(o => o.Instances.Hubs = 1);
        await h.M.Start();
        await h.BringLive(await h.Hub());
        h.NewManager();
        await h.M.Start();
        await h.Tick();
        Assert.Single(h.Pods.Created);
    }

    [Fact]
    public async Task Reap_interrupted_before_its_sweep_is_resumed_on_start()
    {
        await using var h = new ManagerHarness();
        var row = await h.LiveLevel();
        await h.M.Drain(row.Id, "admin", "admin@localhost");
        h.Hooks.FailNextReaped = true;
        await h.Tick(); // the tick logs the failure and carries on
        Assert.Single(h.Hooks.For(row.Id), x => x == "OnReaped");
        Assert.Equal(InstanceState.Reaped, (await h.Row(row.Id)).State);
        Assert.DoesNotContain("instance.reaped.done", await h.Actions(row.Id));

        h.NewManager();
        await h.M.Start();
        Assert.Equal(2, h.Hooks.For(row.Id).Count(x => x == "OnReaped"));
        Assert.Contains("instance.reaped.done", await h.Actions(row.Id));
    }

    [Fact]
    public async Task Finished_reap_is_not_run_again_on_start()
    {
        await using var h = new ManagerHarness();
        var row = await h.LiveLevel();
        await h.M.Drain(row.Id, "admin", "admin@localhost");
        await h.Tick();
        h.NewManager();
        await h.M.Start();
        Assert.Equal(1, h.Hooks.For(row.Id).Count(x => x == "OnReaped"));
    }
}
