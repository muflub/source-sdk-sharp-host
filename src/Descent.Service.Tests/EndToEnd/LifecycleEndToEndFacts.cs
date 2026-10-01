using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;
using SourceSharp.Host.FakeGame.Control;
using SourceSharp.Host.Testing;

namespace Descent.Service.Tests.EndToEnd;

/// <summary>Plan §7.6's in-process gate: the instance lifecycle with fake game pods (create → live → drain → reap, Hang, Crash).</summary>
public class LifecycleEndToEndFacts
{
    static async Task<InstanceRecord> LiveLevel(E2eWorld w)
    {
        await w.SeedLevels(1, "L1-a");
        var r = await w.Lifecycle.RequestLevel(1, null, "L1-a", null);
        var id = ((LevelRequestResult.Accepted)r).Instance.Id;
        await w.Until(async () => (await w.Instance(id))!.State == InstanceState.Live, "the level live");
        return (await w.Instance(id))!;
    }

    static Task<int> Audits(E2eWorld w, string action, string target) =>
        w.Data.ReadAsync((tx, _) => tx.Audit.Count(new AuditQuery(Action: action, Target: target)));

    [Fact]
    public async Task The_world_starts_when_a_port_it_picked_is_taken_before_the_bind()
    {
        // FreePort binds port 0, reads the port and releases it: anything may take it before Kestrel
        // binds it (another fact's outgoing connection draws from the same ephemeral range). Here a
        // listener takes the first pick (the game API) in that window.
        System.Net.Sockets.TcpListener? squatter = null;
        var picks = 0;
        int Pick()
        {
            var port = TestService.FreePort();
            if (Interlocked.Increment(ref picks) == 1)
            {
                squatter = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
                squatter.Start();
            }
            return port;
        }
        try
        {
            await using var w = await E2eWorld.Start(pickPort: Pick);
            Assert.True(picks > 3, $"{picks} picks: the taken port was never replaced");
            Assert.Equal(InstanceState.Live, (await w.Hub())!.State);
        }
        finally { squatter?.Stop(); }
    }

    [Fact]
    public async Task A_level_is_created_goes_live_is_drained_and_reaped_with_its_world_swept()
    {
        await using var w = await E2eWorld.Start();
        var level = await LiveLevel(w);
        var game = w.Pods.Game(level.Id)!;
        Assert.Equal("level", game.Server.Boot!.InstanceKind);
        var rolled = new FakeGameRules().RollItem(new ItemRollContext("scout", 1, 1, 5, 0, "depth1", "e2e"), 7);
        var item = await w.Data.WriteAsync((tx, _) => tx.Items.Mint(new NewItem(rolled.Seed, rolled.BaseType, rolled.Rarity, rolled.ItemLevel, 1, true,
            rolled.Instance, 1, 0, 1), OwnerKind.World, level.Id, level.Id, -1, "e2e", null));

        await w.Lifecycle.Drain(level.Id, "e2e", "test");
        await w.Until(async () => (await w.Instance(level.Id))!.State == InstanceState.Reaped, "the level reaped");
        await w.Until(() => Task.FromResult(w.Pods.Get(level.PodName!) is null), "the pod deleted");

        Assert.Equal(1, await Audits(w, "instance.sweep", level.Id));
        Assert.Equal(ItemState.Swept, (await w.Data.ReadAsync((tx, _) => tx.Items.Get(item.Id)))!.State);
        Assert.Contains("drain e2e", game.Server.Events);
        Assert.Equal(0, await game.Exited); // the delete stopped it cleanly
        Assert.Contains(w.Pods.Calls, c => c == ("delete", level.PodName!));
    }

    [Fact]
    public async Task A_hung_level_goes_suspect_and_is_reaped()
    {
        await using var w = await E2eWorld.Start();
        var level = await LiveLevel(w);
        Assert.True((await w.Control(level.Id).HangAsync(new Empty())).Ok);
        await w.Until(async () => await Audits(w, "instance.suspect", level.Id) > 0, "suspect");
        await w.Until(async () => (await w.Instance(level.Id))!.State == InstanceState.Reaped, "reaped");
        var row = (await w.Instance(level.Id))!;
        Assert.StartsWith("heartbeats_lost", row.Reason);
        await w.Until(() => Task.FromResult(w.Pods.Get(level.PodName!) is null), "the pod deleted");
    }

    [Fact]
    public async Task A_crashed_hub_is_crashed_and_its_leases_are_released()
    {
        await using var w = await E2eWorld.Start();
        var hub = (await w.Hub())!;
        var control = await w.HubControl();
        var joined = await control.JoinAsync(new JoinRequest { Steamid = "76561198000000009" });
        Assert.True(joined.Ok, joined.Reason);
        Assert.Equal(hub.Id, (await w.Data.ReadAsync((tx, _) => tx.Leases.Get(joined.CharacterId)))!.InstanceId); // held before

        var game = w.Pods.Game(hub.Id)!;
        Assert.True((await control.CrashAsync(new CrashRequest { ExitCode = 3 })).Ok);
        await w.Until(async () => (await w.Instance(hub.Id))!.State == InstanceState.Crashed, "crashed");
        Assert.Equal(3, await game.Exited);
        // Whichever the service saw first: the stream dying with the process, or the pod failing.
        Assert.Contains((await w.Instance(hub.Id))!.Reason, new[] { "stream_closed", "pod_failed" });
        // The crash transition is written first, its effects (leases, reserves, routes) right after.
        await w.Until(async () => await w.Data.ReadAsync((tx, _) => tx.Leases.Get(joined.CharacterId)) is null, "the lease released");
        // The manager replaces the hub.
        await w.Until(async () => (await w.Hub()) is { State: InstanceState.Live } h && h.Id != hub.Id, "a new hub live");
    }
}
