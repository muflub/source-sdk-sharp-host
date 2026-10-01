using System.Collections.Concurrent;
using Descent.Service;
using Descent.Service.Api;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Instances;
using SourceSharp.Host.Testing;
using P = SourceSharp.Host.Proto;

namespace SourceSharp.Host.Sdk.Tests;

/// <summary>
/// The real service in-process on free loopback ports (as Descent.Service.Tests' ApiHarness):
/// :memory: data, FakeInstanceHost, FakeGameRules, instance rows with known tokens so every SDK
/// call passes the real auth interceptor. Recording fakes stand in for the pieces other lanes
/// wire (module registry, gateway sessions, travel), and a TCP proxy in front of the game API
/// lets a fact cut or block the SDK's connection.
/// </summary>
public sealed class SdkHarness : IAsyncDisposable
{
    public required WebApplication App { get; init; }
    public required FakeGameRules Rules { get; init; }
    public required SdkTcpProxy Proxy { get; init; }
    public FakeTimeProvider? ServiceClock { get; init; }
    public BoundListeners Bound => App.Services.GetRequiredService<BoundListeners>();
    public IHostData Data => App.Services.GetRequiredService<IHostData>();
    public InstanceStreams Streams => App.Services.GetRequiredService<InstanceStreams>();
    public RecordingModules Modules => (RecordingModules)App.Services.GetRequiredService<IModuleBoot>();
    public RecordingSessions Sessions => (RecordingSessions)App.Services.GetRequiredService<IPlayerSessions>();
    public RecordingHops Hops => (RecordingHops)App.Services.GetRequiredService<IHopListener>();
    public InstanceChatter Chatter => App.Services.GetRequiredService<InstanceChatter>();
    public RecordingTripPlanner TravelApi => App.Services.GetRequiredService<RecordingTripPlanner>();
    public Uri Direct => new($"http://127.0.0.1:{Bound.Port(ListenerRole.GameApi)}");
    readonly List<HostSdk> _sdks = [];

    public bool OwnsProxy { get; init; } = true;
    public required FakeInstanceHost Pods { get; init; }

