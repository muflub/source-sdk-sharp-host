using System.Net;
using System.Net.Sockets;
using SourceSharp.Host.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TF2.Descent.Service.Tests;

/// <summary>The real service on free loopback ports, in Development, for facts that need real listeners.</summary>
public sealed class TestService : IAsyncDisposable
{
    public required WebApplication App { get; init; }
    public required BoundListeners Bound { get; init; }

    /// <summary>A port free on <see cref="TestLoopback.Address"/>, this process's own loopback address.</summary>
    public static int FreePort() => TestLoopback.FreePort();

    /// <summary>This process's loopback address: bind what <see cref="FreePort"/> picked here, not on 127.0.0.1.</summary>
    public static string Loopback => TestLoopback.Host;

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
            ["MapPool:MapsPath"] = Path.Combine(Path.GetTempPath(), $"sourcesharp-maps-{Guid.NewGuid():N}"),
            ["Instances:VerifyModLabel"] = "false",
            ["Modules:Path"] = Path.Combine(Path.GetTempPath(), $"sourcesharp-modules-{Guid.NewGuid():N}"),
            ["Data:BackupPath"] = Path.Combine(Path.GetTempPath(), $"sourcesharp-backups-{Guid.NewGuid():N}"),
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
