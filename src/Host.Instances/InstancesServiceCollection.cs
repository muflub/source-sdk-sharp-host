using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SourceSharp.Host.Abstractions;

namespace SourceSharp.Host.Instances;

public static class InstancesServiceCollection
{
    /// <summary>
    /// The instance manager as IInstanceLifecycle and a hosted service, the mod-image guard,
    /// and KubernetesInstanceHost as IInstanceHost unless one is already registered (a fact's
    /// FakeInstanceHost). The caller registers IHostData, TimeProvider, ServiceOptions,
    /// IInstanceCommands and IInstanceHooks.
    /// </summary>
    public static IServiceCollection AddInstanceManager(this IServiceCollection services)
    {
        services.TryAddSingleton<IInstanceHost>(sp =>
        {
            var o = sp.GetRequiredService<IOptionsMonitor<ServiceOptions>>().CurrentValue.Instances;
            return new KubernetesInstanceHost(KubernetesInstanceHost.Connect(o.Kubeconfig), o.Namespace,
                sp.GetRequiredService<TimeProvider>(), sp.GetService<ILogger<KubernetesInstanceHost>>());
        });
        services.TryAddSingleton<IModImageInspector>(sp => new OciModImageInspector(
            new HttpClient { Timeout = TimeSpan.FromSeconds(20) },
            sp.GetRequiredService<IOptionsMonitor<ServiceOptions>>().CurrentValue.Instances.InsecureRegistries));
        services.TryAddSingleton(sp => new ModImageGuard(sp.GetRequiredService<IModImageInspector>(), sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton(sp => new InstanceManager(
            sp.GetRequiredService<IHostData>(), sp.GetRequiredService<IInstanceHost>(),
            sp.GetRequiredService<IInstanceCommands>(), sp.GetRequiredService<IInstanceHooks>(),
            sp.GetRequiredService<IOptionsMonitor<ServiceOptions>>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ModImageGuard>(), sp.GetService<ILogger<InstanceManager>>()));
        services.TryAddSingleton<IInstanceLifecycle>(sp => sp.GetRequiredService<InstanceManager>());
        services.AddHostedService(sp =>
        {
            var manager = sp.GetRequiredService<InstanceManager>();
            sp.GetService<IHostApplicationLifetime>()?.ApplicationStopping.Register(manager.Stopping);
            return manager;
        });
        return services;
    }
}
