using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Descent.Service.Tests;

/// <summary>The real service on free loopback ports, in Development, for facts that need real listeners.</summary>
public sealed class TestService : IAsyncDisposable
{
    public required WebApplication App { get; init; }
    public required BoundListeners Bound { get; init; }

    public static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    public static WebApplication Build(Dictionary<string, string?> settings, Action<WebApplicationBuilder>? configure = null) =>
        ServiceApp.Build(["--environment", "Development"], b =>
        {
            b.Configuration.AddInMemoryCollection(settings);
            configure?.Invoke(b);
        });

    public static async Task<TestService> StartAsync(Action<WebApplicationBuilder>? configure = null, Dictionary<string, string?>? extra = null)
    {
        // Port 0 everywhere: Kestrel binds free ports and BoundListeners reads them back, so
        // parallel facts never race for a port picked and released beforehand.
        var settings = new Dictionary<string, string?>
        {
            ["Listen:Admin"] = "127.0.0.1:0",
            ["Listen:Internal"] = "127.0.0.1:0",
            ["Listen:GameApi"] = "127.0.0.1:0",
            ["Listen:GatewayApi"] = "127.0.0.1:0",
            ["Listen:FastDl"] = "127.0.0.1:0",
            // Test defaults: never the real /data or /maps, never a cluster.
            ["Data:Path"] = ":memory:",
            ["MapPool:MapsPath"] = Path.Combine(Path.GetTempPath(), $"descent-maps-{Guid.NewGuid():N}"),
            ["Instances:VerifyModLabel"] = "false",
            ["Modules:Path"] = Path.Combine(Path.GetTempPath(), $"descent-modules-{Guid.NewGuid():N}"),
            ["Data:BackupPath"] = Path.Combine(Path.GetTempPath(), $"descent-backups-{Guid.NewGuid():N}"),
        };
        foreach (var (k, v) in extra ?? []) settings[k] = v;
        var app = Build(settings, b =>
        {
            b.Services.AddSingleton<SourceSharp.Host.Abstractions.IInstanceHost>(sp =>
                new SourceSharp.Host.Testing.FakeInstanceHost(sp.GetRequiredService<TimeProvider>()));
            configure?.Invoke(b);
        });
        await app.StartAsync();
        return new TestService { App = app, Bound = app.Services.GetRequiredService<BoundListeners>() };
    }

    public HttpClient Http(ListenerRole role) => new() { BaseAddress = new Uri($"http://127.0.0.1:{Bound.Port(role)}") };

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
    }
}
