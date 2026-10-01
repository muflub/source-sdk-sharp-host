using System.Net;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SourceSharp.Host.Proto.Relay;

namespace SourceSharp.Host.Relay;

/// <summary>
/// The relay hosted in-process on one loopback port (PeerRelay, PeerInfo and health together), for
/// facts and in-process game hosts: what a pod's relay is, without a pod.
/// </summary>
public sealed class RelayHost : IAsyncDisposable
{
    readonly WebApplication _app;
    readonly GrpcChannel _channel;
    public RelayOptions Options { get; }
    public PeerTable Table { get; }
    public RelayMetrics Metrics { get; }
    /// <summary>Where the gateway reaches PeerRelay: the "backend" of a route.</summary>
    public IPEndPoint EndPoint { get; }
    public PeerInfo.PeerInfoClient Info { get; }
    public PeerRelay.PeerRelayClient Relay { get; }
    public IServiceProvider Services => _app.Services;

    public RelayHost(IPEndPoint engine, TimeProvider? time = null, Action<RelayOptions>? configure = null, int port = 0,
                     Action<System.Text.Json.Nodes.JsonObject>? health = null)
    {
        Options = new RelayOptions
        {
            EngineEndpoint = engine.ToString(), PeerPort = 0,
            Listen = $"127.0.0.1:{port}", InfoListen = $"127.0.0.1:{port}", Health = $"127.0.0.1:{port}",
        };
        configure?.Invoke(Options);
        Metrics = new RelayMetrics(Prometheus.Metrics.NewCustomRegistry());
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.Services.AddPeerRelay(Options, time, Metrics);
        b.Services.Configure<HostOptions>(h => h.ShutdownTimeout = TimeSpan.FromSeconds(2));
        b.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port, l => l.Protocols = HttpProtocols.Http2));
        _app = b.Build();
        _app.MapPeerRelay(Options, health);
        Table = _app.Services.GetRequiredService<PeerTable>();
        _app.StartAsync().GetAwaiter().GetResult();
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        EndPoint = IPEndPoint.Parse(new Uri(address).Authority);
        _channel = GrpcChannel.ForAddress(address);
        Info = new PeerInfo.PeerInfoClient(_channel);
        Relay = new PeerRelay.PeerRelayClient(_channel);
    }

    int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _channel.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
