using SourceSharp.Host.Abstractions;

namespace SourceSharp.Host.Instances.Tests;

/// <summary>§6.1 heartbeats, §7.3 crashed / draining / reaped, Q10 empty grace.</summary>
public class HealthAndReapFacts
{
    static TimeSpan Beats(ManagerHarness h, int n) => h.Options.Instances.HeartbeatInterval * n;

    [Fact]
    public async Task Missed_heartbeats_make_an_instance_suspect()
    {
        await using var h = new ManagerHarness();
        var row = await h.LiveLevel();
        await h.M.OnHeartbeat(row.Id, 1);
        await h.Advance(Beats(h, 3) - TimeSpan.FromMilliseconds(1));
        await h.Tick();
        Assert.Equal(InstanceState.Live, (await h.Row(row.Id)).State);
        await h.Advance(TimeSpan.FromMilliseconds(1));
        await h.Tick();
        Assert.Equal(InstanceState.Suspect, (await h.Row(row.Id)).State);
    }

    [Fact]
    public async Task A_heartbeat_brings_a_suspect_back_to_live()
    {
        await using var h = new ManagerHarness();
        var row = await h.LiveLevel();
        await h.M.OnHeartbeat(row.Id, 1);
        await h.Advance(Beats(h, 4));
        await h.Tick();
        await h.M.OnHeartbeat(row.Id, 1);
        Assert.Equal(InstanceState.Live, (await h.Row(row.Id)).State);
        Assert.Equal(2, (await h.Actions(row.Id)).Count(a => a == "instance.live"));
    }

    [Fact]
    public async Task Suspect_then_reap_after_the_reap_count()
    {
        await using var h = new ManagerHarness();
        var row = await h.LiveLevel();
        await h.M.OnHeartbeat(row.Id, 1);
        await h.Advance(Beats(h, 3));
        await h.Tick();
        Assert.Equal(InstanceState.Suspect, (await h.Row(row.Id)).State);
        await h.Advance(Beats(h, 3) - TimeSpan.FromMilliseconds(1));
        await h.Tick();
        Assert.Equal(InstanceState.Suspect, (await h.Row(row.Id)).State);
        await h.Advance(TimeSpan.FromMilliseconds(1));
        await h.Tick();
        var reaped = await h.Row(row.Id);
        Assert.Equal(InstanceState.Reaped, reaped.State);
        Assert.StartsWith("heartbeats_lost", reaped.Reason);
    }

    [Fact]
    public async Task A_stopping_service_judges_no_heartbeats_so_the_next_one_can_adopt()
    {
        await using var h = new ManagerHarness();
        var row = await h.LiveLevel();
        await h.M.OnHeartbeat(row.Id, 1);
        h.M.Stopping();
        await h.Advance(Beats(h, 12));
        await h.Tick();
        var after = await h.Row(row.Id);
        Assert.Equal(InstanceState.Live, after.State);
        Assert.DoesNotContain(h.Log, l => l.StartsWith("delete "));
    }

    [Fact]
    public async Task Stream_close_is_a_crash_the_pod_is_deleted_and_OnCrashed_runs()
    {
        await using var h = new ManagerHarness();
        var row = await h.LiveLevel();
        await h.M.OnStreamClosed(row.Id);
        var after = await h.Row(row.Id);
        Assert.Equal((InstanceState.Crashed, "stream_closed"), (after.State, after.Reason));
        Assert.Contains($"delete {row.PodName} {row.PodUid} 60s", h.Log);
        Assert.Equal(["OnLive", "OnCrashed"], h.Hooks.For(row.Id));
    }

    [Fact]
    public async Task Stream_close_while_draining_is_not_a_crash()
    {
        await using var h = new ManagerHarness();
        var row = await h.LiveLevel();
        await h.M.OnHeartbeat(row.Id, 1);
        await h.M.Drain(row.Id, "admin", "admin@localhost");
        await h.M.OnStreamClosed(row.Id);
        Assert.Equal(InstanceState.Draining, (await h.Row(row.Id)).State);
    }

    [Fact]
    public async Task Pod_failed_outside_draining_is_a_crash_with_its_exit_code()
    {
        await using var h = new ManagerHarness();
        var row = await h.LiveLevel();
        h.Pods.SetPhase(row.PodName!, PodPhase.Failed, exitCode: 139);
        await h.Pump();
        var after = await h.Row(row.Id);
        Assert.Equal((InstanceState.Crashed, 139, "pod_failed"), (after.State, after.ExitCode, after.Reason));
    }

    [Fact]
    public async Task Pod_succeeded_outside_draining_is_a_crash()
    {
        await using var h = new ManagerHarness();
        var row = await h.LiveLevel();
        h.Pods.SetPhase(row.PodName!, PodPhase.Succeeded, exitCode: 0);
        await h.Pump();
        Assert.Equal(InstanceState.Crashed, (await h.Row(row.Id)).State);
    }

