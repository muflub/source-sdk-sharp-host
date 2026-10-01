using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Instances;

namespace SourceSharp.Host.Instances.Tests;

/// <summary>§7.3 / §7.5: the hub is created at start, re-created with backoff, and stops after five failures in five minutes.</summary>
public class HubFacts
{
    static ManagerHarness Harness(Action<ServiceOptions>? more = null) => new(o => { o.Instances.Hubs = 1; more?.Invoke(o); });

    static async Task<int> HubRows(ManagerHarness h) => (await h.D.Read(tx => tx.Instances.List(InstanceKind.Hub))).Count;

    static async Task CrashHub(ManagerHarness h)
    {
        var hub = await h.Hub();
        h.Pods.DeleteExternally(hub.PodName!);
        await h.Pump();
        Assert.Equal(InstanceState.Crashed, (await h.Row(hub.Id)).State);
    }

    [Fact]
    public async Task Start_creates_the_hub_pod_when_none_is_live()
    {
        await using var h = Harness();
        await h.M.Start();
        var hub = await h.Hub();
        Assert.Equal(InstanceState.Booting, hub.State);
        var pod = h.Pods.Created.Single();
        Assert.Equal("hub", pod.Labels[GamePodLabels.Kind]);
        Assert.Equal(pod.Name, hub.PodName);
    }

    [Fact]
    public async Task Hubs_count_is_honoured()
    {
        await using var h = Harness(o => o.Instances.Hubs = 2);
        await h.M.Start();
        await h.Tick();
        Assert.Equal(2, await HubRows(h));
    }

    [Fact]
    public async Task Crashed_hub_is_recreated_after_one_second_and_not_before()
    {
        await using var h = Harness();
        await h.M.Start();
        await CrashHub(h);
        await h.Advance(TimeSpan.FromMilliseconds(999));
        await h.Tick();
        Assert.Equal(1, await HubRows(h));
        await h.Advance(TimeSpan.FromMilliseconds(1));
        await h.Tick();
        Assert.Equal(2, await HubRows(h));
        Assert.Equal(InstanceState.Booting, (await h.Hub()).State);
    }

    [Fact]
    public async Task Second_consecutive_failure_waits_two_seconds()
    {
        await using var h = Harness();
        await h.M.Start();
        await CrashHub(h);
        await h.Advance(TimeSpan.FromSeconds(1));
        await h.Tick();
        await CrashHub(h);
        await h.Advance(TimeSpan.FromMilliseconds(1999));
        await h.Tick();
        Assert.Equal(2, await HubRows(h));
        await h.Advance(TimeSpan.FromMilliseconds(1));
        await h.Tick();
        Assert.Equal(3, await HubRows(h));
    }

    [Fact]
    public async Task Backoff_doubles_from_one_second_to_thirty()
    {
        await using var h = Harness();
        Assert.Equal([1, 2, 4, 8, 16, 30, 30], Enumerable.Range(1, 7).Select(n => h.M.HubBackoff(n).TotalSeconds));
    }

    [Fact]
    public async Task Recreated_hub_is_a_new_row_with_a_new_pod()
    {
        await using var h = Harness();
        await h.M.Start();
        var first = await h.Hub();
        await CrashHub(h);
        await h.Advance(TimeSpan.FromSeconds(1));
        await h.Tick();
        var second = await h.Hub();
        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.PodName, second.PodName);
        Assert.Equal(2, h.Pods.Created.Count);
    }

    [Fact]
    public async Task Five_failures_in_five_minutes_stop_recreation_and_are_audited()
    {
        await using var h = Harness();
        await h.M.Start();
        for (var i = 1; i <= 5; i++)
        {
            await CrashHub(h);
            if (i < 5) { await h.Advance(h.M.HubBackoff(i)); await h.Tick(); }
        }
        Assert.True(h.M.HubsStopped);
        await h.Advance(TimeSpan.FromMinutes(10));
        await h.Tick();
        Assert.Equal(5, await HubRows(h));
        Assert.Equal(1, await h.D.Read(tx => tx.Audit.Count(new AuditQuery(Action: "hub.recreate_stopped"))));
    }

    [Fact]
    public async Task Four_failures_do_not_stop_recreation()
    {
        await using var h = Harness();
        await h.M.Start();
        for (var i = 1; i <= 4; i++)
        {
            await CrashHub(h);
            await h.Advance(h.M.HubBackoff(i));
            await h.Tick();
        }
        Assert.False(h.M.HubsStopped);
        Assert.Equal(5, await HubRows(h));
    }

    [Fact]
    public async Task Failures_spread_beyond_the_window_do_not_stop_recreation()
    {
        await using var h = Harness();
        await h.M.Start();
        for (var i = 1; i <= 6; i++)
        {
            await CrashHub(h);
            await h.Advance(TimeSpan.FromMinutes(2));
            await h.Tick();
        }
        Assert.False(h.M.HubsStopped);
        Assert.Equal(7, await HubRows(h));
    }

    [Fact]
    public async Task Resume_after_a_stop_creates_the_hub_again()
    {
        await using var h = Harness();
        await h.M.Start();
        for (var i = 1; i <= 5; i++)
        {
            await CrashHub(h);
            if (i < 5) { await h.Advance(h.M.HubBackoff(i)); await h.Tick(); }
        }
        await h.M.ResumeHubs("admin@localhost");
        await h.Tick();
        Assert.Equal(6, await HubRows(h));
        Assert.False(h.M.HubsStopped);
    }

    [Fact]
    public async Task Draining_the_hub_creates_its_replacement_at_once()
    {
        await using var h = Harness();
        await h.M.Start();
        var old = await h.BringLive(await h.Hub());
        await h.M.OnHeartbeat(old.Id, 3);
        await h.M.Drain(old.Id, "mod image changed", "admin@localhost");
        await h.Tick();
        var replacement = await h.Hub();
        Assert.NotEqual(old.Id, replacement.Id);
        Assert.Equal(InstanceState.Draining, (await h.Row(old.Id)).State);
    }

    [Fact]
    public async Task A_drained_hub_reap_is_not_a_failure()
    {
        await using var h = Harness();
        await h.M.Start();
        var old = await h.BringLive(await h.Hub());
        await h.M.Drain(old.Id, "replace", "admin@localhost");
        await h.Tick(); // replacement created, old reaped (0 players)
        Assert.Equal(InstanceState.Reaped, (await h.Row(old.Id)).State);
        Assert.True(h.M.HubNextAttempt <= h.Clock.GetUtcNow());
    }

    [Fact]
    public async Task Hub_going_live_resets_the_backoff()
    {
        await using var h = Harness();
        await h.M.Start();
        await CrashHub(h);
        await h.Advance(TimeSpan.FromSeconds(1));
        await h.Tick();
        await CrashHub(h);
        await h.Advance(TimeSpan.FromSeconds(2));
        await h.Tick();
        await h.BringLive(await h.Hub());
        await CrashHub(h);
        Assert.Equal(h.Clock.GetUtcNow() + TimeSpan.FromSeconds(1), h.M.HubNextAttempt);
    }
}
