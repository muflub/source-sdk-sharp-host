using Descent.MapForge;
using Descent.Service.Api;
using Descent.Service.Ledger;
using Microsoft.Extensions.Options;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Data;
using SourceSharp.Host.Instances;
using SourceSharp.Host.MapPool;
using SourceSharp.Host.Modules;
using SourceSharp.Host.Admin;

namespace Descent.Service;

/// <summary>The composition root's registrations and endpoints (plan §1.1). Defaults are registered first; a test's configure callback overrides any of them.</summary>
public static class ServiceComposition
{
    public static void AddDescentService(this IServiceCollection services)
    {
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<ServiceOptions>>().Value);

        services.AddSingleton(sp =>
        {
            var o = sp.GetRequiredService<ServiceOptions>();
            var dir = Path.GetDirectoryName(o.Data.Path);
            if (!string.IsNullOrEmpty(dir) && o.Data.Path != ":memory:") Directory.CreateDirectory(dir);
            return new HostData(new HostDataOptions { Path = o.Data.Path }, sp.GetRequiredService<TimeProvider>());
        });
        services.AddSingleton<IHostData>(sp => sp.GetRequiredService<HostData>());

        // D-H9: modules arrive from the pods; a hash stays loaded while a live instance or a ready level refers to it.
        services.AddRulesModules(sp => () => sp.GetRequiredService<IHostData>().ReadAsync<IReadOnlySet<string>>(async (tx, _) =>
        {
            var used = new HashSet<string>();
            foreach (var i in await tx.Instances.NonTerminal()) if (i.RulesSha256 is { } sha) used.Add(sha);
            foreach (var l in await tx.Levels.List(state: LevelState.Ready, take: 100_000)) if (l.RulesSha256 is { } sha) used.Add(sha);
            return used;
        }));
        services.AddSingleton<IModuleBoot, ModuleBoot>();
        services.AddSingleton<Gateway.GatewayState>();
        services.AddSingleton<Gateway.IGatewayControl>(sp => ActivatorUtilities.CreateInstance<Gateway.GatewayControlClient>(sp));
        services.AddSingleton<Gateway.GatewayRegistry>();
        services.AddSingleton<IPlayerSessions>(sp => sp.GetRequiredService<Gateway.GatewayRegistry>());
        services.AddSingleton<IInstanceRouting>(sp => sp.GetRequiredService<Gateway.GatewayRegistry>());
        services.AddSingleton<Travel.TravelCoordinator>();
        services.AddSingleton<IHopListener>(sp => sp.GetRequiredService<Travel.TravelCoordinator>());
        services.AddSingleton<Travel.ITripPlanner>(sp => sp.GetRequiredService<Travel.TravelCoordinator>());

        // Kubernetes in production; D-H13's local host when Instances.Host = Local (tests override either).
        services.AddSingleton<IInstanceHost>(sp =>
        {
            var o = sp.GetRequiredService<ServiceOptions>();
            if (o.Instances.Host == "Local")
                return new LocalInstanceHost(o.Instances.LocalDir, () => $"http://{o.Listen.GameApi.Replace("0.0.0.0", "127.0.0.1")}");
            return new KubernetesInstanceHost(KubernetesInstanceHost.Connect(o.Instances.Kubeconfig), o.Instances.Namespace,
                sp.GetRequiredService<TimeProvider>(), sp.GetService<ILogger<KubernetesInstanceHost>>());
        });

        services.AddSingleton<ServiceMetrics>();
        services.AddSingleton<DescentLedger>();
        services.AddSingleton<InstanceStreams>();
        services.AddSingleton<IInstanceCommands>(sp => sp.GetRequiredService<InstanceStreams>());
        services.AddSingleton<InstanceChatter>();
        services.AddSingleton<IInstanceHooks>(sp => ActivatorUtilities.CreateInstance<InstanceHooks>(sp, sp.GetService<IMapPool>()!));

        services.AddSingleton<IMapCompiler>(_ => new LinkedMapCompiler());
        services.AddSingleton<IPackValidator>(sp =>
        {
            var o = sp.GetRequiredService<ServiceOptions>();
            return new PackValidator(o.Mod.Name, o.MapPool.Depths);
        });
        services.AddSingleton<IPoolDemand, NoPoolDemand>();

        // H8: the admin UI and the four services it needs from here.
        services.AddSingleton<IAdminLedger, AdminLedger>();
        services.AddSingleton<IAdminGateway, AdminGateway>();
        services.AddSingleton<IAdminModules, AdminModules>();
        services.AddSingleton<IAdminBackups, AdminBackups>();

        // The game API is token-authenticated per pod (§6); the gateway API is not (NetworkPolicy).
        static void Auth(Grpc.AspNetCore.Server.GrpcServiceOptions o) => o.Interceptors.Add<InstanceAuthInterceptor>();
        services.AddGrpc(o =>
            {
                o.MaxReceiveMessageSize = 64 * 1024 * 1024;
                o.Interceptors.Add<RpcMetricsInterceptor>();
            })
            .AddServiceOptions<InstanceApi>(Auth)
            .AddServiceOptions<CharacterApi>(Auth)
            .AddServiceOptions<ItemApi>(Auth)
            .AddServiceOptions<PartyApi>(Auth)
            .AddServiceOptions<TradeApi>(Auth)
            .AddServiceOptions<LostAndFoundApi>(Auth)
            .AddServiceOptions<Travel.TravelApi>(Auth);
    }

    /// <summary>After the test's overrides: the pieces that must see the final registrations.</summary>
    public static void AddDescentWorkers(this IServiceCollection services)
    {
        services.AddInstanceManager();
        services.AddHostMapPool();
        services.AddHostedService<Travel.TravelWorker>();
        services.AddSingleton<Workers.MaintenanceState>();
        services.AddSingleton<Workers.Maintenance>();
        services.AddHostedService(sp => sp.GetRequiredService<Workers.Maintenance>());
    }

    public static void MapDescentService(this WebApplication app)
    {
        app.Services.GetRequiredService<ServiceMetrics>().Register(app.Services);
        app.MapGrpcService<InstanceApi>().On(ListenerRole.GameApi);
        app.MapGrpcService<CharacterApi>().On(ListenerRole.GameApi);
        app.MapGrpcService<ItemApi>().On(ListenerRole.GameApi);
        app.MapGrpcService<PartyApi>().On(ListenerRole.GameApi);
        app.MapGrpcService<TradeApi>().On(ListenerRole.GameApi);
        app.MapGrpcService<LostAndFoundApi>().On(ListenerRole.GameApi);
        app.MapGrpcService<Travel.TravelApi>().On(ListenerRole.GameApi);
        app.MapGrpcService<Gateway.GatewayHealthApi>().On(ListenerRole.GatewayApi);
        app.MapGrpcService<Gateway.GatewayEventsApi>().On(ListenerRole.GatewayApi);
        app.MapFastDl().On(ListenerRole.FastDl);
        app.MapInternalMaps().On(ListenerRole.Internal);
        app.MapInternalPacks().On(ListenerRole.Internal);
        app.MapPoolStatus().On(ListenerRole.Internal, ListenerRole.Admin);
        app.MapHostAdmin().On(ListenerRole.Admin);
    }
}

