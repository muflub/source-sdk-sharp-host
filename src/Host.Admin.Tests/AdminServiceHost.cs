using Descent.Service;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Testing;

namespace SourceSharp.Host.Admin.Tests;

/// <summary>
/// The real service (ServiceApp.Build: its listeners and UseListenerRoles) with the admin UI
/// added the way the composition root will add it, on free loopback ports, in Development.
/// </summary>
public sealed class AdminServiceHost : IAsyncDisposable
{
    public required WebApplication App { get; init; }
    public required AdminWorld World { get; init; }
    BoundListeners Bound => App.Services.GetRequiredService<BoundListeners>();

    /// <param name="pods">The pod store (a FakeInstanceHost by default).</param>
    public static async Task<AdminServiceHost> StartAsync(Action<AdminServiceHost>? seed = null, Func<IServiceProvider, IInstanceHost>? pods = null)
    {
        var world = new AdminWorld();
        var app = ServiceApp.Build(["--environment", "Development"], b =>
        {
            b.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Listen:Admin"] = "127.0.0.1:0",
                ["Listen:Internal"] = "127.0.0.1:0",
                ["Listen:GameApi"] = "127.0.0.1:0",
                ["Listen:GatewayApi"] = "127.0.0.1:0",
                ["Listen:FastDl"] = "127.0.0.1:0",
                ["AdminUi:Refresh"] = "00:00:00",
                // Test defaults (as Descent.Service.Tests' TestService): never real paths, never a cluster.
                ["Data:Path"] = ":memory:",
                ["MapPool:MapsPath"] = Path.Combine(Path.GetTempPath(), $"descent-maps-{Guid.NewGuid():N}"),
                ["Modules:Path"] = Path.Combine(Path.GetTempPath(), $"descent-modules-{Guid.NewGuid():N}"),
                ["Data:BackupPath"] = Path.Combine(Path.GetTempPath(), $"descent-backups-{Guid.NewGuid():N}"),
                ["Instances:VerifyModLabel"] = "false",
            });
            b.Services.AddSingleton<IInstanceHost>(pods ?? (sp => new SourceSharp.Host.Testing.FakeInstanceHost(sp.GetRequiredService<TimeProvider>())));
            b.Services.AddSingleton(world.D.Data);
            b.Services.AddSingleton<IHostData>(world.D.Data);
            b.Services.AddSingleton<IRulesProvider>(new SingleRulesProvider(world.Rules));
            b.Services.AddSingleton<IAdminBackups>(world.Backups);
            b.Services.AddSingleton(world.Catalog);
            b.Services.AddSingleton(world.Catalog.Storage);
            // The service itself registers and maps the admin UI (AddHostAdmin / MapHostAdmin().On(Admin)).
        });
        await app.StartAsync();
        // The manager adopts at start, in the background: a row a fact adds before it finishes has
        // no pod and is crashed ("adoption: no pod"). Its last start step ensures the hub, so wait for a hub row.
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!(await world.D.Data.ReadAsync((tx, _) => tx.Instances.NonTerminal())).Any(r => r.Kind == InstanceKind.Hub))
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("the instance manager never ensured its hub");
            await Task.Delay(10);
        }
        return new AdminServiceHost { App = app, World = world };
    }

    /// <summary>A pod store whose List waits for <paramref name="listed"/>: the manager's adoption held back.</summary>
    public sealed class GatedListing(IInstanceHost inner, Task listed) : IInstanceHost
    {
        public Task<PodRef> Create(PodSpec spec, CancellationToken ct = default) => inner.Create(spec, ct);
        public IAsyncEnumerable<PodEvent> Watch(CancellationToken ct) => inner.Watch(ct);
        public Task<bool> Delete(string name, string uid, TimeSpan grace, CancellationToken ct = default) => inner.Delete(name, uid, grace, ct);
        public Task<string> Logs(string name, int tail, CancellationToken ct = default) => inner.Logs(name, tail, ct);
        public async Task<IReadOnlyList<PodStatus>> List(string labelSelector, CancellationToken ct = default)
        {
            await listed.WaitAsync(ct);
            return await inner.List(labelSelector, ct);
        }
        public Task<JobRef> RunJob(JobSpec spec, CancellationToken ct = default) => inner.RunJob(spec, ct);
        public IAsyncEnumerable<JobEvent> WatchJob(string name, CancellationToken ct) => inner.WatchJob(name, ct);
    }

    public HttpClient Http(ListenerRole role) =>
        new(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri($"http://127.0.0.1:{Bound.Port(role)}") };

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
        await World.DisposeAsync();
    }
}
