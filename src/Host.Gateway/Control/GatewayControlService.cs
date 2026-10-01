using Grpc.Core;
using SourceSharp.Host.Gateway.Relay;
using SourceSharp.Host.Proto.Gateway;

namespace SourceSharp.Host.Gateway.Control;

/// <summary>
/// GatewayControl (plan §8.2), hosted by the gateway on :5003 for the service. Every route-table call
/// carries the version it produces; a stale one is FAILED_PRECONDITION, the service's cue to SyncTable.
/// </summary>
public sealed class GatewayControlService(GatewayRelay relay) : GatewayControl.GatewayControlBase
{
    ControlAck Ack(ControlStatus status, string what)
    {
        var code = status switch
        {
            ControlStatus.Ok => StatusCode.OK,
            ControlStatus.Stale => StatusCode.FailedPrecondition,
            ControlStatus.Unidentified => StatusCode.PermissionDenied,
            ControlStatus.Conflict => StatusCode.AlreadyExists,
            _ => StatusCode.InvalidArgument,
        };
        if (code != StatusCode.OK)
            throw new RpcException(new Status(code, $"{what}: {status} (gateway table version {relay.TableVersion})"));
        return new ControlAck { TableVersion = relay.TableVersion };
    }

    public override Task<ControlAck> SyncTable(RouteTable request, ServerCallContext context) =>
        Task.FromResult(Ack(relay.SyncTable(request), "SyncTable"));

    public override Task<ControlAck> SetRoute(SetRouteRequest request, ServerCallContext context) =>
        Task.FromResult(Ack(relay.SetRoute(request.Version, request.ClientAddr, request.Backend, request.Steamid, request.HoldUntilReady), "SetRoute"));

    public override Task<ControlAck> CloseSession(CloseSessionRequest request, ServerCallContext context) =>
        Task.FromResult(Ack(relay.CloseSession(request.Version, request.ClientAddr, request.Reason), "CloseSession"));

    public override Task<ControlAck> SetDefault(SetDefaultRequest request, ServerCallContext context) =>
        Task.FromResult(Ack(relay.SetDefault(request.Version, request.Backend), "SetDefault"));

    public override Task<ControlAck> BackendReady(BackendReadyRequest request, ServerCallContext context) =>
        Task.FromResult(Ack(relay.BackendReady(request.Version, request.Backend, request.Ready), "BackendReady"));

    public override Task<ControlAck> SetServerInfo(ServerInfo request, ServerCallContext context)
    {
        relay.SetServerInfo(request);
        return Task.FromResult(new ControlAck { TableVersion = relay.TableVersion });
    }

    public override Task<GatewaySnapshot> Snapshot(SnapshotRequest request, ServerCallContext context)
    {
        var s = new GatewaySnapshot { TableVersion = relay.TableVersion, AdmissionQueue = relay.HoldingSessions };
        s.Sessions.AddRange(relay.Snapshot());
        if (relay.ServerInfo is { } info) s.ServerInfo = info;
        return Task.FromResult(s);
    }
}