    /// <param name="dataPath">A database file, for facts that restart the service on the same data (":memory:" otherwise).</param>
    /// <param name="pods">The pod store to share across a restart (adoption lists it at start).</param>
    /// <param name="proxy">An existing proxy to point at this service (a restart keeps the SDK's address).</param>
    public static async Task<SdkHarness> Start(bool fakeServiceClock = false, Dictionary<string, string?>? settings = null,
        string? dataPath = null, FakeInstanceHost? pods = null, SdkTcpProxy? proxy = null)
    {
        var rules = new FakeGameRules();
        pods ??= new FakeInstanceHost(TimeProvider.System);
        var clock = fakeServiceClock ? new FakeTimeProvider(DateTimeOffset.UtcNow) : null;
        var all = new Dictionary<string, string?>
        {
            ["Listen:Admin"] = "127.0.0.1:0", ["Listen:Internal"] = "127.0.0.1:0", ["Listen:GameApi"] = "127.0.0.1:0",
            ["Listen:GatewayApi"] = "127.0.0.1:0", ["Listen:FastDl"] = "127.0.0.1:0",
            ["Data:Path"] = dataPath ?? ":memory:",
            ["MapPool:MapsPath"] = Path.Combine(Path.GetTempPath(), $"descent-sdk-maps-{Guid.NewGuid():N}"),
            ["Instances:VerifyModLabel"] = "false",
        };
        foreach (var (k, v) in settings ?? []) all[k] = v;
        var app = ServiceApp.Build(["--environment", "Development"], b =>
        {
            b.Configuration.AddInMemoryCollection(all);
            if (clock is not null) b.Services.AddSingleton<TimeProvider>(clock);
            b.Services.AddSingleton<IInstanceHost>(pods);
            b.Services.AddSingleton<IRulesProvider>(new SingleRulesProvider(rules));
            b.Services.AddSingleton<IModuleBoot, RecordingModules>();
            b.Services.AddSingleton<IPlayerSessions, RecordingSessions>();
            b.Services.AddSingleton<IHopListener, RecordingHops>();
            b.Services.AddSingleton<RecordingTripPlanner>();
            b.Services.AddSingleton<Descent.Service.Travel.ITripPlanner>(sp => sp.GetRequiredService<RecordingTripPlanner>());
        });
        await app.StartAsync();
        // The manager adopts at start, in the background: a row added before it finishes has no pod
        // and is crashed ("adoption: no pod"). Its last start step ensures the hub, so wait for a hub row.
        var data = app.Services.GetRequiredService<IHostData>();
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!(await data.ReadAsync((tx, _) => tx.Instances.NonTerminal())).Any(r => r.Kind == InstanceKind.Hub))
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("the instance manager never ensured its hub");
            await Task.Delay(10);
        }
        var port = app.Services.GetRequiredService<BoundListeners>().Port(ListenerRole.GameApi);
        proxy?.Retarget(port);
        return new SdkHarness { App = app, Rules = rules, ServiceClock = clock, Pods = pods, Proxy = proxy ?? new SdkTcpProxy(port), OwnsProxy = proxy is null };
    }

    public static string Token(string id) => $"token-{id}";

    /// <summary>An instance row with a known token, as the manager would have created it.</summary>
    public Task<InstanceRecord> Instance(string id, InstanceKind kind, int depth = 0, InstanceState state = InstanceState.Live)
    {
        var now = DateTimeOffset.UtcNow;
        return Data.WriteAsync((tx, _) => tx.Instances.Add(new InstanceRecord(id, kind, state, depth, null, null,
            $"descent-{id}", $"uid-{id}", "127.0.0.1", 27015, InstanceManager.TokenHash(Token(id)), null, null, "fake", 4321, now,
            now, state == InstanceState.Live ? now : null, null, null, null, null, now, 0, 27015)));
    }

    /// <summary>
    /// Fast timers for facts: heartbeats every 100 ms, short retries. The ack-silence budget is
    /// 3 s, not 3 heartbeats: a loaded test host acks hundreds of ms late, and an SDK that gives
    /// up on that live stream closes it, which the service treats as a crash (§6.1), so every
    /// later call is bad_token (SessionFacts.A_service_that_acks_late_keeps_the_stream_and_its_row).
    /// </summary>
    public static HostSdkOptions FastOptions() => new()
    {
        HeartbeatInterval = TimeSpan.FromMilliseconds(100),
        MissedAcksBeforeReconnect = 30,
        ReconnectBackoff = TimeSpan.FromMilliseconds(50),
        ReconnectBackoffMax = TimeSpan.FromMilliseconds(200),
        RetryBackoff = TimeSpan.FromMilliseconds(50),
        RetryBackoffMax = TimeSpan.FromMilliseconds(200),
        RpcTimeout = TimeSpan.FromSeconds(5),
        ConnectTimeout = TimeSpan.FromSeconds(1),
        LogFlushInterval = TimeSpan.FromMilliseconds(50),
        ModuleSource = new FixedModuleSource(null),
    };

    /// <summary>An SDK connected as <paramref name="instanceId"/>, through the proxy unless told otherwise.</summary>
    public HostSdk Sdk(string instanceId, HostSdkOptions? options = null, bool direct = false, Uri? sidecar = null)
    {
        var sdk = HostSdk.Connect(new HostEndpoint(HostMode.Pod, direct ? Direct : Proxy.Uri, instanceId, Token(instanceId), sidecar),
            options ?? FastOptions());
        _sdks.Add(sdk);
        return sdk;
    }

    /// <summary>Stops the service and nothing else: the SDKs and the proxy stay (a service restart).</summary>
    public async Task StopServiceOnly()
    {
        _sdks.Clear();
        await App.StopAsync();
        await App.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _sdks) await s.DisposeAsync();
        if (OwnsProxy) await Proxy.DisposeAsync();
        await App.StopAsync();
        await App.DisposeAsync();
    }
}

/// <summary>The module handshake's host side as a recorder: answers what the fact says, keeps what was announced and uploaded.</summary>
public sealed class RecordingModules : IModuleBoot
{
    public P.ModuleAnswer Answer { get; set; } = P.ModuleAnswer.Known;
    public ConcurrentQueue<P.RulesModule> Announced { get; } = new();
    public ConcurrentDictionary<string, byte[]> Uploaded { get; } = new();
    public ConcurrentQueue<P.ModuleChunk> Chunks { get; } = new();
    int _uploads;
    public int Uploads => _uploads;

