using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Instances;

namespace SourceSharp.Host.Instances.Tests;

/// <summary>§7.3: requested → level_ready → creating → booting → live, and failed.</summary>
public class LifecycleFacts
{
    [Fact]
    public async Task Level_request_with_a_hash_starts_level_ready()
    {
        await using var h = new ManagerHarness();
        var row = await h.RequestLevel(depth: 7, hash: "abc");
        Assert.Equal((InstanceState.LevelReady, InstanceKind.Level, 7, "abc", "party-1"), (row.State, row.Kind, row.Depth, row.LevelHash, row.PartyId));
    }

    [Fact]
    public async Task Level_request_without_a_hash_stays_requested_until_assigned()
    {
        await using var h = new ManagerHarness();
        var r = Assert.IsType<LevelRequestResult.Accepted>(await h.M.RequestLevel(2, null, null, null)).Instance;
        await h.Tick();
        Assert.Equal(InstanceState.Requested, (await h.Row(r.Id)).State);
        Assert.Empty(h.Pods.Created);

        await h.M.AssignLevel(r.Id, "h9", 4);
        await h.Tick();
        var row = await h.Row(r.Id);
        Assert.Equal((InstanceState.Booting, "h9"), (row.State, row.LevelHash));
    }

    [Fact]
    public async Task Tick_creates_the_pod_and_records_its_name_and_uid()
    {
        await using var h = new ManagerHarness();
        var r = await h.RequestLevel();
        await h.Tick();
        var row = await h.Row(r.Id);
        var pod = h.Pods.Created.Single();
        Assert.Equal(InstanceState.Booting, row.State);
        Assert.Equal(pod.Name, row.PodName);
        Assert.Equal(h.Pods.Get(pod.Name)!.Uid, row.PodUid);
        Assert.Equal(27015, row.ExpectedPort);
        Assert.Equal(h.Options.Instances.ModImage.ToString(), row.ModImage);
    }

    [Fact]
    public async Task Pod_ip_from_the_watch_is_recorded()
    {
        await using var h = new ManagerHarness();
        var r = await h.RequestLevel();
        await h.Tick();
        var row = await h.Row(r.Id);
        Assert.Equal(h.Pods.Get(row.PodName!)!.Ip, row.PodIp);
        Assert.NotNull(row.PodIp);
    }

    [Fact]
    public async Task Booting_records_the_rules_module_and_the_mod_image_digest()
    {
        await using var h = new ManagerHarness();
        var r = await h.RequestLevel();
        await h.Tick();
        await h.M.OnBooting(r.Id, "rules-sha", "sha256:digest");
        var row = await h.Row(r.Id);
        Assert.Equal(("rules-sha", "sha256:digest", h.Clock.GetUtcNow()), (row.RulesSha256, row.ModImageDigest, row.Booted));
    }

    [Fact]
    public async Task MapReady_before_pod_ready_waits_for_ready()
    {
        await using var h = new ManagerHarness();
        var r = await h.RequestLevel();
        await h.Tick();
        var result = await h.M.OnMapReady(r.Id, 27015);
        Assert.Equal(MapReadyOutcome.AwaitingPodReady, result.Outcome);
        Assert.Equal((InstanceState.Booting, 27015), (result.Instance.State, result.Instance.Port));

        h.Pods.SetReady(result.Instance.PodName!);
        await h.Pump();
        Assert.Equal(InstanceState.Live, (await h.Row(r.Id)).State);
    }

    [Fact]
    public async Task Pod_ready_alone_is_not_live()
    {
        await using var h = new ManagerHarness();
        var r = await h.RequestLevel();
        await h.Tick();
        h.Pods.SetReady((await h.Row(r.Id)).PodName!);
        await h.Pump();
        Assert.Equal(InstanceState.Booting, (await h.Row(r.Id)).State);
    }

    [Fact]
    public async Task Live_needs_mapready_and_pod_ready_and_calls_OnLive()
    {
        await using var h = new ManagerHarness();
        var row = await h.LiveLevel();
        Assert.Equal(InstanceState.Live, row.State);
        Assert.Equal(h.Clock.GetUtcNow(), row.LiveAt);
        Assert.Equal(["OnLive"], h.Hooks.For(row.Id));
    }

