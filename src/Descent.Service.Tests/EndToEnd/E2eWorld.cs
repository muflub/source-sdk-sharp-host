using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Descent.Service.Api;
using Descent.Service.Travel;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.FakeGame;
using SourceSharp.Host.FakeGame.Control;
using SourceSharp.Host.Gateway;
using SourceSharp.Host.Gateway.Relay;
using Client = SourceSharp.Host.FakeClient.FakeClient;

namespace Descent.Service.Tests.EndToEnd;

/// <summary>
/// The whole path in one process (plan §7.6, §10): the real service with its real instance manager,
/// travel and module registry, fake game servers as pods (<see cref="FakeGameInstanceHost"/>), the real
/// gateway (Host.Gateway) with its UDP relay, and FakeClients that only ever talk to the gateway's
/// public endpoint. The pool is seeded with ready levels (the map files are irrelevant to the fake).
/// </summary>
public sealed class E2eWorld : IAsyncDisposable
{
    public static readonly TimeSpan T = TimeSpan.FromSeconds(30);

    public required WebApplication Service { get; init; }
    public required WebApplication Gateway { get; init; }
    public required FakeGameInstanceHost Pods { get; init; }
    public required IPEndPoint Public { get; init; }
    public required string ModulesDir { get; init; }
    public IHostData Data => Service.Services.GetRequiredService<IHostData>();
    public IInstanceLifecycle Lifecycle => Service.Services.GetRequiredService<IInstanceLifecycle>();
    public TravelCoordinator Travel => Service.Services.GetRequiredService<TravelCoordinator>();
    public CheckingHops Hops => (CheckingHops)Service.Services.GetRequiredService<IHopListener>();
    readonly List<Client> _clients = [];
    readonly List<GrpcChannel> _channels = [];
    long _packId;

    /// <summary>
    /// The game API, gateway API and gateway control ports must be fixed before the start (pods are
    /// told the service's address, the gateway and the service each dial the other), so they are
    /// picked free and released, and something else can take one before Kestrel binds it. A bind
    /// that fails with "address already in use" tears down what started and starts again on fresh ports.
    /// </summary>
    /// <param name="pickPort">Picks each fixed port (the facts' seam for a port taken before the bind).</param>
    public static async Task<E2eWorld> Start(Dictionary<string, string?>? settings = null, Func<int>? pickPort = null)
    {
        pickPort ??= TestService.FreePort;
        for (var attempt = 1; ; attempt++)
        {
            try { return await StartOnce(settings, pickPort); }
            catch (IOException e) when (attempt < 5 && e.InnerException is Microsoft.AspNetCore.Connections.AddressInUseException) { }
        }
    }

