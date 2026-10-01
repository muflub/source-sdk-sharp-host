using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace SourceSharp.Host.Data;

/// <summary>The schema of plan §4.1 on SQLite (WAL), one file host.db on the data volume.</summary>
public sealed class HostDbContext(DbContextOptions<HostDbContext> options) : DbContext(options)
{
    internal DbSet<AccountRow> Accounts => Set<AccountRow>();
    internal DbSet<CharacterRow> Characters => Set<CharacterRow>();
    internal DbSet<LeaseRow> Leases => Set<LeaseRow>();
    internal DbSet<ItemRow> Items => Set<ItemRow>();
    internal DbSet<ItemEventRow> ItemEvents => Set<ItemEventRow>();
    internal DbSet<AustraliumRow> AustraliumLedger => Set<AustraliumRow>();
    internal DbSet<LostAndFoundRow> LostAndFound => Set<LostAndFoundRow>();
    internal DbSet<PartyRow> Parties => Set<PartyRow>();
    internal DbSet<PartyMemberRow> PartyMembers => Set<PartyMemberRow>();
    internal DbSet<TradeRow> Trades => Set<TradeRow>();
    internal DbSet<InstanceRow> Instances => Set<InstanceRow>();
    internal DbSet<SessionRow> Sessions => Set<SessionRow>();
    internal DbSet<PlayerRow> Players => Set<PlayerRow>();
    internal DbSet<PackRow> Packs => Set<PackRow>();
    internal DbSet<PackAssignmentRow> PackAssignments => Set<PackAssignmentRow>();
    internal DbSet<LevelRow> Levels => Set<LevelRow>();
    internal DbSet<MapJobRow> MapJobs => Set<MapJobRow>();
    internal DbSet<VendorStockRow> VendorStock => Set<VendorStockRow>();
    internal DbSet<IdempotencyRow> Idempotency => Set<IdempotencyRow>();
    internal DbSet<AuditRow> AuditLog => Set<AuditRow>();
    internal DbSet<SettingRow> Settings => Set<SettingRow>();
    internal DbSet<RulesModuleRow> RulesModules => Set<RulesModuleRow>();
    internal DbSet<LevelClaimRow> LevelClaims => Set<LevelClaimRow>();

    /// <summary>Unix milliseconds: SQLite can order and compare them, DateTimeOffset text it cannot.</summary>
    sealed class UnixMs() : ValueConverter<DateTimeOffset, long>(v => v.ToUnixTimeMilliseconds(), v => DateTimeOffset.FromUnixTimeMilliseconds(v));

    /// <summary>A 64-bit seed as SQLite's signed INTEGER, bit for bit.</summary>
    sealed class SeedBits() : ValueConverter<ulong, long>(v => unchecked((long)v), v => unchecked((ulong)v));

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        b.Properties<DateTimeOffset>().HaveConversion<UnixMs>();
        b.Properties<ulong>().HaveConversion<SeedBits>();
    }

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<AccountRow>(e => { e.ToTable("accounts"); e.HasKey(x => x.SteamId); });
        m.Entity<CharacterRow>(e =>
        {
            e.ToTable("characters"); e.HasKey(x => x.Id);
            e.HasIndex(x => x.Account);
        });
        m.Entity<LeaseRow>(e => { e.ToTable("leases"); e.HasKey(x => x.CharacterId); e.HasIndex(x => x.InstanceId); });
        m.Entity<ItemRow>(e =>
        {
            e.ToTable("items"); e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.OwnerKind, x.OwnerId });
            e.HasIndex(x => new { x.InstanceId, x.State });
            e.HasIndex(x => x.BaseType);
            e.HasIndex(x => x.Rarity);
        });
        m.Entity<ItemEventRow>(e => { e.ToTable("item_events"); e.HasKey(x => x.Id); e.HasIndex(x => x.ItemId); });
        m.Entity<AustraliumRow>(e => { e.ToTable("australium_ledger"); e.HasKey(x => x.Id); e.HasIndex(x => x.CharacterId); });
        m.Entity<LostAndFoundRow>(e => { e.ToTable("lost_and_found"); e.HasKey(x => x.Id); e.HasIndex(x => x.CharacterId); e.HasIndex(x => x.ItemId).IsUnique(); });
        m.Entity<PartyRow>(e => { e.ToTable("parties"); e.HasKey(x => x.Id); });
        m.Entity<PartyMemberRow>(e =>
        {
            e.ToTable("party_members"); e.HasKey(x => new { x.PartyId, x.CharacterId });
            e.HasIndex(x => x.CharacterId);
        });
        m.Entity<TradeRow>(e => { e.ToTable("trades"); e.HasKey(x => x.Id); e.HasIndex(x => new { x.HubInstance, x.State }); });
        m.Entity<InstanceRow>(e => { e.ToTable("instances"); e.HasKey(x => x.Id); e.HasIndex(x => x.State); e.HasIndex(x => x.PodName); });
        m.Entity<SessionRow>(e =>
        {
            e.ToTable("sessions"); e.HasKey(x => x.Id);
            e.HasIndex(x => x.ClientAddr); e.HasIndex(x => x.Peer); e.HasIndex(x => x.SteamId);
        });
        m.Entity<PlayerRow>(e => { e.ToTable("players"); e.HasKey(x => x.SteamId); e.HasIndex(x => x.PinnedPeer); });
        m.Entity<PackRow>(e =>
        {
            e.ToTable("packs"); e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Mod, x.Library, x.Version }).IsUnique();
        });
        m.Entity<PackAssignmentRow>(e => { e.ToTable("pack_assignments"); e.HasKey(x => new { x.Mod, x.Depth }); });
        m.Entity<LevelRow>(e => { e.ToTable("levels"); e.HasKey(x => x.Hash); e.HasIndex(x => new { x.Depth, x.PackId, x.State }); });
        m.Entity<MapJobRow>(e => { e.ToTable("map_jobs"); e.HasKey(x => x.Id); e.HasIndex(x => new { x.State, x.Priority }); e.HasIndex(x => x.Key); });
        m.Entity<VendorStockRow>(e => { e.ToTable("vendor_stock"); e.HasKey(x => new { x.HubInstance, x.Vendor, x.ItemId }); });
        m.Entity<IdempotencyRow>(e => { e.ToTable("idempotency"); e.HasKey(x => x.RequestId); e.HasIndex(x => x.At); });
        m.Entity<AuditRow>(e => { e.ToTable("audit_log"); e.HasKey(x => x.Id); e.HasIndex(x => x.At); e.HasIndex(x => x.Action); });
        m.Entity<SettingRow>(e => { e.ToTable("settings"); e.HasKey(x => x.Key); });
        m.Entity<RulesModuleRow>(e => { e.ToTable("rules_modules"); e.HasKey(x => x.Sha256); });
        m.Entity<LevelClaimRow>(e => { e.ToTable("character_levels"); e.HasKey(x => new { x.CharacterId, x.Depth }); e.HasIndex(x => x.LevelHash); });

        // snake_case columns, as §4.1 names them.
        foreach (var entity in m.Model.GetEntityTypes())
            foreach (var p in entity.GetProperties())
                p.SetColumnName(SnakeCase(p.Name));
    }

    internal static string SnakeCase(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1])))) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
