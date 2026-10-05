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
using SourceSharp.Host.Gateway.Steam;
using SourceSharp.Host.Proto.Gateway;

namespace SourceSharp.Host.Gateway;

/// <summary>The gateway process: control gRPC on :5003, health and metrics on :5006, the UDP relay.</summary>
public static class GatewayApp
{
    public static string Version { get; } =
        typeof(GatewayApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    static SteamHolder SteamServer(IServiceProvider sp)
    {
        var o = sp.GetRequiredService<IOptions<GatewayOptions>>().Value;
        var time = sp.GetRequiredService<TimeProvider>();
        var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Steam");
        return new SteamHolder(o.Steam.Mode switch
        {
            SteamMode.Fake => new FakeSteamAuthServer(sp.GetRequiredService<FakeSteamBackend>(), time),
            SteamMode.Steamworks => new SteamworksAuthServer(
                events => new SteamGameServerHostAdapter(events),
                () => new SourceSharp.Host.Steam.GameServerConfiguration
                {
                    AppId = o.Steam.AppId,
                    // The public address is the gateway's (S-19): the library opens no game socket of its own.
                    GamePort = (ushort)IPEndPoint.Parse(o.Public).Port,
                    QueryPort = SourceSharp.Host.Steam.GameServerConfiguration.SharedQueryPort,
                    // No VAC: a SourceSharp client is insecure and refuses a secure server (S-4).
                    Mode = SourceSharp.Host.Steam.GameServerMode.Authentication,
                    LoginToken = o.Steam.LoginToken.Length > 0 ? o.Steam.LoginToken : null,
                    NativeLibraryPath = o.Steam.NativeLibraryPath.Length > 0 ? o.Steam.NativeLibraryPath : null,
                    DedicatedServer = true,
                    AdvertiseActive = false, // the server browser is S-18's, later
                    CallTimeout = o.Steam.AuthTimeout,
                },
                o.Steam.LogonRetry, log),
            _ => null,
        });
    }

    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.AddEnvironmentVariables("GATEWAY_");
        builder.Services.AddSerilog((_, lc) => lc
            .ReadFrom.Configuration(builder.Configuration)
            .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss.fff} {Level:u3} {SourceContext} {Message:lj}{NewLine}{Exception}"));
        builder.Services.AddOptions<GatewayOptions>().Bind(builder.Configuration).ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<GatewayOptions>, DetailedValidator>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddGrpc();

        // The service's gateway API (GatewayHealth, GatewayEvents) over h2c inside the cluster (Q24).
        builder.Services.AddSingleton(sp => GrpcChannel.ForAddress(sp.GetRequiredService<IOptions<GatewayOptions>>().Value.Service));
        builder.Services.AddSingleton(sp => new GrpcGatewayEvents(
            new GatewayEvents.GatewayEventsClient(sp.GetRequiredService<GrpcChannel>()),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("GatewayEvents")));
        builder.Services.AddSingleton<IGatewayEvents>(sp => sp.GetRequiredService<GrpcGatewayEvents>());
        // plan_steam_auth S-1: the Steam game server lives in this process. Off: no validation (dev, old fake tier).
        builder.Services.AddSingleton<FakeSteamBackend>();
        builder.Services.AddSingleton(sp => SteamServer(sp));
        builder.Services.AddSingleton(sp => new GatewayRelay(
            sp.GetRequiredService<IOptions<GatewayOptions>>().Value,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IGatewayEvents>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("GatewayRelay"),
            steam: sp.GetRequiredService<SteamHolder>().Server));
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
        app.MapGet("/healthz", (GatewayRelay relay) =>
        {
            var (mode, steam) = relay.SteamState;
            // plan_steam_auth: ok is the relay's; Steam being down refuses new connects but does not make the pod unready.
            return Results.Json(new
            {
                version = Version, ok = true,
                steam = new { mode = mode.ToString(), loggedOn = steam.LoggedOn, gsSteamId = steam.GameServerSteamId.ToString(), epoch = steam.Epoch },
            });
        }).RequireHost($"*:{healthPort}");
        app.MapMetrics("/metrics").RequireHost($"*:{healthPort}");
        app.MapGrpcService<GatewayControlService>().RequireHost($"*:{controlPort}");
        return app;
    }
}

/// <summary>The configured Steam server, or none (a singleton cannot be null).</summary>
public sealed record SteamHolder(ISteamAuthServer? Server);

/// <summary>ValidateOnStart with every failing rule named.</summary>
sealed class DetailedValidator : IValidateOptions<GatewayOptions>
{
    public ValidateOptionsResult Validate(string? name, GatewayOptions options)
    {
        var errors = GatewayOptionsValidator.Validate(options);
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

/// <summary>Starts the relay with the host and holds the health stream for the gateway's life.</summary>
public sealed class GatewayWorker(GatewayRelay relay, GatewayHealthClient health, GrpcGatewayEvents events, SteamHolder steam) : BackgroundService
{
    /// <summary>
    /// The public socket is bound before the host reports started: since .NET 10 a BackgroundService
    /// runs all of ExecuteAsync on the thread pool, so a Start inside it raced whoever read
    /// PublicEndPoint right after StartAsync (seen: E2eWorld "not started", 2 facts in one run).
    /// </summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (steam.Server is { } s) await s.StartAsync(cancellationToken); // logon proceeds in the background
        relay.Start();
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await health.RunAsync(stoppingToken); }
        finally
        {
            await relay.DisposeAsync();
            if (steam.Server is { } s) await s.DisposeAsync();
            await events.DisposeAsync();
        }
    }
}
