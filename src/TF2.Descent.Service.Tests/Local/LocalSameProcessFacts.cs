using Microsoft.Extensions.DependencyInjection;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.FakeGame;
using SourceSharp.Host.FakeGame.Control;
using SourceSharp.Host.Testing;

namespace TF2.Descent.Service.Tests.Local;

/// <summary>
/// D-H13's same-process travel end to end: the real service in its local profile, one fake game
/// started from local-host.json, and a descent and a town portal served by that one process. The
/// process becomes each target instance in turn; the instance it left is reaped, never crashed.
/// </summary>
public sealed class LocalSameProcessFacts : IAsyncDisposable
{
    const string Steam = "76561198000000001";
    static readonly TimeSpan T = TimeSpan.FromSeconds(30);

    readonly string _dir = Path.Combine(Path.GetTempPath(), $"sourcesharp-local-sp-{Guid.NewGuid():N}");
    readonly int _port = TestService.FreePort();
    Microsoft.AspNetCore.Builder.WebApplication? _service;
    FakeGameHost? _game;

    IHostData Data => _service!.Services.GetRequiredService<IHostData>();

    async Task<FakeGameHost> Start()
    {
        var settings = LocalProfile.Settings(_dir);
        settings["Listen:GameApi"] = TestLoopback.Endpoint(); // this process's own loopback address (TestLoopback)
        settings["Listen:Admin"] = TestLoopback.Endpoint();
        settings["Instances:HeartbeatInterval"] = "00:00:01";
        settings["Instances:TickInterval"] = "00:00:00.100";
        settings["MapPool:DefaultPerDepth"] = "0";
        // Not 27015: on a host running k3s, UDP to 127.x:27015 is taken by its load balancer (E2eWorld).
        settings["Instances:GamePort"] = _port.ToString();
        _service = TestService.Build(settings);
        await _service.StartAsync();

        var file = Path.Combine(_dir, "local-host.json");
        await Until(() => Task.FromResult(File.Exists(file)), "local-host.json");
        await SeedLevel(1, "L1");

        _game = await FakeGameHost.StartAsync(["-hostlocal", file, "+map", "tf2_descent_town", "-port", _port.ToString()], new Dictionary<string, string?>
        {
            ["FAKEGAME_HeartbeatMs"] = "200", ["FAKEGAME_MainStallMs"] = "600", ["FAKEGAME_PlayerIdleMs"] = "60000",
            ["FAKEGAME_ControlListen"] = TestLoopback.Endpoint(),
            ["RELAY_Mode"] = "Loopback", ["RELAY_Listen"] = TestLoopback.Endpoint(),
            ["RELAY_InfoListen"] = TestLoopback.Endpoint(), ["RELAY_Health"] = TestLoopback.Endpoint(),
            ["RELAY_EngineEndpoint"] = $"{TestLoopback.Host}:{_port}", ["RELAY_AddressPool"] = $"127.250.{Environment.ProcessId % 256}.0/24",
        });
        await Until(() => Task.FromResult(_game.Server.MapIsReady), "the hub's map ready");
        var joined = await _game.Control.JoinAsync(new JoinRequest { Steamid = Steam, Name = "Ann" });
        Assert.True(joined.Leased, joined.Reason);
        return _game;
    }

    /// <summary>A ready level in the pool, and its files in the local store the connection file points at.</summary>
    async Task SeedLevel(int depth, string hash)
    {
        await Data.WriteAsync(async (tx, _) =>
        {
            var pack = (await tx.Library.AddPack(new PackRecord(0, "tf2_descent", "crypt", 1, "p1", "v1", "bake", "local", tx.Now, 1, "sha", null, null, PackState.Ready))).Id;
            await tx.Library.Assign(new PackAssignment("tf2_descent", depth, pack, false, "local", tx.Now, OldLevelsPolicy.Drain));
            await tx.Levels.Add(new LevelRecord(hash, pack, depth, "crypt", 7, 0, LevelState.Ready, tx.Now, null, 1, null, null));
            return true;
        });
        var levels = Path.Combine(_dir, "maps", "levels");
        Directory.CreateDirectory(levels);
        foreach (var e in new[] { ".bsp", ".nav3d", ".map2d" }) File.WriteAllText(Path.Combine(levels, $"tf2_descent-{depth}-{hash}{e}"), e);
    }

    static async Task Until(Func<Task<bool>> condition, string what)
    {
        var until = DateTime.UtcNow + T;
        while (!await condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException($"timed out waiting for {what}");
            await Task.Delay(50);
        }
    }

    Task<InstanceRecord?> Row(string id) => Data.ReadAsync((tx, _) => tx.Instances.Get(id));

    async Task<string?> LeasedOn(string steam)
    {
        var p = await _game!.Control.PlayerAsync(new PlayerRequest { Steamid = steam });
        return p.CharacterId.Length == 0 ? null : (await Data.ReadAsync((tx, _) => tx.Leases.Get(p.CharacterId)))?.InstanceId;
    }

    async Task<string> Travel(Func<FakeGameControl.FakeGameControlClient, Task<TravelReply>> ask)
    {
        var reply = await ask(_game!.Control);
        Assert.True(reply.Ok, $"{reply.State} {reply.Reason}");
        var target = reply.InstanceId;
        await Until(async () => _game.Server.Sdk.Endpoint.InstanceId == target && _game.Server.MapIsReady && await LeasedOn(Steam) == target,
            $"the process to become {target} with {Steam} leased there (events: {string.Join(" | ", _game.Server.Events.TakeLast(8))})");
        return target;
    }

    [Fact]
    public async Task One_process_descends_and_becomes_the_level_instance()
    {
        var game = await Start();
        var hub = game.Server.Sdk.Endpoint.InstanceId;
        var level = await Travel(c => c.DescendAsync(new TravelRequest { Steamid = Steam, Depth = 1 }).ResponseAsync);

        Assert.NotEqual(hub, level);
        Assert.Equal(("level", 1, "tf2_descent-1-L1"), (game.Server.Boot!.InstanceKind, game.Server.Boot.Depth, game.Server.Map));
        Assert.Equal(InstanceState.Live, (await Row(level))!.State);
        await Until(async () => (await Row(hub))!.State == InstanceState.Reaped, "the hub the process left reaped");
        Assert.Equal("same_process_hop", (await Row(hub))!.Reason);
    }

    [Fact]
    public async Task One_process_goes_down_and_back_up_through_a_town_portal()
    {
        var game = await Start();
        var hub = game.Server.Sdk.Endpoint.InstanceId;
        var level = await Travel(c => c.DescendAsync(new TravelRequest { Steamid = Steam, Depth = 1 }).ResponseAsync);
        var town = await Travel(c => c.TownPortalAsync(new TravelRequest { Steamid = Steam }).ResponseAsync);

        Assert.NotEqual(hub, town); // the hub this process left was replaced; the process became the new one
        Assert.Equal(("hub", "tf2_descent_town"), (game.Server.Boot!.InstanceKind, game.Server.Map));
        await Until(async () => (await Row(level))!.State == InstanceState.Reaped, "the level the process left reaped");
        var crashed = await Data.ReadAsync((tx, _) => tx.Audit.List(new AuditQuery(Action: "instance.crashed", Take: 50)));
        Assert.Empty(crashed);
    }

    public async ValueTask DisposeAsync()
    {
        if (_game is not null) await _game.DisposeAsync();
        if (_service is not null) { await _service.StopAsync(); await _service.DisposeAsync(); }
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
