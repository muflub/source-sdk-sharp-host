using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SourceSharp.Host.Abstractions;

namespace SourceSharp.Host.Modules;

public static class RulesModuleServices
{
    /// <summary>
    /// The rules-module registry (<see cref="IRulesModuleRegistry"/> and <see cref="RulesModuleRegistry"/>),
    /// the loader, <see cref="RulesProvider"/> as <see cref="IRulesProvider"/> (replacing any
    /// provider registered before), and a hosted service that relearns the current module at
    /// start and sweeps unreferenced load contexts every <paramref name="sweepEvery"/> (default
    /// one minute). The caller registers IHostData, TimeProvider and ServiceOptions, and supplies
    /// <paramref name="referencedHashes"/>: the hashes a live instance, a ready level or an
    /// assignment refers to.
    /// </summary>
    public static IServiceCollection AddRulesModules(this IServiceCollection services,
        Func<IServiceProvider, Func<Task<IReadOnlySet<string>>>> referencedHashes, TimeSpan? sweepEvery = null)
    {
        services.TryAddSingleton<RulesModuleLoader>();
        services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptionsMonitor<ServiceOptions>>();
            return new RulesModuleRegistry(sp.GetRequiredService<IHostData>(), () => options.CurrentValue.Modules,
                sp.GetRequiredService<RulesModuleLoader>(), referencedHashes(sp));
        });
        services.TryAddSingleton<IRulesModuleRegistry>(sp => sp.GetRequiredService<RulesModuleRegistry>());
        services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptionsMonitor<ServiceOptions>>();
            return new RulesProvider(sp.GetRequiredService<RulesModuleRegistry>(),
                () => options.CurrentValue.Instances.ModImage.ToString(), referencedHashes(sp));
        });
        services.Replace(ServiceDescriptor.Singleton<IRulesProvider>(sp => sp.GetRequiredService<RulesProvider>()));
        services.AddHostedService(sp => new RulesModuleSweeper(
            sp.GetRequiredService<RulesModuleRegistry>(), sp.GetRequiredService<RulesProvider>(), sp.GetRequiredService<IHostData>(),
            sp.GetRequiredService<TimeProvider>(), sweepEvery ?? TimeSpan.FromMinutes(1), sp.GetService<ILogger<RulesModuleSweeper>>()));
        return services;
    }
}

/// <summary>At start: clears interrupted uploads and relearns the current module; then sweeps unreferenced contexts.</summary>
public sealed class RulesModuleSweeper(
    RulesModuleRegistry registry, RulesProvider provider, IHostData data, TimeProvider clock, TimeSpan every, ILogger<RulesModuleSweeper>? log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var stale = registry.CleanStaleUploads();
        if (stale > 0) log?.LogInformation("removed {Count} interrupted module uploads", stale);
        await provider.RestoreAsync(data, stoppingToken);

        using var timer = new PeriodicTimer(every, clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var gone = await provider.SweepAsync();
                if (gone.Count > 0) log?.LogInformation("unloaded rules modules {Hashes}", string.Join(", ", gone));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log?.LogWarning(e, "rules module sweep failed");
            }
        }
    }
}