    [Fact]
    public async Task Each_transition_writes_its_audit_row_in_order()
    {
        await using var h = new ManagerHarness();
        var row = await h.LiveLevel();
        Assert.Equal(
            ["instance.requested", "instance.levelready", "instance.creating", "instance.booting", "instance.boot_reported", "instance.map_ready", "instance.live", "instance.empty"],
            await h.Actions(row.Id));
    }

    [Fact]
    public async Task Transition_audit_row_names_the_state_before_and_after()
    {
        await using var h = new ManagerHarness();
        var row = await h.LiveLevel();
        var live = (await h.Audits(row.Id)).Single(a => a.Action == "instance.live");
        Assert.Contains("Booting", live.BeforeJson);
        Assert.Contains("Live", live.AfterJson);
        Assert.Equal(InstanceManager.Actor, live.Actor);
    }

    [Fact]
    public async Task MapReady_on_another_port_fails_the_instance_and_records_the_port()
    {
        await using var h = new ManagerHarness();
        var r = await h.RequestLevel();
        await h.Tick();
        var pod = (await h.Row(r.Id)).PodName!;
        h.Pods.SetReady(pod);
        await h.Pump();

        var result = await h.M.OnMapReady(r.Id, 27016);
        Assert.Equal(MapReadyOutcome.PortMismatch, result.Outcome);
        var row = await h.Row(r.Id);
        Assert.Equal((InstanceState.Failed, 27016), (row.State, row.Port));
        Assert.StartsWith("port_mismatch", row.Reason);
        var audit = (await h.Audits(r.Id)).Single(a => a.Action == "instance.port_mismatch");
        Assert.Contains("27016", audit.AfterJson);
        Assert.Contains("27015", audit.AfterJson);
    }

    [Fact]
    public async Task Port_mismatch_deletes_the_pod_and_runs_OnCrashed()
    {
        await using var h = new ManagerHarness();
        var r = await h.RequestLevel();
        await h.Tick();
        var row = await h.Row(r.Id);
        await h.M.OnMapReady(r.Id, 27020);
        Assert.Contains($"delete {row.PodName} {row.PodUid} 60s", h.Log);
        Assert.Equal(["OnCrashed"], h.Hooks.For(r.Id));
    }

    [Fact]
    public async Task Boot_timeout_fails_the_instance_at_the_deadline_and_not_before()
    {
        await using var h = new ManagerHarness();
        var r = await h.RequestLevel();
        await h.Tick();
        await h.Advance(h.Options.Instances.BootTimeout - TimeSpan.FromSeconds(1));
        await h.Tick();
        Assert.Equal(InstanceState.Booting, (await h.Row(r.Id)).State);

        await h.Advance(TimeSpan.FromSeconds(1));
        await h.Tick();
        var row = await h.Row(r.Id);
        Assert.Equal(InstanceState.Failed, row.State);
        Assert.StartsWith("boot_timeout", row.Reason);
    }

    [Fact]
    public async Task Boot_timeout_deletes_the_pod()
    {
        await using var h = new ManagerHarness();
        var r = await h.RequestLevel();
        await h.Tick();
        var row = await h.Row(r.Id);
        await h.Advance(h.Options.Instances.BootTimeout);
        await h.Tick();
        Assert.Contains($"delete {row.PodName} {row.PodUid} 60s", h.Log);
        await h.Advance(TimeSpan.FromSeconds(60));
        Assert.Null(h.Pods.Get(row.PodName!));
    }

    [Fact]
    public async Task Boot_timeout_does_not_touch_a_live_instance()
    {
        await using var h = new ManagerHarness();
        var row = await h.LiveLevel();
        await h.M.OnHeartbeat(row.Id, 1);
        await h.Advance(h.Options.Instances.BootTimeout);
        await h.M.OnHeartbeat(row.Id, 1);
        await h.Tick();
        Assert.Equal(InstanceState.Live, (await h.Row(row.Id)).State);
    }

