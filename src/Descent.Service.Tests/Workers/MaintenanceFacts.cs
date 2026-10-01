using Descent.Service.Tests.Ledger;
using Descent.Service.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using SourceSharp.Host.Abstractions;

namespace Descent.Service.Tests.Workers;

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
        h.Options.Data.BackupPath = Path.Combine(Path.GetTempPath(), $"descent-backups-{Guid.NewGuid():N}");
        return (new Maintenance(h.D.Data, h.Ledger, c, h.Options, s, h.D.Clock, NullLogger<Maintenance>.Instance), c, s);
    }

    [Fact]
    public async Task An_expired_lease_is_released_with_its_reserve_and_the_pod_is_told()
    {
        await using var h = await LedgerHarness.Create();
        var (m, c, _) = Worker(h);
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
        var path = Path.Combine(Path.GetTempPath(), $"descent-reconcile-{Guid.NewGuid():N}.db");
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
            var m = new Maintenance(d.Data, new Descent.Service.Ledger.DescentLedger(new SingleRulesProvider(new SourceSharp.Host.Testing.FakeGameRules()),
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
