using System.Diagnostics;
using Grpc.Core;
using Grpc.Net.Client;

namespace SourceSharp.Host.Sdk;

/// <summary>
/// Every unary call the SDK makes: the instance's credentials as metadata, a deadline per attempt,
/// retries with backoff that re-send the same request (so the same request_id, §4.4), and a typed
/// result. Fail closed: when the retries run out the answer is <see cref="HostError.Unreachable"/>.
/// </summary>
internal sealed class Rpc : IDisposable
{
    readonly HostEndpoint _endpoint;
    readonly HostSdkOptions _options;
    readonly HostMetrics _metrics;
    readonly CancellationToken _stopping;
    public GrpcChannel Channel { get; }

    public Rpc(HostEndpoint endpoint, HostSdkOptions options, HostMetrics metrics, CancellationToken stopping)
    {
        _endpoint = endpoint;
        _options = options;
        _metrics = metrics;
        _stopping = stopping;
        Channel = CreateChannel(endpoint.Service, options);
    }

    public static GrpcChannel CreateChannel(Uri address, HostSdkOptions options) =>
        GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler
            {
                ConnectTimeout = options.ConnectTimeout,
                EnableMultipleHttp2Connections = true,
                KeepAlivePingDelay = options.KeepAlivePingDelay,
                KeepAlivePingTimeout = options.KeepAlivePingTimeout,
                KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
                PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            },
            MaxReceiveMessageSize = 64 * 1024 * 1024,
        });

    public Metadata Auth() => new() { { "x-instance-id", _endpoint.InstanceId }, { "x-instance-token", _endpoint.Token } };

    public static string NewRequestId() => Guid.NewGuid().ToString("N");

    /// <summary>A mutating or reading call; <paramref name="attempts"/> bounds the retries (the default from the options).</summary>
    public async Task<HostResult<T>> Call<T>(Func<Metadata, DateTime, CancellationToken, AsyncUnaryCall<T>> call, int? attempts = null)
    {
        var max = Math.Max(1, attempts ?? _options.RpcAttempts);
        var backoff = _options.RetryBackoff;
        RpcException? last = null;
        for (var attempt = 1; attempt <= max; attempt++)
        {
            if (_stopping.IsCancellationRequested) return HostResult<T>.Refused(HostRefusal.Disposed());
            if (attempt > 1)
            {
                _metrics.Retry();
                try { await _options.Clock.Delay(backoff, _stopping).ConfigureAwait(false); }
                catch (OperationCanceledException) { return HostResult<T>.Refused(HostRefusal.Disposed()); }
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, _options.RetryBackoffMax.Ticks));
            }
            _metrics.Call();
            var sw = Stopwatch.StartNew();
            try
            {
                var value = await call(Auth(), DateTime.UtcNow + _options.RpcTimeout, _stopping).ResponseAsync.ConfigureAwait(false);
                _metrics.Latency(sw.Elapsed);
                return HostResult<T>.Success(value);
            }
            catch (RpcException e) when (HostRefusal.Transient(e))
            {
                last = e;
            }
            catch (RpcException e)
            {
                _metrics.Failure();
                return HostResult<T>.Refused(HostRefusal.FromRpc(e));
            }
            catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException && !_stopping.IsCancellationRequested)
            {
                last = new RpcException(new Status(StatusCode.Unavailable, e.Message));
            }
        }
        _metrics.Failure();
        if (_stopping.IsCancellationRequested) return HostResult<T>.Refused(HostRefusal.Disposed());
        return HostResult<T>.Refused(HostRefusal.Unreachable($"{max} attempts: {last?.Status.StatusCode} {last?.Status.Detail}"));
    }

    public void Dispose() => Channel.Dispose();
}
