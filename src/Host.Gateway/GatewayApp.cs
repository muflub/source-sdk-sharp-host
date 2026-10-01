using System.Net;
using System.Reflection;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using Prometheus;
using Serilog;
using SourceSharp.Host.Gateway.Control;
using SourceSharp.Host.Gateway.Events;
using SourceSharp.Host.Gateway.Relay;
using SourceSharp.Host.Proto.Gateway;

namespace SourceSharp.Host.Gateway;

/// <summary>The gateway process: control gRPC on :5003, health and metrics on :5006, the UDP relay.</summary>
public static class GatewayApp
{
    public static string Version { get; } =
        typeof(GatewayApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.AddEnvironmentVariables("GATEWAY_");
        builder.Services.AddSerilog((_, lc) => lc
            .ReadFrom.Configuration(builder.Configuration)
            .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss.fff} {Level:u3} {SourceContext} {Message:lj}{NewLine}{Exception}"));
        builder.Services.AddOptions<GatewayOptions>().Bind(builder.Configuration);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddGrpc();

        // The service's gateway API (GatewayHealth, GatewayEvents) over h2c inside the cluster (Q24).
        builder.Services.AddSingleton(sp => GrpcChannel.ForAddress(sp.GetRequiredService<IOptions<GatewayOptions>>().Value.Service));
        builder.Services.AddSingleton(sp => new GrpcGatewayEvents(
            new GatewayEvents.GatewayEventsClient(sp.GetRequiredService<GrpcChannel>()),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("GatewayEvents")));
        builder.Services.AddSingleton<IGatewayEvents>(sp => sp.GetRequiredService<GrpcGatewayEvents>());
        builder.Services.AddSingleton(sp => new GatewayRelay(
            sp.GetRequiredService<IOptions<GatewayOptions>>().Value,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IGatewayEvents>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("GatewayRelay")));
        builder.Services.AddSingleton(sp =>
        {
            var o = sp.GetRequiredService<IOptions<GatewayOptions>>().Value;
            var relay = sp.GetRequiredService<GatewayRelay>();
            return new GatewayHealthClient(
                new GatewayHealth.GatewayHealthClient(sp.GetRequiredService<GrpcChannel>()),
                () => (relay.TableVersion, relay.SessionCount),
                o.GatewayId, o.PingInterval, sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger("GatewayHealth"));
        });
        builder.Services.AddHostedService<GatewayWorker>();

        configure?.Invoke(builder);

        var o = new GatewayOptions();
        builder.Configuration.Bind(o);
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(IPEndPoint.Parse(o.Control), l => l.Protocols = HttpProtocols.Http2);
            k.Listen(IPEndPoint.Parse(o.Health), l => l.Protocols = HttpProtocols.Http1);
        });

        var app = builder.Build();
        var healthPort = IPEndPoint.Parse(o.Health).Port;
        var controlPort = IPEndPoint.Parse(o.Control).Port;
        app.MapGet("/healthz", () => Results.Json(new { version = Version, ok = true })).RequireHost($"*:{healthPort}");
        app.MapMetrics("/metrics").RequireHost($"*:{healthPort}");
        app.MapGrpcService<GatewayControlService>().RequireHost($"*:{controlPort}");
        return app;
    }
}

/// <summary>Starts the relay with the host and holds the health stream for the gateway's life.</summary>
public sealed class GatewayWorker(GatewayRelay relay, GatewayHealthClient health, GrpcGatewayEvents events) : BackgroundService
{
    /// <summary>
    /// The public socket is bound before the host reports started: since .NET 10 a BackgroundService
    /// runs all of ExecuteAsync on the thread pool, so a Start inside it raced whoever read
    /// PublicEndPoint right after StartAsync (seen: E2eWorld "not started", 2 facts in one run).
    /// </summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        relay.Start();
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await health.RunAsync(stoppingToken); }
        finally
        {
            await relay.DisposeAsync();
            await events.DisposeAsync();
        }
    }
}
