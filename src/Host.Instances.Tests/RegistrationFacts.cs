using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SourceSharp.Host.Abstractions;

namespace SourceSharp.Host.Instances.Tests;

public sealed class RegistrationFacts
{
    sealed class Lifetime : IHostApplicationLifetime
    {
        public readonly CancellationTokenSource Stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => Stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => Stopping.Cancel();
    }

    [Fact]
    public async Task The_registered_manager_stops_judging_heartbeats_on_ApplicationStopping()
    {
        await using var h = new ManagerHarness();
        var row = await h.LiveLevel();
        var lifetime = new Lifetime();
        await using var sp = new ServiceCollection()
            .AddSingleton<IHostData>(h.D.Data).AddSingleton<IInstanceHost>(h.Pods)
            .AddSingleton<IInstanceCommands>(h.Commands).AddSingleton<IInstanceHooks>(h.Hooks)
            .AddSingleton<IOptionsMonitor<ServiceOptions>>(new StaticOptions(h.Options))
            .AddSingleton<TimeProvider>(h.Clock).AddSingleton<IHostApplicationLifetime>(lifetime)
            .AddInstanceManager().BuildServiceProvider();
        var manager = sp.GetServices<IHostedService>().OfType<InstanceManager>().Single();

        lifetime.StopApplication();
        h.Clock.Advance(h.Options.Instances.HeartbeatInterval * 12);
        await manager.Tick();

        Assert.Equal(InstanceState.Live, (await h.Row(row.Id)).State);
    }
}
