using Grpc.Core;
using Microsoft.Extensions.Options;
using SourceSharp.Host.Abstractions;
using P = SourceSharp.Host.Proto;

namespace Descent.Service.Api;

/// <summary>The rules-module handshake of D-H9 as the API needs it (Host.Modules implements it).</summary>
public interface IModuleBoot
{
    /// <summary>Known / Send / Pending, or a HostRefusal (contract_version, quarantined).</summary>
    Task<P.ModuleAnswer> Announce(InstanceRecord caller, P.RulesModule module, CancellationToken ct);
    Task<P.UploadModuleResponse> Upload(InstanceRecord caller, IAsyncStreamReader<P.ModuleChunk> chunks, CancellationToken ct);
    /// <summary>The instance booted with this module from this mod image (the pool's "current" module follows it).</summary>
    void NoteBooted(InstanceRecord instance, string sha256);
}

/// <summary>Who a peer is (D-H10): the gateway's session registry, fed by GatewayEvents.</summary>
public interface IPlayerSessions
{
    Task<P.PlayerJoinedResponse> PlayerJoined(InstanceRecord caller, string peer, string steamId, CancellationToken ct);
    Task<P.ResolvePeerResponse> ResolvePeer(InstanceRecord caller, string peer, CancellationToken ct);
    Task PlayerLeft(InstanceRecord caller, string peer, string steamId, CancellationToken ct);
}

/// <summary>Travel (§6.5) listens for HopReady after it sent PrepareHop.</summary>
public interface IHopListener
{
    Task HopReady(InstanceRecord caller, string hopId, string steamId, CancellationToken ct);
}

