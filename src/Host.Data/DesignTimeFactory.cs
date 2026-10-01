using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SourceSharp.Host.Data;

/// <summary>For `dotnet ef migrations add` only: the model, on a throwaway SQLite file.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<HostDbContext>
{
    public HostDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<HostDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