    static async Task<E2eWorld> StartOnce(Dictionary<string, string?>? settings, Func<int> pickPort)
    {
        var gameApi = pickPort();
        var gatewayApi = pickPort();
        var gatewayControl = pickPort();
        var modules = Directory.CreateTempSubdirectory("e2e-modules-").FullName;
        var pods = new FakeGameInstanceHost();
        pods.ExtraEnv["FAKEGAME_HeartbeatMs"] = "200";
        pods.ExtraEnv["FAKEGAME_MainStallMs"] = "600";
        pods.ExtraEnv["FAKEGAME_PlayerIdleMs"] = "60000";
        var all = new Dictionary<string, string?>
        {
            ["Listen:Admin"] = "127.0.0.1:0", ["Listen:Internal"] = "127.0.0.1:0", ["Listen:FastDl"] = "127.0.0.1:0",
            ["Listen:GameApi"] = $"127.0.0.1:{gameApi}", ["Listen:GatewayApi"] = $"127.0.0.1:{gatewayApi}",
            ["Listen:GatewayControl"] = $"http://127.0.0.1:{gatewayControl}",
            ["Data:Path"] = ":memory:",
            ["MapPool:MapsPath"] = Path.Combine(Path.GetTempPath(), $"e2e-maps-{Guid.NewGuid():N}"),
            ["MapPool:DefaultPerDepth"] = "0",
            ["Modules:Path"] = modules,
            ["Instances:ServiceAddress"] = $"127.0.0.1:{gameApi}",
            ["Instances:EngineImage"] = "descent-fake:test",
            ["Instances:SkipModInit"] = "true",
            ["Instances:VerifyModLabel"] = "false",
            ["Instances:HeartbeatInterval"] = "00:00:01",
            ["Instances:TickInterval"] = "00:00:00.100",
            ["Instances:BootTimeout"] = "00:00:30",
            ["Instances:EmptyGrace"] = "00:01:00",
            ["Instances:ReapGrace"] = "00:00:01",
            // Not 27015: on a host running k3s (the live cluster), UDP to any 127.x:27015 is taken by its
            // service load balancer and never reaches a local socket (measured: 29005 -> 27015 lost, -> 28015 delivered).
            ["Instances:GamePort"] = "28015",
            // The fake hosts the relay in a managed process: nothing can interpose the engine's sockets.
            ["Instances:RelayMode"] = "Loopback",
        };
        foreach (var (k, v) in settings ?? []) all[k] = v;
        var service = TestService.Build(all, b =>
        {
            b.Services.AddSingleton<IInstanceHost>(pods);
            b.Services.AddSingleton<IHopListener>(sp => new CheckingHops(sp.GetRequiredService<TravelCoordinator>(), sp.GetRequiredService<IHostData>()));
        });
        var gateway = GatewayApp.Build(["--environment", "Development"], b => b.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GatewayId"] = "gw-e2e", ["Public"] = "127.0.0.1:0", ["Control"] = $"127.0.0.1:{gatewayControl}", ["Health"] = "127.0.0.1:0",
            ["Service"] = $"http://127.0.0.1:{gatewayApi}", ["HandshakesPerSecond"] = "1000", ["PingInterval"] = "00:00:00.500",
            ["Serilog:MinimumLevel:Default"] = "Warning",
        }));
        try
        {
            await service.StartAsync();
            await gateway.StartAsync();
        }
        catch (IOException)
        {
            // A port was taken: stop whatever started (the manager may already have created the hub's pod).
            await gateway.DisposeAsync();
            await pods.DisposeAsync();
            try { await service.StopAsync(); } catch (Exception) { }
            await service.DisposeAsync();
            try { Directory.Delete(modules, true); } catch (IOException) { }
            throw;
        }
        var world = new E2eWorld
        {
            Service = service, Gateway = gateway, Pods = pods, ModulesDir = modules,
            Public = gateway.Services.GetRequiredService<GatewayRelay>().PublicEndPoint,
        };
        await world.Until(async () => (await world.Hub()) is { State: InstanceState.Live }, "the hub live");
        var relay = gateway.Services.GetRequiredService<GatewayRelay>();
        await world.Until(async () => relay.DefaultBackend?.ToString() == $"{(await world.Hub())!.PodIp}:5010", $"the gateway's default route to the hub (has {relay.DefaultBackend})");
        return world;
    }

    // ------------------------------------------------------------------ reading the world

    public async Task<InstanceRecord?> Hub() =>
        (await Data.ReadAsync((tx, _) => tx.Instances.NonTerminal())).FirstOrDefault(i => i.Kind == InstanceKind.Hub);

    public Task<InstanceRecord?> Instance(string id) => Data.ReadAsync((tx, _) => tx.Instances.Get(id));

    /// <summary>Items in play: every Live item but the reserves (minted at a level lease, swept at its release).</summary>
    public Task<int> LiveOutsideReserves() => Data.ReadAsync(async (tx, _) =>
        await tx.Items.Count(new SourceSharp.Host.Abstractions.ItemQuery()) - await tx.Items.Count(new SourceSharp.Host.Abstractions.ItemQuery(OwnerKind: OwnerKind.Reserve)));

    public Task<IReadOnlyList<ItemRecord>> Backpack(string characterId) =>
        Data.ReadAsync((tx, _) => tx.Items.Query(new SourceSharp.Host.Abstractions.ItemQuery(OwnerKind: OwnerKind.Character, OwnerId: characterId)));

    public FakeGameControl.FakeGameControlClient Control(string instanceId)
    {
        var game = Pods.Game(instanceId) ?? throw new InvalidOperationException($"no fake for {instanceId}");
        var ch = GrpcChannel.ForAddress($"http://{game.ControlEndPoint}");
        _channels.Add(ch);
        return new FakeGameControl.FakeGameControlClient(ch);
    }

    public async Task<FakeGameControl.FakeGameControlClient> HubControl()
    {
        var hub = await Hub() ?? throw new InvalidOperationException("no hub");
        await Until(() => Task.FromResult(Pods.Game(hub.Id) is not null), "the hub's fake");
        return Control(hub.Id);
    }

    // ------------------------------------------------------------------ clients

    public Client Client(ulong steamId)
    {
        var c = new Client(Public, steamId, $"p{steamId}") { ResendInterval = TimeSpan.FromMilliseconds(500) };
        _clients.Add(c);
        return c;
    }

    /// <summary>A client connected through the gateway, and its character leased by the instance it landed on.</summary>
    public async Task<(Client Client, string CharacterId)> Joined(ulong steamId)
    {
        var c = Client(steamId);
        var instance = await c.ConnectAsync(T);
        var control = Control(instance);
        PlayerReply? p = null;
        await Until(async () => (p = await control.PlayerAsync(new PlayerRequest { Steamid = steamId.ToString() })).Leased, $"{steamId} leased on {instance}");
        return (c, p!.CharacterId);
    }

    // ------------------------------------------------------------------ the pool

    public async Task SeedLevels(int depth, params string[] hashes)
    {
        await Data.WriteAsync(async (tx, _) =>
        {
            if (_packId == 0)
                _packId = (await tx.Library.AddPack(new PackRecord(0, "descent", "crypt", 1, "p1", "v1", "bake", "e2e", tx.Now, 1, "sha", null, null, PackState.Ready))).Id;
            if (await tx.Library.Assignment("descent", depth) is null)
                await tx.Library.Assign(new PackAssignment("descent", depth, _packId, false, "e2e", tx.Now, OldLevelsPolicy.Drain));
            foreach (var h in hashes)
                await tx.Levels.Add(new LevelRecord(h, _packId, depth, "crypt", (ulong)h.GetHashCode(), 0, LevelState.Ready, tx.Now, null, 1, null, null));
            return true;
        });
    }

    // ------------------------------------------------------------------ waiting

    public async Task Until(Func<Task<bool>> condition, string what, TimeSpan? timeout = null)
    {
        var sw = Stopwatch.StartNew();
        var limit = timeout ?? T;
        while (true)
        {
            try { if (await condition()) return; }
            catch (Grpc.Core.RpcException) { /* a fake not up yet */ }
            if (sw.Elapsed > limit) throw new TimeoutException($"{what} never held; hub events: {await HubEvents()}");
            await Task.Delay(20);
        }
    }

    async Task<string> HubEvents()
    {
        try
        {
            var hub = await Hub();
            return hub is null || Pods.Game(hub.Id) is not { } g ? "(none)" : string.Join(" | ", g.Server.Events.TakeLast(12));
        }
        catch (Exception e) { return e.Message; }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _clients) c.Dispose();
        foreach (var ch in _channels) ch.Dispose();
        await Gateway.StopAsync();
        await Gateway.DisposeAsync();
        await Pods.DisposeAsync();
        await Service.StopAsync();
        await Service.DisposeAsync();
        try { Directory.Delete(ModulesDir, true); } catch (IOException) { }
    }
}

/// <summary>
/// Travel's HopReady, observed at the service: for every hop, whether the travelling character still
/// held a lease on the source instance when HopReady arrived (PrepareHop's contract: checkpoint and
/// release first), and the character's version then. Delegates to the real coordinator.
/// </summary>
public sealed class CheckingHops(TravelCoordinator travel, IHostData data) : IHopListener
{
    public ConcurrentQueue<(string HopId, string SteamId, bool LeaseHeldBySource, long Version)> Seen { get; } = new();

    public async Task HopReady(InstanceRecord caller, string hopId, string steamId, CancellationToken ct)
    {
        var (held, version) = await data.ReadAsync(async (tx, _) =>
        {
            var held = false; long version = -1;
            foreach (var ch in await tx.Characters.List(steamId))
            {
                version = Math.Max(version, ch.Version);
                if ((await tx.Leases.Get(ch.Id))?.InstanceId == caller.Id) held = true;
            }
            return (held, version);
        }, ct);
        Seen.Enqueue((hopId, steamId, held, version));
        await travel.HopReady(caller, hopId, steamId, ct);
    }
}
