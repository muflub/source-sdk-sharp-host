using System.Diagnostics;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Prometheus;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Data;

namespace Descent.Service;

/// <summary>
/// §12's metrics: RPC latency per method, pod create-to-live, and gauges read from the stores on
/// every scrape. One registry per service instance (not prometheus-net's process-wide default),
/// so two services in one process — the facts — never read each other's numbers.
/// </summary>
public sealed class ServiceMetrics
{
    public CollectorRegistry Registry { get; } = Metrics.NewCustomRegistry();
    public Histogram Rpc { get; }
    public Histogram CreateToLive { get; }
    readonly Gauge _instances, _sessions, _itemsLive, _itemsMinted, _itemsTerminal, _poolReady, _dbQueue;

    public ServiceMetrics()
    {
        var f = Metrics.WithCustomRegistry(Registry);
        Rpc = f.CreateHistogram("descent_rpc_seconds", "gRPC handling time by method and status",
            new HistogramConfiguration { LabelNames = ["method", "status"], Buckets = Histogram.ExponentialBuckets(0.0005, 2, 16) });
        CreateToLive = f.CreateHistogram("descent_instance_create_to_live_seconds", "pod created → instance live",
            new HistogramConfiguration { Buckets = Histogram.ExponentialBuckets(1, 2, 10) });
        _instances = f.CreateGauge("descent_instances", "instances by kind and state", "kind", "state");
        _sessions = f.CreateGauge("descent_sessions_open", "open gateway sessions");
        _itemsLive = f.CreateGauge("descent_items_live", "live items in the ledger");
        _itemsMinted = f.CreateGauge("descent_items_minted_total", "items ever minted (from the event log)");
        _itemsTerminal = f.CreateGauge("descent_items_terminal_total", "items that reached a terminal state (from the event log)");
        _poolReady = f.CreateGauge("descent_pool_ready_levels", "ready levels per depth", "depth");
        _dbQueue = f.CreateGauge("descent_db_write_queue", "writes waiting on the single writer");
    }

    /// <summary>Refreshes the store-derived gauges just before each scrape of this registry.</summary>
    public void Register(IServiceProvider services) =>
        Registry.AddBeforeCollectCallback(async ct =>
        {
            var data = services.GetRequiredService<HostData>();
            _dbQueue.Set(data.QueueDepth);
            var (instances, open, totals) = await data.ReadAsync(async (tx, _) =>
                (await tx.Instances.NonTerminal(), (await tx.Sessions.Open()).Count, await tx.Items.Totals()), ct);
            foreach (var kind in Enum.GetValues<InstanceKind>())
                foreach (var state in Enum.GetValues<InstanceState>())
                    _instances.WithLabels(kind.ToString(), state.ToString()).Set(instances.Count(i => i.Kind == kind && i.State == state));
            _sessions.Set(open);
            _itemsMinted.Set(totals.Minted);
            _itemsTerminal.Set(totals.Terminal);
            _itemsLive.Set(totals.Live);
            if (services.GetService<IMapPool>() is { } pool)
                foreach (var d in pool.Status.Depths) _poolReady.WithLabels(d.Depth.ToString()).Set(d.Ready);
        });
}

/// <summary>Times every gRPC call into <see cref="ServiceMetrics.Rpc"/>, by method and final status.</summary>
public sealed class RpcMetricsInterceptor(ServiceMetrics metrics) : Interceptor
{
    async Task<T> Time<T>(string method, Func<Task<T>> call)
    {
        var sw = Stopwatch.StartNew();
        var status = "OK";
        try { return await call(); }
        catch (RpcException e) { status = e.StatusCode.ToString(); throw; }
        catch { status = "Unknown"; throw; }
        finally { metrics.Rpc.WithLabels(method, status).Observe(sw.Elapsed.TotalSeconds); }
    }

    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation) =>
        Time(context.Method, () => continuation(request, context));

    public override Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream, ServerCallContext context, ClientStreamingServerMethod<TRequest, TResponse> continuation) =>
        Time(context.Method, () => continuation(requestStream, context));
}
