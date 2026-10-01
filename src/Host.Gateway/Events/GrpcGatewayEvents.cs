using System.Threading.Channels;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SourceSharp.Host.Proto.Gateway;

namespace SourceSharp.Host.Gateway.Events;

/// <summary>
/// GatewayEvents over gRPC (plan §8.2): notifications go through one ordered queue, each with its
/// <c>request_id</c> fixed at enqueue so a retry is recognisable as the same event; a failed call is
/// retried with backoff on the gateway's clock until it lands or the gateway stops. The queue is
/// bounded; on overflow the oldest is dropped and logged (the service resyncs from Snapshot).
/// </summary>
public sealed class GrpcGatewayEvents : IGatewayEvents, IAsyncDisposable
{
    readonly GatewayEvents.GatewayEventsClient _client;
    readonly TimeProvider _time;
    readonly ILogger _log;
    readonly Channel<(string Kind, Func<CancellationToken, Task> Call)> _queue;
    readonly CancellationTokenSource _stop = new();
    readonly Task _pump;

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Calls that failed and were retried; events dropped on overflow.</summary>
    public long Retries => Interlocked.Read(ref _retries);
    public long Delivered => Interlocked.Read(ref _delivered);
    long _retries, _delivered;

    public GrpcGatewayEvents(GatewayEvents.GatewayEventsClient client, TimeProvider time, ILogger? log = null, int capacity = 10_000)
    {
        _client = client;
        _time = time;
        _log = log ?? NullLogger.Instance;
        _queue = Channel.CreateBounded<(string, Func<CancellationToken, Task>)>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        }, dropped => _log.LogWarning("gateway event queue full: dropped {Kind}", dropped.Item1));
        _pump = Task.Run(Pump);
    }

    public void SessionOpened(SessionEvent e) => Enqueue("SessionOpened", ct => _client.SessionOpenedAsync(Stamp(e), Opts(ct)).ResponseAsync);
    public void SessionClosed(SessionEvent e) => Enqueue("SessionClosed", ct => _client.SessionClosedAsync(Stamp(e), Opts(ct)).ResponseAsync);
    public void SessionMoved(SessionEvent e) => Enqueue("SessionMoved", ct => _client.SessionMovedAsync(Stamp(e), Opts(ct)).ResponseAsync);
    public void PinAssigned(PlayerPin pin) => Enqueue("PinAssigned", ct => _client.PinAssignedAsync(Stamp(pin), Opts(ct)).ResponseAsync);
    public void Stats(GatewayStats stats) => Enqueue("Stats", ct => _client.StatsAsync(stats, Opts(ct)).ResponseAsync);

    /// <summary>Awaited by the identity-first handshake: three attempts with the same request_id.</summary>
    public async Task<IdentifyResponse> IdentifyAsync(IdentifyRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.RequestId)) request.RequestId = Guid.NewGuid().ToString("N");
        for (var attempt = 1; ; attempt++)
        {
            try { return await _client.IdentifyAsync(request, Opts(ct)); }
            catch (RpcException e) when (attempt < 3 && e.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
            {
                Interlocked.Increment(ref _retries);
                await Task.Delay(RetryDelay, _time, ct);
            }
        }
    }

    static SessionEvent Stamp(SessionEvent e) { if (string.IsNullOrEmpty(e.RequestId)) e.RequestId = Guid.NewGuid().ToString("N"); return e; }
    static PlayerPin Stamp(PlayerPin p) { if (string.IsNullOrEmpty(p.RequestId)) p.RequestId = Guid.NewGuid().ToString("N"); return p; }

    CallOptions Opts(CancellationToken ct) => new(deadline: DateTime.UtcNow + CallTimeout, cancellationToken: ct);

    void Enqueue(string kind, Func<CancellationToken, Task> call) => _queue.Writer.TryWrite((kind, call));

    async Task Pump()
    {
        var ct = _stop.Token;
        try
        {
            await foreach (var (kind, call) in _queue.Reader.ReadAllAsync(ct))
            {
                var delay = RetryDelay;
                while (true)
                {
                    try
                    {
                        await call(ct);
                        Interlocked.Increment(ref _delivered);
                        break;
                    }
                    catch (RpcException e) when (!ct.IsCancellationRequested)
                    {
                        Interlocked.Increment(ref _retries);
                        _log.LogDebug("{Kind} failed ({Status}); retrying in {Delay}", kind, e.StatusCode, delay);
                        await Task.Delay(delay, _time, ct);
                        delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRetryDelay.Ticks));
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _queue.Writer.TryComplete();
        _stop.Cancel();
        await _pump.ConfigureAwait(false);
        _stop.Dispose();
    }
}
