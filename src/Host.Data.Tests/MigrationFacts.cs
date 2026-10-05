using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SourceSharp.Host.Testing;

namespace SourceSharp.Host.Data.Tests;

/// <summary>§12's upgrade procedure rests on migrations: the model and the migrations never drift apart.</summary>
public class MigrationFacts
{
    [Fact]
    public void The_model_has_no_changes_without_a_migration()
    {
        using var db = new DesignTimeFactory().CreateDbContext([]);
        Assert.False(db.Database.HasPendingModelChanges(), "the model changed: add a migration (dotnet tool run dotnet-ef migrations add <Name> --project src/Host.Data --output-dir Migrations)");
    }

    [Fact]
    public async Task A_new_database_is_migrated_and_records_its_history()
    {
        var path = Path.Combine(Path.GetTempPath(), $"host-migrate-{Guid.NewGuid():N}.db");
        try
        {
            await using (var d = new TestData(path: path))
                await d.Write(tx => tx.Characters.Create("1", "scout", "Ann", false, [1], 1, 1, 0));
            await using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM __EFMigrationsHistory";
            Assert.True((long)(await cmd.ExecuteScalarAsync())! >= 1);
        }
        finally { foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*")) File.Delete(f); }
    }

    static async Task<long> Scalar(SqliteConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    static async Task Exec(SqliteConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task SteamAuthAndBans_carries_the_old_address_and_account_bans_into_the_bans_table()
    {
        var path = Path.Combine(Path.GetTempPath(), $"host-migrate-bans-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<HostDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            await using (var db = new HostDbContext(options))
                await db.GetService<IMigrator>().MigrateAsync("20261003030933_Anchors"); // the schema before

            await using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await c.OpenAsync();
                await Exec(c, """
                    INSERT INTO settings (key, value_json, updated_by, updated_at) VALUES ('gateway.bans', '["203.0.113.7","198.51.100.0/24",42]', 'admin', 1000);
                    INSERT INTO accounts (steam_id, created, banned, note) VALUES ('76561198000000001', 1, 1, 'cheating'), ('76561198000000002', 1, 1, NULL), ('76561198000000003', 1, 0, NULL);
                    """);
                Assert.Equal(1, await Scalar(c, "SELECT count(*) FROM settings WHERE key = 'gateway.bans'")); // the setup took effect
            }

            await using (var db = new HostDbContext(options))
                await db.Database.MigrateAsync();

            await using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await c.OpenAsync();
                Assert.Equal(2, await Scalar(c, "SELECT count(*) FROM bans WHERE kind = 'address' AND value IN ('203.0.113.7', '198.51.100.0/24')"));
                Assert.Equal(2, await Scalar(c, "SELECT count(*) FROM bans WHERE kind = 'steamid'"));
                Assert.Equal(4, await Scalar(c, "SELECT count(*) FROM bans"));  // the non-string 42 was not carried
                Assert.Equal(1, await Scalar(c, "SELECT count(*) FROM bans WHERE value = '76561198000000001' AND reason = 'cheating'"));
                Assert.Equal(0, await Scalar(c, "SELECT count(*) FROM bans WHERE value = '76561198000000003'"));
                Assert.Equal(0, await Scalar(c, "SELECT count(*) FROM settings WHERE key = 'gateway.bans'"));
            }
        }
        finally { foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*")) File.Delete(f); }
    }
}
