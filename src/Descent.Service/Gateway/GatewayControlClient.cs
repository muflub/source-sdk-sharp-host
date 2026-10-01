using Grpc.Core;
using Grpc.Net.Client;
using SourceSharp.Host.Abstractions;
using G = SourceSharp.Host.Proto.Gateway;

namespace Descent.Service.Gateway;

/// <summary>What the service tells the gateway (plan §8.2 GatewayControl), versioned.</summary>
public interface IGatewayControl
{
    Task SetRoute(string clientAddr, string backend, string? steamId, bool holdUntilReady, CancellationToken ct = default);
    Task CloseSession(string clientAddr, string reason, CancellationToken ct = default);
    Task SetDefault(string backend, CancellationToken ct = default);
    Task BackendReady(string backend, bool ready, CancellationToken ct = default);
    /// <summary>The whole table from the registry: the cue after a restart, a stale version or a re-attach.</summary>
    Task SyncTable(CancellationToken ct = default);
    long TableVersion { get; }
}

/// <summary>
/// The control client. Every call carries a fresh table version (monotonic across service
/// restarts: it starts at the start time in microseconds); a FAILED_PRECONDITION from the gateway
/// means its table is newer or different, and is answered with a full SyncTable. An unreachable
/// gateway is logged, not thrown: the gateway keeps relaying on its last table and re-attaches,
/// which triggers the sync.
/// </summary>
public sealed class GatewayControlClient(ServiceOptions options, IHostData data, TimeProvider clock, ILogger<GatewayControlClient> log) : IGatewayControl, IDisposable
{
    readonly Lazy<GrpcChannel> _channel = new(() => GrpcChannel.ForAddress(options.Listen.GatewayControl));
    long _version = clock.GetUtcNow().ToUnixTimeMilliseconds() * 1000;
    G.GatewayControl.GatewayControlClient Client => new(_channel.Value);

    public long TableVersion => Interlocked.Read(ref _version);
    long Next() => Interlocked.Increment(ref _version);

    /// <summary>No gateway configured (local mode, D-H13): every call is a no-op.</summary>
    bool Off => string.IsNullOrEmpty(options.Listen.GatewayControl);

    async Task Call(Func<long, Task> call, CancellationToken ct)
    {
        if (Off) return;
        try { await call(Next()); }
        catch (RpcException e) when (e.StatusCode == StatusCode.FailedPrecondition)
        {
            log.LogInformation("gateway refused a control call ({Detail}); syncing the table", e.Status.Detail);
            await SyncTable(ct);
        }
        catch (RpcException e) when (e.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
        {
            log.LogWarning("gateway unreachable: {Detail}", e.Status.Detail);
        }
    }

    /// <summary>
    /// The route goes into the registry first, then to the gateway: a SyncTable racing this call
    /// (a stale version from concurrent calls) rebuilds from the registry and must not send the
    /// client back to its old backend. The version is taken after the write, so a sync that read
    /// the old row carries a lower version than this call.
    /// </summary>
    public async Task SetRoute(string clientAddr, string backend, string? steamId, bool holdUntilReady, CancellationToken ct = default)
    {
        await data.WriteAsync(async (tx, _) =>
        {
            if (await tx.Sessions.ByClientAddr(clientAddr) is { State: "open" } s && s.Backend != backend)
                // The peer belonged to the old backend (D-H10: unique only within one); the gateway's
                // SessionMoved brings the new one.
                await tx.Sessions.Upsert(s with { Backend = backend, Peer = "", SteamId = steamId ?? s.SteamId });
            return true;
        }, ct);
        await Call(v => Client.SetRouteAsync(new G.SetRouteRequest { Version = (ulong)v, ClientAddr = clientAddr, Backend = backend, Steamid = steamId ?? "", HoldUntilReady = holdUntilReady }, cancellationToken: ct).ResponseAsync, ct);
    }

    public Task CloseSession(string clientAddr, string reason, CancellationToken ct = default) =>
        Call(v => Client.CloseSessionAsync(new G.CloseSessionRequest { Version = (ulong)v, ClientAddr = clientAddr, Reason = reason }, cancellationToken: ct).ResponseAsync, ct);

    public Task SetDefault(string backend, CancellationToken ct = default) =>
        Call(v => Client.SetDefaultAsync(new G.SetDefaultRequest { Version = (ulong)v, Backend = backend }, cancellationToken: ct).ResponseAsync, ct);

    public Task BackendReady(string backend, bool ready, CancellationToken ct = default) =>
        Call(v => Client.BackendReadyAsync(new G.BackendReadyRequest { Version = (ulong)v, Backend = backend, Ready = ready }, cancellationToken: ct).ResponseAsync, ct);

    public async Task SyncTable(CancellationToken ct = default)
    {
        if (Off) return;
        var table = await BuildTable(ct);
        try { await Client.SyncTableAsync(table, cancellationToken: ct); }
        catch (RpcException e) { log.LogWarning("SyncTable failed: {Status}", e.Status); }
    }

    public async Task<G.GatewaySnapshot> Snapshot(CancellationToken ct) =>
        await Client.SnapshotAsync(new G.SnapshotRequest(), deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct);

    /// <summary>The route table as the registry sees it: open sessions, the hub's backend, backends not yet live.</summary>
    public async Task<G.RouteTable> BuildTable(CancellationToken ct)
    {
        var table = new G.RouteTable { Version = (ulong)Next() };
        await data.ReadAsync(async (tx, _) =>
        {
            var instances = await tx.Instances.NonTerminal();
            var hub = instances.FirstOrDefault(i => i.Kind == InstanceKind.Hub && i.State == InstanceState.Live && i.PodIp is not null);
            if (hub is not null) table.DefaultBackend = Backend(hub, options);
            foreach (var i in instances.Where(i => i.PodIp is not null && i.State != InstanceState.Live))
                table.UnreadyBackends.Add(Backend(i, options));
            if (await tx.Settings.Get(AdminGateway.BansKey) is { } bans)
                table.BannedAddresses.AddRange(System.Text.Json.JsonSerializer.Deserialize<List<string>>(bans.ValueJson) ?? []);
            foreach (var s in await tx.Sessions.Open())
                table.Routes.Add(new G.Route
                {
                    SessionId = s.Id, ClientAddr = s.ClientAddr, Backend = s.Backend, Steamid = s.SteamId ?? "",
                    Identified = s.IdentifiedAt is not null, Peer = s.Peer,
                });
            return true;
        }, ct);
        return table;
    }

    /// <summary>The route table's backend is the pod's relay (D-H10/D-H11: the launcher on RelayPort).</summary>
    public static string Backend(InstanceRecord i, ServiceOptions o) => $"{i.PodIp}:{o.Instances.RelayPort}";

    public void Dispose()
    {
        if (_channel.IsValueCreated) _channel.Value.Dispose();
    }
}
