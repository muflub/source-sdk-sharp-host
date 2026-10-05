using SourceSharp.Host.Contracts;
using P = SourceSharp.Host.Proto;

namespace SourceSharp.Host.Sdk;

/// <summary>What the game may tune. Every default is the plan's; facts shorten the timers.</summary>
public sealed class HostSdkOptions
{
    public IHostClock Clock { get; set; } = HostClock.System;
    /// <summary>Heartbeat period on the Connect stream (§4.3: 5 s).</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>
    /// Fail closed: a lease whose renewal the SDK cannot confirm for this long is reported lost.
    /// Must not exceed the service's Lease.Ttl (30 s).
    /// </summary>
    public TimeSpan LeaseTtl { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>A stream without a heartbeat ack for this many intervals is torn down and reopened.</summary>
    public int MissedAcksBeforeReconnect { get; set; } = 3;
    /// <summary>
    /// A game whose main thread has not called Pump() for this long is hung: the SDK withholds its
    /// heartbeats (the stream stays open) so the service marks it suspect and reaps it (§6.1).
    /// Counted from the first Pump, so a long map load before it is not a hang. Zero disables.
    /// </summary>
    public TimeSpan MainThreadStallLimit { get; set; } = TimeSpan.FromSeconds(15);
    /// <summary>Backoff between Connect attempts, from the first: a new pod's traffic is blocked ~2 s by NetworkPolicy (docs/ops.md).</summary>
    public TimeSpan ReconnectBackoff { get; set; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan ReconnectBackoffMax { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(3);
    /// <summary>MapReady waits this long for the Connect stream before it reports the map (a live instance must be reachable).</summary>
    public TimeSpan StreamWaitBeforeMapReady { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>
    /// HTTP/2 pings find a half-open connection (a partition the socket never saw): after
    /// delay + timeout (15 s, inside the 30 s lease TTL) the connection is dropped and the next
    /// stream opens a new one.
    /// </summary>
    public TimeSpan KeepAlivePingDelay { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan KeepAlivePingTimeout { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>Deadline of each unary attempt.</summary>
    public TimeSpan RpcTimeout { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>Attempts per unary call before it is <see cref="HostError.Unreachable"/>; every attempt carries the same request id.</summary>
    public int RpcAttempts { get; set; } = 4;
    public TimeSpan RetryBackoff { get; set; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan RetryBackoffMax { get; set; } = TimeSpan.FromSeconds(2);
    /// <summary>Reserve size per tier index (§5.2a: Stock, Vintage, Strange, Unusual, Australium). The service caps each at its own size.</summary>
    public int[] ReserveSizes { get; set; } = [12, 12, 6, 2, 0];
    /// <summary>Refill a tier when it falls below this fraction of its size.</summary>
    public double ReserveLowWater { get; set; } = 1.0 / 3.0;
    /// <summary>Log lines held while the host is slow; more are dropped and counted.</summary>
    public int LogCapacity { get; set; } = 2000;
    /// <summary>At most this many lines per Log call, one call per <see cref="LogFlushInterval"/> (the throttle).</summary>
    public int LogBatch { get; set; } = 200;
    public TimeSpan LogFlushInterval { get; set; } = TimeSpan.FromSeconds(1);
    public int ModuleChunkBytes { get; set; } = 64 * 1024;
    /// <summary>Where the rules module comes from; default: the loaded assembly marked [HostRulesModule].</summary>
    public IRulesModuleSource ModuleSource { get; set; } = new LoadedAssemblyModuleSource();
    /// <summary>
    /// What the game adds to Booting's report of what it runs (e.g. its SourceSharp mods and their
    /// versions); the SDK adds every non-framework assembly loaded in the process itself.
    /// </summary>
    public Func<IEnumerable<LoadedModuleInfo>>? DeclaredModules { get; set; }
    /// <summary>Starts the in-process local host for <c>-hostlocal inproc</c>.</summary>
    public ILocalHostStarter LocalHostStarter { get; set; } = new ReflectionLocalHostStarter();
    public TimeSpan PeerInfoTimeout { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// The SDK's entry (plan §6.6): the game references Host.Sdk and talks to these interfaces only.
/// <c>await HostSdk.StartAsync()</c> once at load, <see cref="Pump"/> once per frame on the main
/// thread, <see cref="DisposeAsync"/> at shutdown.
/// </summary>
public sealed class HostSdk : IAsyncDisposable
{
    /// <summary>Announced in Booting; the service refuses an SDK major it does not speak (§6.6).</summary>
    public const string Version = "2.6.0";

    readonly SdkCore _core;
    readonly HostSession _session;
    readonly Peers _peers;

    public HostEndpoint Endpoint => _core.Endpoint;
    public IHostSession Session => _session;
    public IHostCommands Commands { get; }
    public ICharacters Characters { get; }
    public IItems Items { get; }
    public IParties Parties { get; }
    public ITrades Trades { get; }
    public ITravel Travel { get; }
    public ILostAndFound LostAndFound { get; }
    /// <summary>D-H17: bytes kept across restarts, per level copy, instance, character, and town.</summary>
    public IHostStore Store { get; }
    public IPeers Peers { get; }
    public IHostClock Clock => _core.Options.Clock;
    public IHostMetrics Metrics => _core.Metrics;

    HostSdk(HostEndpoint endpoint, HostSdkOptions options)
    {
        _core = new SdkCore(endpoint, options);
        var commands = new HostCommands(_core);
        Commands = commands;
        var characters = new Characters(_core);
        Characters = characters;
        Items = new Items(_core);
        Parties = new Parties(_core);
        Trades = new Trades(_core);
        Travel = new Travel(_core);
        LostAndFound = new LostAndFoundApi(_core);
        Store = new HostStore(_core);
        _peers = new Peers(_core);
        Peers = _peers;
        _core.Peers = _peers;
        _session = new HostSession(_core, commands);
    }

    /// <summary>Resolves the mode from this process's arguments and environment and connects.</summary>
    public static Task<HostSdk> StartAsync(HostSdkOptions? options = null, CancellationToken ct = default) =>
        StartAsync(Environment.GetCommandLineArgs(), Environment.GetEnvironmentVariable, options, ct);

    public static async Task<HostSdk> StartAsync(IReadOnlyList<string> args, Func<string, string?> env, HostSdkOptions? options = null, CancellationToken ct = default)
    {
        options ??= new HostSdkOptions();
        var endpoint = await HostSdkConfig.Resolve(args, env, options.LocalHostStarter, ct).ConfigureAwait(false);
        return Connect(endpoint, options);
    }

    /// <summary>Connects to an endpoint already resolved. The Connect stream opens in the background and retries from its first attempt.</summary>
    public static HostSdk Connect(HostEndpoint endpoint, HostSdkOptions? options = null)
    {
        var sdk = new HostSdk(endpoint, options ?? new HostSdkOptions());
        sdk._session.Start();
        return sdk;
    }

    /// <summary>Delivers every queued completion and command on the calling (main) thread. Same as <see cref="IHostSession.Pump"/>.</summary>
    public int Pump() => _session.Pump();

    public async ValueTask DisposeAsync()
    {
        await _session.StopAsync().ConfigureAwait(false);
        _peers.Dispose();
        _core.Dispose();
    }
}

/// <summary>What every part of the SDK shares: options, the channel, the main-thread queue, the lease registry.</summary>
internal sealed class SdkCore : IDisposable
{
    readonly CancellationTokenSource _stopping = new();
    HostEndpoint _endpoint;
    /// <summary>Who this process is to the service; changes only at a same-process hop (<see cref="Rebind"/>).</summary>
    public HostEndpoint Endpoint => Volatile.Read(ref _endpoint);
    /// <summary>The same-process hop the service announced (local mode), until the game boots its map.</summary>
    public HostLocalFile? PendingLocalHop { get => Volatile.Read(ref _pendingLocalHop); set => Volatile.Write(ref _pendingLocalHop, value); }
    HostLocalFile? _pendingLocalHop;
    public HostSdkOptions Options { get; }
    public HostMetrics Metrics { get; } = new();
    public MainThreadQueue Main { get; } = new();
    public Rpc Rpc { get; }
    public LeaseRegistry Leases { get; }
    public CancellationToken Stopping => _stopping.Token;
    public P.InstanceService.InstanceServiceClient Instance { get; }
    public P.CharacterService.CharacterServiceClient CharacterClient { get; }
    public P.ItemService.ItemServiceClient ItemClient { get; }
    public P.PartyService.PartyServiceClient PartyClient { get; }
    public P.TradeService.TradeServiceClient TradeClient { get; }
    public P.TravelService.TravelServiceClient TravelClient { get; }
    public P.LostAndFoundService.LostAndFoundServiceClient LostAndFoundClient { get; }
    public P.StoreService.StoreServiceClient StoreClient { get; }

    public SdkCore(HostEndpoint endpoint, HostSdkOptions options)
    {
        _endpoint = endpoint;
        Options = options;
        Rpc = new Rpc(() => Endpoint, options, Metrics, _stopping.Token);
        Leases = new LeaseRegistry(this);
        Instance = new(Rpc.Channel);
        CharacterClient = new(Rpc.Channel);
        ItemClient = new(Rpc.Channel);
        PartyClient = new(Rpc.Channel);
        TradeClient = new(Rpc.Channel);
        TravelClient = new(Rpc.Channel);
        LostAndFoundClient = new(Rpc.Channel);
        StoreClient = new(Rpc.Channel);
    }

    /// <summary>Runs <paramref name="work"/> off the main thread and completes the returned task only inside Pump().</summary>
    public Task<T> Background<T>(Func<Task<T>> work, Action<T>? onMain = null) => Main.Deliver(Task.Run(work), onMain);

    /// <summary>IPeers' per-session cache; PlayerJoined primes it and PlayerLeft clears it.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, PeerIdentity> PeerCache { get; } = new();
    /// <summary>Who is behind a peer (set by HostSdk once built): PlayerJoined asks it for the validated SteamID.</summary>
    public IPeers Peers { get; set; } = null!;

    public void Cancel() => _stopping.Cancel();

    /// <summary>
    /// Becomes <paramref name="target"/>'s instance (D-H13 same-process hop). One local service, so
    /// the channel stays: a target on another service is refused rather than half-followed.
    /// </summary>
    public void Rebind(HostLocalFile target)
    {
        var next = HostSdkConfig.FromLocal(Endpoint.Mode, target);
        if (next.Service != Endpoint.Service)
            throw new InvalidOperationException($"same-process hop to {next.Service}, but this process talks to {Endpoint.Service}");
        Volatile.Write(ref _endpoint, next with { Sidecar = Endpoint.Sidecar });
    }

    public void Dispose()
    {
        _stopping.Cancel();
        Rpc.Dispose();
        _stopping.Dispose();
    }

    /// <summary>The contract version the rules module is announced with (HostContract.Version).</summary>
    public static string ContractVersion => HostContract.Version;
}
