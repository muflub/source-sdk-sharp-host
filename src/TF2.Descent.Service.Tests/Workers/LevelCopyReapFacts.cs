using Microsoft.Extensions.DependencyInjection;
using SourceSharp.Host.Abstractions;

namespace TF2.Descent.Service.Tests.Workers;

/// <summary>D-H18: copies of a level share its files, so reaping one copy never retires a level another runs.</summary>
public class LevelCopyReapFacts
{
    static InstanceRecord Row(string id, InstanceState state, string? levelId) =>
        new(id, InstanceKind.Level, state, 1, null, "L1", $"p-{id}", "u", "10.0.0.1", 27015, "h", null, null, null, 1, DateTimeOffset.UtcNow,
            null, null, null, null, null, null, null, 0, 27015, levelId);

    static async Task<(TestService Svc, IInstanceHooks Hooks, IHostData Data)> Start(bool copyRunning)
    {
        var svc = await TestService.StartAsync();
        var data = svc.App.Services.GetRequiredService<IHostData>();
        // Seeded after the manager's start-up adoption (it ends by ensuring the hub), which would crash a pod-less live row.
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!(await data.ReadAsync((tx, _) => tx.Instances.NonTerminal())).Any(r => r.Kind == InstanceKind.Hub))
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("the instance manager never ensured its hub");
            await Task.Delay(10);
        }
        await data.WriteAsync(async (tx, _) =>
        {
            await tx.Levels.Add(new LevelRecord("L1", 1, 1, "t", 1, 0, LevelState.HandedOut, DateTimeOffset.UtcNow, "lvl-a", 1, null, null));
            await tx.Instances.Add(Row("lvl-a", InstanceState.Reaped, null));
            if (copyRunning) await tx.Instances.Add(Row("lvl-b", InstanceState.Live, "L1~COPY"));
            return true;
        });
        return (svc, svc.App.Services.GetRequiredService<IInstanceHooks>(), data);
    }

    static async Task<LevelState> Reap(IInstanceHooks hooks, IHostData data)
    {
        await hooks.OnReaped((await data.ReadAsync((tx, _) => tx.Instances.Get("lvl-a")))!, default);
        return (await data.ReadAsync((tx, _) => tx.Levels.Get("L1")))!.State;
    }

    [Fact]
    public async Task Reaping_the_last_instance_of_an_unclaimed_level_retires_it()
    {
        var (svc, hooks, data) = await Start(copyRunning: false);
        await using var _ = svc;
        Assert.Equal(LevelState.Retired, await Reap(hooks, data));
    }

    [Fact]
    public async Task Reaping_one_copy_keeps_the_level_while_another_copy_runs()
    {
        var (svc, hooks, data) = await Start(copyRunning: true);
        await using var _ = svc;
        Assert.Equal(LevelState.HandedOut, await Reap(hooks, data));
    }
}