    [Fact]
    public async Task Pod_deleted_from_outside_is_a_crash()
    {
        await using var h = new ManagerHarness();
        var row = await h.LiveLevel();
        h.Pods.DeleteExternally(row.PodName!);
        await h.Pump();
        var after = await h.Row(row.Id);
        Assert.Equal((InstanceState.Crashed, "pod_deleted"), (after.State, after.Reason));
        Assert.Equal(["OnLive", "OnCrashed"], h.Hooks.For(row.Id));
    }

    [Fact]
    public async Task Container_restart_is_a_crash()
    {
        await using var h = new ManagerHarness();
        var row = await h.LiveLevel();
        h.Pods.Restart(row.PodName!);
        await h.Pump();
        Assert.Equal((InstanceState.Crashed, "container_restarted"), ((await h.Row(row.Id)).State, (await h.Row(row.Id)).Reason));
    }

    [Fact]
    public async Task Crash_during_boot_is_a_crash()
    {
        await using var h = new ManagerHarness();
        var r = await h.RequestLevel();
        await h.Tick();
        var row = await h.Row(r.Id);
        h.Pods.SetPhase(row.PodName!, PodPhase.Failed, exitCode: 1);
        await h.Pump();
        Assert.Equal(InstanceState.Crashed, (await h.Row(r.Id)).State);
    }

    [Fact]
    public async Task Events_of_a_pod_with_another_uid_are_ignored()
    {
        await using var h = new ManagerHarness();
        var row = await h.LiveLevel();
        var stranger = new PodStatus(row.PodName!, "another-uid", PodPhase.Failed, false, null, 0, 1,
            new Dictionary<string, string> { [GamePodLabels.App] = GamePodLabels.AppValue, [GamePodLabels.Instance] = row.Id });
        await h.M.Apply(new PodEvent(PodEventType.Modified, stranger));
        Assert.Equal(InstanceState.Live, (await h.Row(row.Id)).State);
    }

    [Fact]
    public async Task Level_pod_is_never_restarted_after_a_crash()
    {
        await using var h = new ManagerHarness();
        var row = await h.LiveLevel();
        h.Pods.DeleteExternally(row.PodName!);
        await h.Pump();
        for (var i = 0; i < 5; i++) { await h.Advance(TimeSpan.FromSeconds(30)); await h.Tick(); }
        Assert.Single(h.Pods.Created);
        Assert.Single(await h.D.Read(tx => tx.Instances.List(InstanceKind.Level)));
    }

    [Fact]
    public async Task Empty_level_drains_after_the_empty_grace_and_not_before()
    {
        await using var h = new ManagerHarness();
        var row = await h.LiveLevel();
        await Beat(h, row.Id, 0, h.Options.Instances.EmptyGrace - TimeSpan.FromSeconds(5));
        await h.Tick();
        Assert.Equal(InstanceState.Live, (await h.Row(row.Id)).State);
        await Beat(h, row.Id, 0, TimeSpan.FromSeconds(5));
        await h.Tick();
        var after = await h.Row(row.Id);
        Assert.Equal((InstanceState.Draining, "empty"), (after.State, after.Reason));
        Assert.Equal(new InstanceCommand.Drain("empty"), h.Commands.Sent.Single().Command);
    }

    [Fact]
    public async Task Empty_level_with_a_corpse_waits_the_corpse_grace()
    {
        await using var h = new ManagerHarness();
        var row = await h.LiveLevel();
        h.Hooks.Corpses.Add(row.Id);
        await Beat(h, row.Id, 0, h.Options.Instances.CorpseGrace - TimeSpan.FromSeconds(5));
        await h.Tick();
        Assert.Equal(InstanceState.Live, (await h.Row(row.Id)).State);
        await Beat(h, row.Id, 0, TimeSpan.FromSeconds(5));
        await h.Tick();
        var after = await h.Row(row.Id);
        Assert.Equal((InstanceState.Draining, "empty_with_corpse"), (after.State, after.Reason));
    }

    [Fact]
    public async Task Occupied_level_never_drains()
    {
        await using var h = new ManagerHarness();
        var row = await h.LiveLevel();
        await Beat(h, row.Id, 2, h.Options.Instances.CorpseGrace * 2);
        await h.Tick();
        Assert.Equal(InstanceState.Live, (await h.Row(row.Id)).State);
    }

    [Fact]
    public async Task A_player_joining_resets_the_empty_grace()
    {
        await using var h = new ManagerHarness();
        var row = await h.LiveLevel();
        await Beat(h, row.Id, 0, h.Options.Instances.EmptyGrace - TimeSpan.FromSeconds(10));
        await Beat(h, row.Id, 1, TimeSpan.FromSeconds(5));
        await Beat(h, row.Id, 0, TimeSpan.FromSeconds(30));
        await h.Tick();
        Assert.Equal(InstanceState.Live, (await h.Row(row.Id)).State);
    }

