using SourceSharp.Host.Abstractions;

namespace SourceSharp.Host.Data;

/// <summary>The stores bound to one DbContext and, for writes, the writer's transaction.</summary>
internal sealed class Tx(HostDbContext db, TimeProvider clock, string actor, bool writable) : IWriteTx
{
    readonly Ctx _c = new(db, clock, actor, writable);
    internal HostDbContext Db => _c.Db;

    ICharacterStore? _characters; ILeaseStore? _leases; IItemLedger? _items; ILostAndFoundStore? _lf; IPartyStore? _parties;
    ITradeStore? _trades; IInstanceStore? _instances; ISessionStore? _sessions; ILibraryStore? _library; ILevelStore? _levels;
    IMapJobStore? _jobs; IVendorStore? _vendors; IAuditLog? _audit; ISettingsStore? _settings; IRulesModuleStore? _modules; ILevelClaimStore? _claims;

    public ICharacterStore Characters => _characters ??= new CharacterStore(_c);
    public ILeaseStore Leases => _leases ??= new LeaseStore(_c);
    public IItemLedger Items => _items ??= new ItemLedger(_c);
    public ILostAndFoundStore LostAndFound => _lf ??= new LostAndFoundStore(_c);
    public IPartyStore Parties => _parties ??= new PartyStore(_c);
    public ITradeStore Trades => _trades ??= new TradeStore(_c);
    public IInstanceStore Instances => _instances ??= new InstanceStore(_c);
    public ISessionStore Sessions => _sessions ??= new SessionStore(_c);
    public ILibraryStore Library => _library ??= new LibraryStore(_c);
    public ILevelStore Levels => _levels ??= new LevelStore(_c);
    public IMapJobStore MapJobs => _jobs ??= new MapJobStore(_c);
    public IVendorStore Vendors => _vendors ??= new VendorStore(_c);
    public IAuditLog Audit => _audit ??= new AuditLog(_c);
    public ISettingsStore Settings => _settings ??= new SettingsStore(_c);
    public IRulesModuleStore Modules => _modules ??= new RulesModuleStore(_c);
    public ILevelClaimStore LevelClaims => _claims ??= new LevelClaimStore(_c);
    public DateTimeOffset Now => _c.Now;
    public string Actor => _c.Actor;
}

internal sealed class Ctx(HostDbContext db, TimeProvider clock, string actor, bool writable)
{
    public HostDbContext Db { get; } = db;
    public TimeProvider Clock { get; } = clock;
    public string Actor { get; } = actor;
    public DateTimeOffset Now => Clock.GetUtcNow();

    public void Writable()
    {
        if (!writable) throw new InvalidOperationException("a write inside a read transaction: use IHostData.WriteAsync");
    }

    public async Task Save()
    {
        Writable();
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
    }

    public string NewId() => Ulid.New(Clock);
}
