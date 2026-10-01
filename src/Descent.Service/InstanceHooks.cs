using Descent.Service.Api;
using Descent.Service.Ledger;
using SourceSharp.Host.Abstractions;

namespace Descent.Service;

/// <summary>What the gateway must do when an instance comes and goes (the gateway link implements it).</summary>
public interface IInstanceRouting
{
    /// <summary>The instance is live: its sidecar may take streams (BackendReady).</summary>
    Task InstanceLive(InstanceRecord instance, CancellationToken ct);
    /// <summary>The instance is going away: its sessions fall back to the hub route.</summary>
    Task InstanceGone(InstanceRecord instance, CancellationToken ct);
}

/// <summary>
/// The service's side of the instance lifecycle (lane I's IInstanceHooks): the §5.3 sweep,
/// lease release, hub fallback and pool retirement at reap or crash. Idempotent: the manager
/// re-runs a terminal transition's side effects after a restart when its done-mark is missing.
/// Never calls back into IInstanceLifecycle (the manager holds its lock while calling).
/// </summary>
public sealed class InstanceHooks(
    IHostData data,
    DescentLedger ledger,
    IInstanceRouting routing,
    IMapPool? pool,
    InstanceChatter chatter,
    Microsoft.Extensions.Options.IOptions<ServiceOptions> options,
    IServiceProvider services,
    ILogger<InstanceHooks> log) : IInstanceHooks
{
    /// <summary>Resolved on use: travel needs the instance manager, which needs these hooks.</summary>
    Travel.TravelCoordinator? TravelOrNull => services.GetService<Travel.TravelCoordinator>();

    public async Task OnLive(InstanceRecord instance, CancellationToken ct)
    {
        if (instance.LiveAt is { } live) services.GetService<ServiceMetrics>()?.CreateToLive.Observe((live - instance.Created).TotalSeconds);
        await routing.InstanceLive(instance, ct);
        if (TravelOrNull is { } travel) await travel.OnInstanceLive(instance, ct);
    }

    /// <summary>Before the pod delete: routes stop, the pod's last log lines are kept on the volume (§7.1, 7 days).</summary>
    public async Task OnReaping(InstanceRecord instance, CancellationToken ct)
    {
        await routing.InstanceGone(instance, ct);
        try
        {
            var dir = Path.Combine(Path.GetDirectoryName(options.Value.Data.Path) ?? ".", "instances");
            Directory.CreateDirectory(dir);
            await File.WriteAllLinesAsync(Path.Combine(dir, $"{instance.Id}.log"), chatter.Tail(instance.Id, 10_000), ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.LogWarning("could not keep the log of {Instance}: {Error}", instance.Id, e.Message);
        }
    }

    public Task OnReaped(InstanceRecord instance, CancellationToken ct) => Finish(instance, "reaped", ct);

    public async Task OnCrashed(InstanceRecord instance, CancellationToken ct)
    {
        await routing.InstanceGone(instance, ct);
        await Finish(instance, instance.State == InstanceState.Failed ? "failed" : "crashed", ct);
    }

    async Task Finish(InstanceRecord instance, string why, CancellationToken ct)
    {
        await data.WriteAsync(async (tx, _) =>
        {
            foreach (var lease in await tx.Leases.ForInstance(instance.Id))
            {
                await DescentLedger.SweepReserve(tx, lease.CharacterId, instance.Id, "reserve_released");
                await tx.Leases.Release(lease.CharacterId, null);
                await tx.Audit.Write("lease.release", lease.CharacterId, null, new { instance = instance.Id, reason = why });
            }
            await ledger.SweepInstance(tx, instance);
            return true;
        }, ct);
        // D-H12: a level a character still claims for its session stays handed out.
        if (instance.Kind == InstanceKind.Level && instance.LevelHash is { } hash && pool is not null
            && await data.ReadAsync((tx, _) => tx.LevelClaims.ClaimsOn(hash), ct) == 0)
            await pool.RetireLevelAsync(hash);
    }

    public Task<bool> HasCorpses(InstanceRecord instance, CancellationToken ct) =>
        data.ReadAsync(async (tx, _) => await tx.Items.Count(new ItemQuery(OwnerKind: OwnerKind.Corpse, InstanceId: instance.Id)) > 0, ct);
}