    public Task<P.ModuleAnswer> Announce(InstanceRecord caller, P.RulesModule module, CancellationToken ct)
    {
        Announced.Enqueue(module);
        return Task.FromResult(Answer);
    }

    public async Task<P.UploadModuleResponse> Upload(InstanceRecord caller, IAsyncStreamReader<P.ModuleChunk> chunks, CancellationToken ct)
    {
        Interlocked.Increment(ref _uploads);
        var files = new Dictionary<string, MemoryStream>();
        await foreach (var c in chunks.ReadAllAsync(ct))
        {
            Chunks.Enqueue(c);
            if (!files.TryGetValue(c.Sha256, out var ms)) files[c.Sha256] = ms = new MemoryStream();
            c.Data.WriteTo(ms);
        }
        foreach (var (sha, ms) in files) Uploaded[sha] = ms.ToArray();
        return new P.UploadModuleResponse { Accepted = true };
    }

    public void NoteBooted(InstanceRecord instance, string sha256) { }
}

/// <summary>The gateway's session registry, faked: every join is allowed with a known real address; ResolvePeer knows what a fact tells it.</summary>
public sealed class RecordingSessions : IPlayerSessions
{
    public ConcurrentDictionary<string, P.ResolvePeerResponse> Known { get; } = new();
    public ConcurrentQueue<(string Peer, string SteamId)> Joined { get; } = new();
    public ConcurrentQueue<(string Peer, string SteamId)> Left { get; } = new();
    int _resolves;
    public int Resolves => _resolves;

    public Task<P.PlayerJoinedResponse> PlayerJoined(InstanceRecord caller, string peer, string steamId, CancellationToken ct)
    {
        Joined.Enqueue((peer, steamId));
        return Task.FromResult(new P.PlayerJoinedResponse { Allowed = true, SessionId = $"sess-{steamId}", ClientAddr = "203.0.113.7:27005" });
    }

    public Task<P.ResolvePeerResponse> ResolvePeer(InstanceRecord caller, string peer, CancellationToken ct)
    {
        Interlocked.Increment(ref _resolves);
        return Task.FromResult(Known.TryGetValue(peer, out var r) ? r : new P.ResolvePeerResponse { Found = false });
    }

    public Task PlayerLeft(InstanceRecord caller, string peer, string steamId, CancellationToken ct)
    {
        Left.Enqueue((peer, steamId));
        return Task.CompletedTask;
    }
}

/// <summary>Travel's HopReady listener as a recorder; at each HopReady it reads the watched character's version, for ordering facts.</summary>
public sealed class RecordingHops(IHostData data) : IHopListener
{
    public ConcurrentQueue<(string HopId, string SteamId, long WatchedVersion)> Ready { get; } = new();
    public string? Watch { get; set; }

    public async Task HopReady(InstanceRecord caller, string hopId, string steamId, CancellationToken ct)
    {
        var v = Watch is null ? -1 : (await data.ReadAsync((tx, _) => tx.Characters.Get(Watch), ct))?.Version ?? -1;
        Ready.Enqueue((hopId, steamId, v));
    }
}

/// <summary>
/// Travel's planner, recorded: the real TravelService (auth, idempotency) runs; the trip itself is
/// this fake, so the SDK's facts see exactly what the service was asked for.
/// </summary>
public sealed class RecordingTripPlanner : Descent.Service.Travel.ITripPlanner
{
    public ConcurrentQueue<(string Method, P.TravelRequest Request)> Calls { get; } = new();

    public Task<P.TravelResponse> Go(InstanceRecord caller, string characterId, byte[] token, string? partyId, int depth,
        Descent.Service.Travel.TravelKind kind, string requestId, CancellationToken ct)
    {
        var method = kind == Descent.Service.Travel.TravelKind.Descend ? "RequestDescent" : kind.ToString();
        Calls.Enqueue((method, new P.TravelRequest
        {
            RequestId = requestId, CharacterId = characterId, LeaseToken = Google.Protobuf.ByteString.CopyFrom(token), PartyId = partyId ?? "", Depth = depth,
        }));
        return Task.FromResult(method == "StairsDown"
            ? new P.TravelResponse { State = P.TravelState.Waiting, EtaMs = 1500 }
            : new P.TravelResponse { State = P.TravelState.Started, InstanceId = $"lvl-{depth}" });
    }
}
