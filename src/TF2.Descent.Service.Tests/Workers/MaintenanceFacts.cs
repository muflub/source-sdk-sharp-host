using TF2.Descent.Service.Tests.Ledger;
using TF2.Descent.Service.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Hosting;
using SourceSharp.Host.Abstractions;

namespace TF2.Descent.Service.Tests.Workers;

/// <summary>Plan §4.3 (lease expiry), §5.4 (reconcile), D-H4 (vendor roll), §4.4 retention, Q14 (backups).</summary>
public class MaintenanceFacts
{
    sealed class Commands : IInstanceCommands
    {
        public List<string> Sent { get; } = [];
        public Task<bool> Send(string instanceId, InstanceCommand command, CancellationToken ct = default) { Sent.Add($"{instanceId} {command}"); return Task.FromResult(true); }
    }

    static (Maintenance M, Commands C, MaintenanceState S) Worker(LedgerHarness h)
    {
        var c = new Commands();
        var s = new MaintenanceState();
        h.Options.Data.BackupPath = Path.Combine(Path.GetTempPath(), $"sourcesharp-backups-{Guid.NewGuid():N}");
        return (new Maintenance(h.D.Data, h.Ledger, c, h.Options, s, h.D.Clock, NullLogger<Maintenance>.Instance), c, s);
    }

    [Fact]
    public async Task An_expired_lease_is_released_with_its_reserve_and_the_pod_is_told()
    {
        await using var h = await LedgerHarness.Create();
        var (m, c, _) = Worker(h);
        h.D.Clock.Advance(h.Options.Instances.AdoptReconnect); // past the start's grace (the facts below)
        var ch = await h.Character();
        await h.Lease(ch, h.Level);
        await h.W(tx => h.Ledger.TakeReserve(tx, h.Level, ch, h.Token(ch), [(0, 2)], h.Req()));
        Assert.Equal(0, await m.ExpireLeases(default));
        h.D.Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(1, await m.ExpireLeases(default));
        Assert.Null(await h.R(tx => tx.Leases.Get(ch)));
        Assert.Equal(0, await h.R(tx => tx.Items.Count(new ItemQuery(OwnerKind: OwnerKind.Reserve))));
        Assert.Contains($"{h.Level.Id} LeaseRevoked {{ CharacterId = {ch}, Reason = lease_expired }}", c.Sent);
        Assert.Single(await h.R(tx => tx.Audit.List(new AuditQuery(Action: "lease.expired"))));
    }

    /// <summary>A lease whose deadline passed while the service was down: the service has just started.</summary>
    static async Task<(LedgerHarness H, string Ch)> LeaseExpiredDuringAnOutage()
    {
        var h = await LedgerHarness.Create();
        var ch = await h.Character();
        await h.Lease(ch, h.Level);
        h.D.Clock.Advance(h.Options.Lease.Ttl + TimeSpan.FromSeconds(8)); // a 38 s restart (measured on k3s)
        Assert.NotEmpty(await h.R(tx => tx.Leases.Expired())); // the stimulus: it is past its deadline
        return (h, ch);
    }

    [Fact]
    public async Task A_restarted_service_does_not_expire_a_lease_in_its_reconnect_window()
    {
        var (h, ch) = await LeaseExpiredDuringAnOutage();
        await using var _ = h;
        var (m, c, _) = Worker(h);
        Assert.Equal(0, await m.ExpireLeases(default));
        Assert.NotNull(await h.R(tx => tx.Leases.Get(ch)));
        Assert.Empty(c.Sent);
    }

    [Fact]
    public async Task A_pod_that_reconnects_in_the_window_keeps_its_lease_after_it()
    {
        var (h, ch) = await LeaseExpiredDuringAnOutage();
        await using var _ = h;
        var (m, _, _) = Worker(h);
        h.D.Clock.Advance(TimeSpan.FromSeconds(10));
        await h.W(tx => tx.Leases.RenewForInstance(h.Level.Id, h.Options.Lease.Ttl)); // its heartbeats resume
        h.D.Clock.Advance(h.Options.Instances.AdoptReconnect);
        await h.W(tx => tx.Leases.RenewForInstance(h.Level.Id, h.Options.Lease.Ttl));
        Assert.Equal(0, await m.ExpireLeases(default));
        Assert.NotNull(await h.R(tx => tx.Leases.Get(ch)));
    }

    sealed class Lifetime : IHostApplicationLifetime
    {
        readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => _stopping.Cancel();
    }

    [Fact]
    public async Task A_stopping_service_expires_no_lease()
    {
        await using var h = await LedgerHarness.Create();
        var lifetime = new Lifetime();
        var m = new Maintenance(h.D.Data, h.Ledger, new Commands(), h.Options, new MaintenanceState(), h.D.Clock, NullLogger<Maintenance>.Instance, lifetime);
        h.D.Clock.Advance(h.Options.Instances.AdoptReconnect); // past the start's grace
        var ch = await h.Character();
        await h.Lease(ch, h.Level);
        h.D.Clock.Advance(h.Options.Lease.Ttl + TimeSpan.FromSeconds(1));
        lifetime.StopApplication(); // the rollout's SIGTERM: the pods' streams have ended
        Assert.Equal(0, await m.ExpireLeases(default));
        Assert.NotNull(await h.R(tx => tx.Leases.Get(ch)));
    }

