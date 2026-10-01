using Microsoft.EntityFrameworkCore;
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
}
