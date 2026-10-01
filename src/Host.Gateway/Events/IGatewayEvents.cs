using SourceSharp.Host.Proto.Gateway;

namespace SourceSharp.Host.Gateway.Events;

/// <summary>
/// What the relay tells the service (GatewayEvents, plan §8.2). The notifications are queued and
/// delivered in order with a <c>request_id</c> and retries (<see cref="GrpcGatewayEvents"/>), so the
/// packet path never waits on the service; only <see cref="IdentifyAsync"/> is awaited, by the
/// identity-first handshake, which holds the client's packet meanwhile (§8.1b).
/// </summary>
public interface IGatewayEvents
{
    void SessionOpened(SessionEvent e);
    void SessionClosed(SessionEvent e);
    void SessionMoved(SessionEvent e);
    void PinAssigned(PlayerPin pin);
    void Stats(GatewayStats stats);
    Task<IdentifyResponse> IdentifyAsync(IdentifyRequest request, CancellationToken ct);
}

/// <summary>No service: every event is dropped and every Identify is allowed to the default route.</summary>
public sealed class NullGatewayEvents : IGatewayEvents
{
    public static readonly NullGatewayEvents Instance = new();
    public void SessionOpened(SessionEvent e) { }
    public void SessionClosed(SessionEvent e) { }
    public void SessionMoved(SessionEvent e) { }
    public void PinAssigned(PlayerPin pin) { }
    public void Stats(GatewayStats stats) { }
    public Task<IdentifyResponse> IdentifyAsync(IdentifyRequest request, CancellationToken ct) =>
        Task.FromResult(new IdentifyResponse { Allowed = true });
}
