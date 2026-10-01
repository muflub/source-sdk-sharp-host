using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SourceSharp.Host.Proto.Gateway;

namespace SourceSharp.Host.Gateway.Control;

/// <summary>
/// The one stream (plan §8.2, Q5): <c>GatewayHealth.Attach</c>, held for the gateway's life. A ping
/// every <c>PingInterval</c> carries the table version and session count; <c>request_sync</c> is set
/// on the first ping after each (re)attach and after a pong whose version differs, which is the
/// service's cue to SyncTable. Three unanswered pings mark the health <see cref="Lost"/>: the relay
/// keeps relaying on its last table, and this logs it. It never touches the relay's table.
/// </summary>
public sealed class GatewayHealthClient(
    GatewayHealth.GatewayHealthClient client,
    Func<(ulong Version, int Sessions)> state,
    string gatewayId,
    TimeSpan pingInterval,
    TimeProvider time,
    ILogger? log = null)
{
    readonly ILogger _log = log ?? NullLogger.Instance;
    long _sent, _answered;
    bool _requestSync = true;

    public int Attaches { get; private set; }
    public bool Attached { get; private set; }
    /// <summary>Three or more pings unanswered; the relay is running on its last table.</summary>
    public bool Lost => Interlocked.Read(ref _sent) - Interlocked.Read(ref _answered) >= 3;
    public ulong LastServiceVersion { get; private set; }
    public long PingsSent => Interlocked.Read(ref _sent);
    public long PongsReceived => Interlocked.Read(ref _answered);
    public TimeSpan ReattachDelay { get; init; } = TimeSpan.FromSeconds(1);

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await AttachOnce(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e) when (e is RpcException or IOException or InvalidOperationException)
            {
                _log.LogWarning("health stream lost ({Error}); relaying on table {Version}", e.Message, state().Version);
            }
            Attached = false;
            try { await Task.Delay(ReattachDelay, time, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    async Task AttachOnce(CancellationToken ct)
    {
        using var call = client.Attach(cancellationToken: ct);
        Attaches++;
        Attached = true;
        _requestSync = true;
        _sent = _answered = 0;
        var loggedLost = false;
        using var inner = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var reader = Task.Run(async () =>
        {
            await foreach (var pong in call.ResponseStream.ReadAllAsync(inner.Token))
            {
                Interlocked.Exchange(ref _answered, (long)pong.Seq);
                LastServiceVersion = pong.TableVersion;
                if (pong.TableVersion != state().Version)
                {
                    _requestSync = true;
                    _log.LogInformation("service table {Service} != gateway table {Gateway}: requesting SyncTable", pong.TableVersion, state().Version);
                }
            }
        }, inner.Token);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (reader.IsCompleted) { await reader; throw new InvalidOperationException("health stream ended by the service"); }
                var (version, sessions) = state();
                var seq = Interlocked.Increment(ref _sent);
                await call.RequestStream.WriteAsync(new Ping
                {
                    GatewayId = gatewayId,
                    TableVersion = version,
                    Sessions = sessions,
                    Seq = (ulong)seq,
                    RequestSync = _requestSync,
                }, ct);
                _requestSync = false;
                if (Lost && !loggedLost)
                {
                    loggedLost = true;
                    _log.LogWarning("three pongs missed: relaying on table {Version}", version);
                }
                else if (!Lost) loggedLost = false;
                await Task.Delay(pingInterval, time, ct);
            }
        }
        finally { inner.Cancel(); }
    }
}