    [Fact]
    public async Task Draining_and_empty_is_reaped()
    {
        await using var h = new ManagerHarness();
        var row = await h.LiveLevel();
        await h.M.OnHeartbeat(row.Id, 1);
        await h.M.Drain(row.Id, "admin", "admin@localhost");
        await h.Tick();
        Assert.Equal(InstanceState.Draining, (await h.Row(row.Id)).State);
        await h.M.OnHeartbeat(row.Id, 0);
        await h.Tick();
        Assert.Equal(InstanceState.Reaped, (await h.Row(row.Id)).State);
    }

    [Fact]
    public async Task Pod_exiting_while_draining_is_a_reap_not_a_crash()
    {
        await using var h = new ManagerHarness();
        var row = await h.LiveLevel();
        await h.M.OnHeartbeat(row.Id, 1);
        await h.M.Drain(row.Id, "admin", "admin@localhost");
        h.Pods.ExitNow(row.PodName!);
        await h.Pump();
        Assert.Equal(InstanceState.Reaped, (await h.Row(row.Id)).State);
        Assert.Equal(["OnLive", "OnReaping", "OnReaped"], h.Hooks.For(row.Id));
    }

    [Fact]
    public async Task Reap_runs_OnReaping_then_the_pod_delete_then_OnReaped()
    {
        await using var h = new ManagerHarness();
        var row = await h.LiveLevel();
        await h.M.Drain(row.Id, "admin", "admin@localhost");
        h.Log.Clear();
        await h.Tick(); // draining with 0 players → reap
        Assert.Equal([$"OnReaping {row.Id}", $"delete {row.PodName} {row.PodUid} 60s", $"OnReaped {row.Id}"], h.Log);
    }

    [Fact]
    public async Task Reap_is_persisted_before_its_side_effects()
    {
        await using var h = new ManagerHarness();
        var row = await h.LiveLevel();
        await h.M.Drain(row.Id, "admin", "admin@localhost");
        await h.Tick();
        var reapingSaw = h.Hooks.Calls.Single(c => c.Hook == "OnReaping").Row;
        Assert.Equal(InstanceState.Reaped, reapingSaw.State);
        var actions = await h.Actions(row.Id);
        Assert.True(actions.ToList().IndexOf("instance.reaped") < actions.ToList().IndexOf("instance.reaped.done"));
    }

    [Fact]
    public async Task Reap_is_a_pod_delete_with_the_configured_grace()
    {
        await using var h = new ManagerHarness(o => o.Instances.ReapGrace = TimeSpan.FromSeconds(45));
        var row = await h.LiveLevel();
        await h.M.Drain(row.Id, "admin", "admin@localhost");
        await h.Tick();
        Assert.Contains(h.Pods.Calls, c => c == ("delete", row.PodName!, row.PodUid!, TimeSpan.FromSeconds(45)));
        await h.Advance(TimeSpan.FromSeconds(44));
        Assert.NotNull(h.Pods.Get(row.PodName!));
        await h.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(h.Pods.Get(row.PodName!));
    }

    [Fact]
    public async Task Reaped_pod_takes_its_secret_with_it()
    {
        await using var h = new ManagerHarness();
        var row = await h.LiveLevel();
        Assert.Single(h.Pods.Secrets);
        await h.M.Drain(row.Id, "admin", "admin@localhost");
        await h.Tick();
        await h.Advance(h.Options.Instances.ReapGrace);
        Assert.Empty(h.Pods.Secrets);
    }

    [Fact]
    public async Task Pod_deleted_event_after_a_reap_changes_nothing()
    {
        await using var h = new ManagerHarness();
        var row = await h.LiveLevel();
        await h.M.Drain(row.Id, "admin", "admin@localhost");
        await h.Tick();
        await h.Advance(h.Options.Instances.ReapGrace);
        var after = await h.Row(row.Id);
        Assert.Equal(InstanceState.Reaped, after.State);
        Assert.DoesNotContain("OnCrashed", h.Hooks.For(row.Id));
    }

    static async Task Beat(ManagerHarness h, string id, int players, TimeSpan over)
    {
        var step = h.Options.Instances.HeartbeatInterval;
        await h.M.OnHeartbeat(id, players);
        for (var t = TimeSpan.Zero; t + step <= over; t += step)
        {
            await h.Advance(step);
            await h.M.OnHeartbeat(id, players);
        }
        var rest = TimeSpan.FromTicks(over.Ticks % step.Ticks);
        if (rest > TimeSpan.Zero) { await h.Advance(rest); await h.M.OnHeartbeat(id, players); }
    }
}
