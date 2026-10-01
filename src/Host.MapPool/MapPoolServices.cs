using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using SourceSharp.Host.Abstractions;

namespace SourceSharp.Host.MapPool;

/// <summary>
/// DI for the pool. The composition root registers what it owns first: <see cref="IHostData"/>,
/// <see cref="ServiceOptions"/>, <see cref="TimeProvider"/>, <see cref="IRulesProvider"/>,
/// <see cref="IPoolDemand"/>, <see cref="IMapCompiler"/> and <see cref="IPackValidator"/>
/// (Descent.MapForge's), and optionally <see cref="IBakeLauncher"/>.
/// </summary>
public static class MapPoolServices
{
    public static IServiceCollection AddHostMapPool(this IServiceCollection services, MapPoolLayoutOptions? layout = null)
    {
        services.AddSingleton(layout ?? new MapPoolLayoutOptions());
        services.TryAddSingleton(sp => new LevelStorage(sp.GetRequiredService<ServiceOptions>().MapPool));
        services.TryAddSingleton<PoolSignal>();
        services.TryAddSingleton(sp => new PackCatalog(sp.GetRequiredService<IHostData>(), sp.GetRequiredService<IPackValidator>(),
            sp.GetRequiredService<LevelStorage>(), sp.GetRequiredService<ServiceOptions>(), sp.GetRequiredService<PoolSignal>()));
        services.AddSingleton(sp =>
        {
            var o = sp.GetRequiredService<ServiceOptions>();
            return new LinkDriver(sp.GetRequiredService<IMapCompiler>(), sp.GetRequiredService<LevelStorage>(), o.Mod, o.MapPool,
                sp.GetRequiredService<MapPoolLayoutOptions>());
        });
        services.AddSingleton<MapPoolWorker>(sp => ActivatorUtilities.CreateInstance<MapPoolWorker>(sp));
        services.AddSingleton<IMapPool>(sp => sp.GetRequiredService<MapPoolWorker>());
        services.AddHostedService(sp => sp.GetRequiredService<MapPoolWorker>());
        return services;
    }
}