/// <summary>§6.1: one stream per pod for heartbeats up and commands down; everything else unary.</summary>
public sealed class InstanceApi(
    IHostData data,
    IInstanceLifecycle lifecycle,
    InstanceStreams streams,
    InstanceChatter chatter,
    IModuleBoot modules,
    IPlayerSessions sessions,
    IHopListener hops,
    IOptions<ServiceOptions> options,
    IHostApplicationLifetime lifetime,
    ILogger<InstanceApi> log) : P.InstanceService.InstanceServiceBase
{
    /// <summary>The SDK versions this service speaks; anything else is refused before anything happens (§6.6).</summary>
    public static readonly string[] SupportedSdkMajors = ["1"];

    public override async Task Connect(IAsyncStreamReader<P.Heartbeat> requestStream, IServerStreamWriter<P.ServerCommand> responseStream, ServerCallContext context)
    {
        var caller = context.Caller();
        var (reader, generation) = streams.Open(caller.Id);
        await lifecycle.OnStreamOpened(caller.Id, context.CancellationToken);
        // The service stopping ends the stream too, so a graceful stop never waits on open pods.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, lifetime.ApplicationStopping);
        var down = Task.Run(async () =>
        {
            await foreach (var cmd in reader.ReadAllAsync(cts.Token))
                await responseStream.WriteAsync(cmd, cts.Token);
        }, cts.Token);
        try
        {
            await foreach (var hb in requestStream.ReadAllAsync(cts.Token))
            {
                await lifecycle.OnHeartbeat(caller.Id, hb.Players, cts.Token);
                await data.WriteAsync((tx, _) => tx.Leases.RenewForInstance(caller.Id, options.Value.Lease.Ttl), cts.Token);
                await streams.Send(caller.Id, new P.ServerCommand { HeartbeatAck = new P.HeartbeatAck { Seq = hb.Seq } }, cts.Token);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or RpcException)
        {
            log.LogInformation("instance {Instance} stream ended: {Reason}", caller.Id, e.GetType().Name);
        }
        finally
        {
            var wasCurrent = streams.Close(caller.Id, generation);
            await cts.CancelAsync();
            try { await down; } catch (OperationCanceledException) { } catch (InvalidOperationException) { }
            // The crash signal (§6.1) is the current stream ending while the service runs. A stream a
            // reconnect superseded, or one ended because the service itself is stopping (the pods are
            // adopted on start, §7.4), is not a crash.
            if (wasCurrent && !lifetime.ApplicationStopping.IsCancellationRequested)
                await lifecycle.OnStreamClosed(caller.Id, CancellationToken.None);
            else
                log.LogInformation("instance {Instance} stream ended ({Why}); not a crash", caller.Id, wasCurrent ? "service stopping" : "superseded");
        }
    }

    public override async Task<P.BootingResponse> Booting(P.BootingRequest request, ServerCallContext context)
    {
        var caller = context.Caller();
        var major = request.SdkVersion.Split('.')[0];
        if (!SupportedSdkMajors.Contains(major))
            throw new RpcException(new Status(StatusCode.Unimplemented,
                $"sdk_version: this host speaks SDK {string.Join(", ", SupportedSdkMajors.Select(m => m + ".x"))}, the pod sent {request.SdkVersion}"));
        P.ModuleAnswer answer;
        try { answer = await modules.Announce(caller, request.Module, context.CancellationToken); }
        catch (HostRefusal e) { throw Api.ToRpc(e); }
        InstanceRecord instance;
        try { instance = await lifecycle.OnBooting(caller.Id, request.Module.Sha256, request.ModImageDigest.Length > 0 ? request.ModImageDigest : null, context.CancellationToken); }
        catch (HostRefusal e) { throw Api.ToRpc(e); }
        if (answer == P.ModuleAnswer.Known) modules.NoteBooted(instance, request.Module.Sha256);
        return new P.BootingResponse
        {
            Module = answer, InstanceSeed = instance.Seed, InstanceKind = instance.Kind == InstanceKind.Hub ? "hub" : "level",
            Depth = instance.Depth, LevelHash = instance.LevelHash ?? "",
        };
    }

    public override async Task<P.UploadModuleResponse> UploadModule(IAsyncStreamReader<P.ModuleChunk> requestStream, ServerCallContext context)
    {
        var caller = context.Caller();
        try
        {
            var r = await modules.Upload(caller, requestStream, context.CancellationToken);
            if (r.Accepted && caller.RulesSha256 is { } sha) modules.NoteBooted(caller, sha);
            return r;
        }
        catch (HostRefusal e) { throw Api.ToRpc(e); }
    }

    public override async Task<P.MapReadyResponse> MapReady(P.MapReadyRequest request, ServerCallContext context)
    {
        var caller = context.Caller();
        MapReadyResult result;
        try { result = await lifecycle.OnMapReady(caller.Id, request.Port, context.CancellationToken); }
        catch (HostRefusal e) { throw Api.ToRpc(e); }
        if (result.Outcome == MapReadyOutcome.PortMismatch)
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"port_mismatch: the socket reports {request.Port}, the pod was given {result.Instance.ExpectedPort}"), new Metadata { { "x-reason", "port_mismatch" } });
        return new P.MapReadyResponse { ExpectedPort = result.Instance.ExpectedPort };
    }

    public override Task<P.PlayerJoinedResponse> PlayerJoined(P.PlayerJoinedRequest request, ServerCallContext context) =>
        sessions.PlayerJoined(context.Caller(), request.Peer, request.Steamid, context.CancellationToken);

    public override Task<P.ResolvePeerResponse> ResolvePeer(P.ResolvePeerRequest request, ServerCallContext context)
    {
        return sessions.ResolvePeer(context.Caller(), request.Peer, context.CancellationToken);
    }

    public override async Task<P.Ack> PlayerLeft(P.PlayerLeftRequest request, ServerCallContext context)
    {
        await sessions.PlayerLeft(context.Caller(), request.Peer, request.Steamid, context.CancellationToken);
        return new P.Ack();
    }

    public override async Task<P.Ack> HopReady(P.HopReadyRequest request, ServerCallContext context)
    {
        await hops.HopReady(context.Caller(), request.HopId, request.Steamid, context.CancellationToken);
        return new P.Ack();
    }

    public override Task<P.Ack> ExecResult(P.ExecResultRequest request, ServerCallContext context)
    {
        context.Caller();
        chatter.ExecResult(request.CommandId, request.Output);
        return Task.FromResult(new P.Ack());
    }

    public override Task<P.Ack> Log(P.LogRequest request, ServerCallContext context)
    {
        var caller = context.Caller();
        var lines = request.Lines.Select(l => $"{DateTimeOffset.FromUnixTimeMilliseconds(l.AtUnixMs):HH:mm:ss.fff} {l.Level} {l.Text}").ToList();
        if (request.Dropped > 0) lines.Add($"(the SDK dropped {request.Dropped} lines)");
        chatter.Log(caller.Id, lines);
        return Task.FromResult(new P.Ack());
    }
}
