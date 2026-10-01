using Grpc.Core;
using SourceSharp.Host.Abstractions;
using G = SourceSharp.Host.Proto.Gateway;

namespace Descent.Service.Gateway;

/// <summary>§8.2 GatewayHealth.Attach: the one stream; a version mismatch, a request_sync or a (re-)attach triggers SyncTable.</summary>
public sealed class GatewayHealthApi(IGatewayControl control, GatewayState state, TimeProvider clock, ILogger<GatewayHealthApi> log) : G.GatewayHealth.GatewayHealthBase
{
    public override async Task Attach(IAsyncStreamReader<G.Ping> requestStream, IServerStreamWriter<G.Pong> responseStream, ServerCallContext context)
    {
        var first = true;
        await foreach (var ping in requestStream.ReadAllAsync(context.CancellationToken))
        {
            state.LastPing = clock.GetUtcNow();
            state.GatewayId = ping.GatewayId;
            state.Sessions = ping.Sessions;
            if (first || ping.RequestSync || ping.TableVersion != (ulong)control.TableVersion)
            {
                log.LogInformation("gateway {Gateway}: syncing the table (attach={First}, request={Request}, versions {Theirs}/{Ours})",
                    ping.GatewayId, first, ping.RequestSync, ping.TableVersion, control.TableVersion);
                await control.SyncTable(context.CancellationToken);
                first = false;
            }
            await responseStream.WriteAsync(new G.Pong { TableVersion = (ulong)control.TableVersion, Seq = ping.Seq });
        }
    }
}

/// <summary>§8.2 GatewayEvents: unary, each with a request_id the gateway re-sends on failure.</summary>
public sealed class GatewayEventsApi(GatewayRegistry registry, IHostData data, IGatewayControl control, GatewayState state) : G.GatewayEvents.GatewayEventsBase
{
    async Task<G.EventAck> Once(string requestId, Func<Task> work, CancellationToken ct)
    {
        if (requestId.Length > 0)
        {
            var r = await data.IdempotentAsync("gw:" + requestId, "gateway", async (_, _) => { return []; }, ct);
            if (r.Replayed) return new G.EventAck { TableVersion = (ulong)control.TableVersion };
        }
        await work();
        return new G.EventAck { TableVersion = (ulong)control.TableVersion };
    }

    public override Task<G.EventAck> SessionOpened(G.SessionEvent request, ServerCallContext context) =>
        Once(request.RequestId, () => registry.SessionOpened(request, context.CancellationToken), context.CancellationToken);

    public override Task<G.EventAck> SessionClosed(G.SessionEvent request, ServerCallContext context) =>
        Once(request.RequestId, () => registry.SessionClosed(request, context.CancellationToken), context.CancellationToken);

    public override Task<G.EventAck> SessionMoved(G.SessionEvent request, ServerCallContext context) =>
        Once(request.RequestId, () => registry.SessionMoved(request, context.CancellationToken), context.CancellationToken);

    public override Task<G.IdentifyResponse> Identify(G.IdentifyRequest request, ServerCallContext context) =>
        registry.Identify(request, context.CancellationToken);

    /// <summary>D-H10 moved pins into the pod's launcher; kept for compatibility, it records nothing.</summary>
    public override Task<G.EventAck> PinAssigned(G.PlayerPin request, ServerCallContext context) =>
        Task.FromResult(new G.EventAck { TableVersion = (ulong)control.TableVersion });

    public override Task<G.EventAck> Stats(G.GatewayStats request, ServerCallContext context)
    {
        state.LastStats = request;
        return Task.FromResult(new G.EventAck { TableVersion = (ulong)control.TableVersion });
    }
}
