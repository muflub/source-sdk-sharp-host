using Microsoft.Extensions.DependencyInjection;
using SourceSharp.Host.Abstractions;
using TF2.Descent.Service.Travel;

namespace TF2.Descent.Service.Tests.Workers;

/// <summary>
/// D-H14 (amended): InstanceHooks.IsHeldOpen, as the service composes it. A prewarm is held open
/// while the level its party arrived on runs; the link is read from the audit, so a restart keeps it.
/// </summary>
public class PrewarmHoldFacts
{
    static InstanceRecord Row(string id, InstanceKind kind, InstanceState state, int depth) =>
        new(id, kind, state, depth, null, "h", $"p-{id}", "u", "10.0.0.1", 27015, "h", null, null, null, 1, DateTimeOffset.UtcNow,
            null, null, null, null, null, null, null, 0, 27015);

    static async Task<(TestService Svc, IInstanceHooks Hooks, IHostData Data)> Start()
    {
        var svc = await TestService.StartAsync();
        var data = svc.App.Services.GetRequiredService<IHostData>();
        // The manager adopts at start, in the background: a row seeded before it finishes has no
        // pod and is crashed. Its last start step ensures the hub, so wait for a hub row first.
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!(await data.ReadAsync((tx, _) => tx.Instances.NonTerminal())).Any(r => r.Kind == InstanceKind.Hub))
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("the instance manager never ensured its hub");
            await Task.Delay(10);
        }
        await data.WriteAsync(async (tx, _) =>
        {
            await tx.Instances.Add(Row("lvl-d1", InstanceKind.Level, InstanceState.Live, 1));
            await tx.Instances.Add(Row("lvl-d2", InstanceKind.Level, InstanceState.Live, 2));
            return true;
        });
        return (svc, svc.App.Services.GetRequiredService<IInstanceHooks>(), data);
    }

    static Task Link(IHostData data, string prewarm, string parent) => data.WriteAsync(async (tx, _) =>
    {
        await tx.Audit.Write(TravelCoordinator.PrewarmedAction, prewarm, null, new { parent, depth = 2 }, "travel");
        return true;
    });

    [Fact]
    public async Task A_prewarm_is_held_open_while_its_partys_level_runs()
    {
        var (svc, hooks, data) = await Start();
        await using var _ = svc;
        await Link(data, "lvl-d2", "lvl-d1");
        Assert.True(await hooks.IsHeldOpen((await data.ReadAsync((tx, _) => tx.Instances.Get("lvl-d2")))!, default));
    }

    [Fact]
    public async Task The_hold_ends_when_that_level_ends()
    {
        var (svc, hooks, data) = await Start();
        await using var _ = svc;
        await Link(data, "lvl-d2", "lvl-d1");
        await data.WriteAsync((tx, _) => tx.Instances.Update("lvl-d1", r => r with { State = InstanceState.Reaped }));
        Assert.False(await hooks.IsHeldOpen((await data.ReadAsync((tx, _) => tx.Instances.Get("lvl-d2")))!, default));
    }

    [Fact]
    public async Task A_level_with_no_link_is_not_held()
    {
        var (svc, hooks, data) = await Start();
        await using var _ = svc;
        Assert.False(await hooks.IsHeldOpen((await data.ReadAsync((tx, _) => tx.Instances.Get("lvl-d2")))!, default));
    }
}