    [Fact]
    public async Task Pod_create_failure_fails_the_instance()
    {
        await using var h = new ManagerHarness();
        var r = await h.RequestLevel();
        h.Pods.FailNextCreate = new InvalidOperationException("quota");
        await h.Tick();
        var row = await h.Row(r.Id);
        Assert.Equal(InstanceState.Failed, row.State);
        Assert.Contains("quota", row.Reason);
    }

    [Fact]
    public async Task Mod_image_with_another_label_is_refused_and_no_pod_is_created()
    {
        await using var h = new ManagerHarness(o => o.Instances.VerifyModLabel = true);
        h.Inspector = new FixedLabel("othermod");
        h.NewManager();
        var r = await h.RequestLevel();
        await h.Tick();
        var row = await h.Row(r.Id);
        Assert.Equal(InstanceState.Failed, row.State);
        Assert.Empty(h.Pods.Created);
        Assert.Contains("instance.mod_image_refused", await h.Actions(r.Id));
    }

    [Fact]
    public async Task Mod_image_with_the_mods_label_is_accepted()
    {
        await using var h = new ManagerHarness(o => o.Instances.VerifyModLabel = true);
        h.Inspector = new FixedLabel("descent");
        h.NewManager();
        var r = await h.RequestLevel();
        await h.Tick();
        Assert.Equal(InstanceState.Booting, (await h.Row(r.Id)).State);
    }

    [Fact]
    public async Task Fake_tier_skips_the_mod_image_check()
    {
        await using var h = new ManagerHarness(o => { o.Instances.VerifyModLabel = true; o.Instances.SkipModInit = true; });
        h.Inspector = new FixedLabel("othermod");
        h.NewManager();
        var r = await h.RequestLevel();
        await h.Tick();
        var row = await h.Row(r.Id);
        Assert.Equal(InstanceState.Booting, row.State);
        Assert.Null(row.ModImage);
    }

    [Fact]
    public async Task Admin_drain_sends_drain_and_audits_the_actor()
    {
        await using var h = new ManagerHarness();
        var row = await h.LiveLevel();
        await h.M.OnHeartbeat(row.Id, 2);
        await h.M.Drain(row.Id, "maintenance", "admin@localhost");
        Assert.Equal(InstanceState.Draining, (await h.Row(row.Id)).State);
        Assert.Equal(new InstanceCommand.Drain("maintenance"), h.Commands.Sent.Single().Command);
        Assert.Equal("admin@localhost", (await h.Audits(row.Id)).Single(a => a.Action == "instance.draining").Actor);
    }

    [Fact]
    public async Task Kick_is_audited_and_sent()
    {
        await using var h = new ManagerHarness();
        var row = await h.LiveLevel();
        await h.M.Kick(row.Id, "7656", "afk", "admin@localhost");
        Assert.Equal(new InstanceCommand.Kick("7656", "afk"), h.Commands.Sent.Single().Command);
        Assert.Contains("instance.kick", await h.Actions(row.Id));
    }

    [Fact]
    public async Task Exec_without_a_stream_is_refused_after_the_audit()
    {
        await using var h = new ManagerHarness();
        var row = await h.LiveLevel();
        h.Commands.Connected = false;
        var e = await Assert.ThrowsAsync<HostRefusal>(() => h.M.Exec(row.Id, "status", "admin@localhost"));
        Assert.Equal("no_stream", e.Reason);
        Assert.Contains("instance.exec", await h.Actions(row.Id));
    }

    [Fact]
    public void Terminal_states_have_no_transitions()
    {
        foreach (var from in new[] { InstanceState.Reaped, InstanceState.Crashed, InstanceState.Failed })
            foreach (var to in Enum.GetValues<InstanceState>())
                Assert.False(InstanceTransitions.IsAllowed(from, to), $"{from} → {to}");
        Assert.True(InstanceTransitions.IsAllowed(InstanceState.Booting, InstanceState.Live)); // positive arm
    }

    [Fact]
    public void Live_never_goes_back_to_booting()
    {
        Assert.False(InstanceTransitions.IsAllowed(InstanceState.Live, InstanceState.Booting));
    }

    sealed class FixedLabel(string? label) : IModImageInspector
    {
        public Task<string?> ModLabel(ImageRef image, CancellationToken ct = default) => Task.FromResult(label);
    }
}
