using Descent.Service.Ledger;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Data;

namespace Descent.Service.Workers;

/// <summary>The Dashboard's view of the workers (§11): last run, last result, reconcile findings.</summary>
public sealed class MaintenanceState
{
    public DateTimeOffset? LastLeaseSweep { get; set; }
    public DateTimeOffset? LastVendorRoll { get; set; }
    public DateTimeOffset? LastReconcile { get; set; }
    public IReadOnlyList<DescentLedger.Finding> Findings { get; set; } = [];
    public DateTimeOffset? LastBackup { get; set; }
    public string? LastBackupFile { get; set; }
    public string? LastError { get; set; }
}

/// <summary>
/// The service's periodic work (plan §1 "workers", §4.3, §5.4, D-H4, Q14). Each job is a public
/// method a fact can call on a fake clock; the loop only schedules them.
/// </summary>
public sealed class Maintenance(
    HostData data,
    DescentLedger ledger,
    IInstanceCommands commands,
    ServiceOptions options,
    MaintenanceState state,
    TimeProvider clock,
    ILogger<Maintenance> log) : BackgroundService
{
    /// <summary>§4.3: expired leases are released every few seconds, audited; the reserve goes with them; the pod hears LeaseRevoked.</summary>
    public async Task<int> ExpireLeases(CancellationToken ct)
    {
        var expired = await data.WriteAsync(async (tx, _) =>
        {
            var gone = new List<Lease>();
            foreach (var l in await tx.Leases.Expired())
            {
                await DescentLedger.SweepReserve(tx, l.CharacterId, l.InstanceId, "reserve_released");
                await tx.Leases.Release(l.CharacterId, null);
                await tx.Audit.Write("lease.expired", l.CharacterId, null, new { instance = l.InstanceId, expired = l.Expires });
                gone.Add(l);
            }
            return gone;
        }, ct);
        foreach (var l in expired)
            await commands.Send(l.InstanceId, new InstanceCommand.LeaseRevoked(l.CharacterId, "lease_expired"), ct);
        state.LastLeaseSweep = clock.GetUtcNow();
        return expired.Count;
    }

    /// <summary>D-H4: each live hub's vendors, rolled; unsold stock swept by the roll itself.</summary>
    public async Task<int> RollVendors(CancellationToken ct)
    {
        var hubs = await data.ReadAsync((tx, _) => tx.Instances.List(InstanceKind.Hub, InstanceState.Live), ct);
        var rolled = 0;
        foreach (var hub in hubs)
            foreach (var vendor in options.Maintenance.Vendors)
            {
                try { rolled += await data.WriteAsync((tx, _) => ledger.RollVendor(tx, hub, vendor), ct); }
                catch (HostRefusal e) { log.LogInformation("vendor roll of {Hub}/{Vendor} skipped: {Reason}", hub.Id, vendor, e.Message); }
            }
        state.LastVendorRoll = clock.GetUtcNow();
        return rolled;
    }

    /// <summary>§5.4: findings for the Dashboard; the cached Australium sums are the one thing fixed.</summary>
    public async Task<IReadOnlyList<DescentLedger.Finding>> Reconcile(CancellationToken ct)
    {
        var findings = await data.WriteAsync((tx, _) => DescentLedger.Reconcile(tx, fixCachedSums: true), ct);
        state.Findings = findings;
        state.LastReconcile = clock.GetUtcNow();
        if (findings.Count > 0) log.LogWarning("reconcile: {Count} findings", findings.Count);
        return findings;
    }

    /// <summary>§4.4 and §5.1: idempotency rows past 24 h and terminal items past 30 days go; their events stay.</summary>
    public async Task<(int Requests, int Items)> Purge(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var requests = await data.PurgeIdempotencyAsync(now - options.Data.IdempotencyRetention, ct);
        var items = await data.WriteAsync((tx, _) => tx.Items.PurgeTerminalOlderThan(now - options.Maintenance.TerminalItemRetention), ct);
        return (requests, items);
    }

    /// <summary>Q14: an hourly VACUUM INTO, the newest 24 hourly and the newest 7 daily kept.</summary>
    public async Task<string> Backup(CancellationToken ct)
    {
        var dir = options.Data.BackupPath;
        Directory.CreateDirectory(dir);
        var now = clock.GetUtcNow();
        var file = Path.Combine(dir, $"hourly-{now:yyyyMMdd-HHmm}.db");
        await data.BackupAsync(file, ct);
        var daily = Path.Combine(dir, $"daily-{now:yyyyMMdd}.db");
        if (!File.Exists(daily)) File.Copy(file, daily);
        Prune(dir, "hourly-", options.Data.HourlyBackups);
        Prune(dir, "daily-", options.Data.DailyBackups);
        state.LastBackup = now;
        state.LastBackupFile = file;
        return file;
    }

    static void Prune(string dir, string prefix, int keep)
    {
        foreach (var old in Directory.GetFiles(dir, prefix + "*.db").Order(StringComparer.Ordinal).SkipLast(keep))
            File.Delete(old);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options;
        var next = new Dictionary<string, DateTimeOffset>();
        using var timer = new PeriodicTimer(o.Lease.ExpirySweep, clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var now = clock.GetUtcNow();
            async Task Due(string name, TimeSpan every, Func<CancellationToken, Task> job)
            {
                if (next.TryGetValue(name, out var at) && now < at) return;
                next[name] = now + every;
                try { await job(stoppingToken); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    state.LastError = $"{name}: {e.Message}";
                    log.LogWarning(e, "maintenance job {Job} failed", name);
                }
            }
            await Due("leases", o.Lease.ExpirySweep, ct => ExpireLeases(ct));
            await Due("vendors", o.Maintenance.VendorRoll, ct => RollVendors(ct));
            await Due("reconcile", o.Maintenance.Reconcile, ct => Reconcile(ct));
            await Due("purge", TimeSpan.FromHours(6), ct => Purge(ct));
            await Due("backup", o.Maintenance.Backup, ct => Backup(ct));
        }
    }
}
