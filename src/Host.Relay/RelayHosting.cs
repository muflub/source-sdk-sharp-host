using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Prometheus;

namespace SourceSharp.Host.Relay;

/// <summary>The relay's Prometheus series.</summary>
public sealed class RelayMetrics
{
    public CollectorRegistry Registry { get; }
    public Gauge Streams { get; }
    public Counter DatagramsUp { get; }
    public Counter DatagramsDown { get; }
    public Counter BytesUp { get; }
    public Counter BytesDown { get; }

    public RelayMetrics(CollectorRegistry? registry = null)
    {
        Registry = registry ?? Metrics.DefaultRegistry;
        var f = Metrics.WithCustomRegistry(Registry);
        Streams = f.CreateGauge("relay_streams", "Live relay streams (sessions on this pod).");
        DatagramsUp = f.CreateCounter("relay_datagrams_up_total", "Datagrams sent to the engine.");
        DatagramsDown = f.CreateCounter("relay_datagrams_down_total", "Datagrams from the engine sent down a stream.");
        BytesUp = f.CreateCounter("relay_bytes_up_total", "Bytes sent to the engine.");
        BytesDown = f.CreateCounter("relay_bytes_down_total", "Bytes from the engine sent down a stream.");
    }
}

/// <summary>
/// Hosts the D-H10 relay in any ASP.NET Core process (plan D-H11: the launcher; the fake game;
/// facts): PeerRelay on <c>Listen</c> (h2c, for the gateway), PeerInfo on <c>InfoListen</c>
/// (loopback only, for the game server), /healthz and /metrics on <c>Health</c>.
/// </summary>
public static class RelayHosting
{
    public static string Version { get; } =
        typeof(RelayHosting).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>Registers the relay's services. Validates the options first: a non-loopback InfoListen throws.</summary>
    public static IServiceCollection AddPeerRelay(this IServiceCollection services, RelayOptions options,
        TimeProvider? time = null, RelayMetrics? metrics = null)
    {
        options.Validate();
        var clock = time ?? TimeProvider.System;
        services.AddSingleton(options);
        services.AddSingleton(clock);
        services.AddSingleton(metrics ?? new RelayMetrics());
        services.AddSingleton(sp => new PeerTable(options, clock));
        services.AddSingleton<InterposeSwitchboard>();
        services.AddGrpc();
        return services;
    }

    /// <summary>The three listeners, on the addresses the options name.</summary>
    public static void ListenRelay(this KestrelServerOptions k, RelayOptions o)
    {
        k.Listen(IPEndPoint.Parse(o.Listen), l => l.Protocols = HttpProtocols.Http2);
        k.Listen(IPEndPoint.Parse(o.InfoListen), l => l.Protocols = HttpProtocols.Http2);
        k.Listen(IPEndPoint.Parse(o.Health), l => l.Protocols = HttpProtocols.Http1);
    }

    /// <summary>
    /// Maps PeerRelay, PeerInfo and health, each answered only on its own listener: the connection's
    /// local port must be the listener's (not the Host header's, which a port-forward or a Service
    /// rewrites). Port 0, used by facts, maps without the restriction. <paramref name="health"/> adds the host's own state to
    /// /healthz (the launcher: the engine's); /healthz answers 503 when it sets "ok" to false.
    /// Ends every stream with Closed{shutdown} when the host stops.
    /// </summary>
    public static WebApplication MapPeerRelay(this WebApplication app, RelayOptions o, Action<JsonObject>? health = null)
    {
        static RelayListenerPort On(string ep) => new(IPEndPoint.Parse(ep).Port);
        app.Use(async (ctx, next) =>
        {
            if (ctx.GetEndpoint()?.Metadata.GetMetadata<RelayListenerPort>() is { Port: not 0 } only && ctx.Connection.LocalPort != only.Port)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await next(ctx);
        });
        app.MapGrpcService<PeerRelayService>().WithMetadata(On(o.Listen));
        app.MapGrpcService<PeerInfoService>().WithMetadata(On(o.InfoListen));
        var table = app.Services.GetRequiredService<PeerTable>();
        var board = app.Services.GetRequiredService<InterposeSwitchboard>();
        // A RequestDelegate, not a minimal-API lambda: no reflection over the delegate (NativeAOT).
        RequestDelegate healthz = async ctx =>
        {
            var j = new JsonObject
            {
                ["version"] = Version,
                ["ok"] = true,
                ["relay"] = new JsonObject { ["up"] = true, ["mode"] = o.Mode, ["peers"] = o.Interpose ? board.LiveCount : table.LiveCount },
            };
            health?.Invoke(j);
            var ok = j["ok"]?.GetValue<bool>() ?? true;
            ctx.Response.StatusCode = ok ? 200 : 503;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(j.ToJsonString());
        };
        app.MapGet("/healthz", healthz).WithMetadata(On(o.Health));
        app.MapMetrics("/metrics", app.Services.GetRequiredService<RelayMetrics>().Registry).WithMetadata(On(o.Health));
        app.Lifetime.ApplicationStopping.Register(() => { table.CutAll("shutdown"); board.CutAll("shutdown"); });
        return app;
    }
}

/// <summary>Endpoint metadata: the local port an endpoint answers on (0 = any).</summary>
public sealed record RelayListenerPort(int Port);