    [Fact]
    public async Task A_running_service_past_its_grace_expires_the_same_lease()
    {
        await using var h = await LedgerHarness.Create();
        var m = new Maintenance(h.D.Data, h.Ledger, new Commands(), h.Options, new MaintenanceState(), h.D.Clock, NullLogger<Maintenance>.Instance, new Lifetime());
        h.D.Clock.Advance(h.Options.Instances.AdoptReconnect);
        var ch = await h.Character();
        await h.Lease(ch, h.Level);
        h.D.Clock.Advance(h.Options.Lease.Ttl + TimeSpan.FromSeconds(1));
        Assert.Equal(1, await m.ExpireLeases(default)); // the control: only stopping spares it
    }

    [Fact]
    public async Task A_lease_nobody_renews_expires_once_the_window_has_passed()
    {
        var (h, ch) = await LeaseExpiredDuringAnOutage();
        await using var _ = h;
        var (m, _, _) = Worker(h);
        h.D.Clock.Advance(h.Options.Instances.AdoptReconnect);
        Assert.Equal(1, await m.ExpireLeases(default));
        Assert.Null(await h.R(tx => tx.Leases.Get(ch)));
    }

    [Fact]
    public async Task A_re_lease_from_the_holding_instance_after_the_outage_gets_the_same_lease()
    {
        var (h, ch) = await LeaseExpiredDuringAnOutage();
        await using var _ = h;
        var before = await h.R(tx => tx.Leases.Get(ch));
        var again = await h.W(tx => h.Ledger.LeaseCharacter(tx, h.Level, ch));
        Assert.Equal(before!.Token, again.Lease.Token);
        Assert.Equal(h.Level.Id, again.Lease.InstanceId);
    }

    [Fact]
    public async Task Each_live_hub_gets_each_vendor_rolled()
    {
        await using var h = await LedgerHarness.Create();
        var (m, _, _) = Worker(h);
        Assert.Equal(3 * 6, await m.RollVendors(default));
    }

    [Fact]
    public async Task Reconcile_reports_and_fixes_a_cached_australium_sum_that_drifted_from_its_ledger()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sourcesharp-reconcile-{Guid.NewGuid():N}.db");
        try
        {
            await using var d = new SourceSharp.Host.Testing.TestData(path: path);
            var ch = await d.Write(tx => tx.Characters.Create("1", "scout", "Ann", false, [1], 1, 1, 0));
            await d.Write(tx => tx.Characters.AddAustralium(ch.Id, 15, "test", null));
            // The drift a bug would cause: the cached sum changed behind the ledger's back.
            await using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                await raw.OpenAsync();
                await using var cmd = raw.CreateCommand();
                cmd.CommandText = "UPDATE characters SET australium = 99 WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", ch.Id);
                Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
            }
            var state = new MaintenanceState();
            var m = new Maintenance(d.Data, new TF2.Descent.Service.Ledger.DescentLedger(new SingleRulesProvider(new SourceSharp.Host.Testing.FakeGameRules()),
                Microsoft.Extensions.Options.Options.Create(new ServiceOptions())), new Commands(), new ServiceOptions(), state, d.Clock, NullLogger<Maintenance>.Instance);
            var findings = await m.Reconcile(default);
            Assert.Equal(("australium_cache", ch.Id), (Assert.Single(findings).Kind, findings[0].Target));
            Assert.Equal(15, (await d.Read(tx => tx.Characters.Get(ch.Id)))!.Australium);
            Assert.Empty(await m.Reconcile(default));
        }
        finally { foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*")) File.Delete(f); }
    }

    [Fact]
    public async Task A_backup_is_written_and_old_hourlies_are_pruned()
    {
        await using var h = await LedgerHarness.Create();
        var (m, _, s) = Worker(h);
        h.Options.Data.HourlyBackups = 2;
        try
        {
            for (var i = 0; i < 3; i++) { await m.Backup(default); h.D.Clock.Advance(TimeSpan.FromHours(1)); }
            var files = Directory.GetFiles(h.Options.Data.BackupPath, "hourly-*.db");
            Assert.Equal(2, files.Length);
            Assert.Single(Directory.GetFiles(h.Options.Data.BackupPath, "daily-*.db"));
            Assert.True(File.Exists(s.LastBackupFile));
        }
        finally { Directory.Delete(h.Options.Data.BackupPath, true); }
    }

    [Fact]
    public async Task Purge_drops_old_request_ids_and_old_terminal_items()
    {
        await using var h = await LedgerHarness.Create();
        var (m, _, _) = Worker(h);
        await h.D.Data.IdempotentAsync("r1", null, (_, _) => Task.FromResult(new byte[] { 1 }));
        var item = await h.W(tx => tx.Items.Mint(LedgerTestItem(), OwnerKind.World, "x", "x", -1, "t", null));
        await h.W(tx => tx.Items.Terminate(item.Id, OwnerKind.World, ItemState.Swept, "t", null));
        Assert.Equal((0, 0), await m.Purge(default));
        h.D.Clock.Advance(TimeSpan.FromDays(31));
        Assert.Equal((1, 1), await m.Purge(default));
    }

    static NewItem LedgerTestItem() => SourceSharp.Host.Testing.TestData.Item();
}
