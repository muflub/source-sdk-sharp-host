using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Admin;
using SourceSharp.Host.Contracts;
using SourceSharp.Host.MapPool;
using SourceSharp.Host.Testing;

namespace SourceSharp.Host.Admin.Tests;

/// <summary>Real stores on :memory:, FakeGameRules, the admin fakes, and AdminActions over them.</summary>
public sealed class AdminWorld : IAsyncDisposable
{
    public TestData D { get; } = new();
    public FakeGameRules Rules { get; } = new();
    public ServiceOptions Options { get; } = new() { MapPool = { MapsPath = Path.Combine(Path.GetTempPath(), "lane-a-" + Guid.NewGuid().ToString("N")) } };
    public PackCatalog Catalog { get; }
    public PoolSignal Signal { get; } = new();
    public AdminFakeLedger Ledger { get; } = new();
    public AdminFakeGateway Gateway { get; } = new();
    public AdminFakeModules Modules { get; }
    public AdminFakeBackups Backups { get; }
    public AdminFakeLifecycle Lifecycle { get; }
    public FakeInstanceHost Pods { get; }
    public AdminActions Actions { get; }
    public string Actor => Options.Admin.Actor;
    int _n;

    public AdminWorld()
    {
        Modules = new AdminFakeModules(D.Data);
        Backups = new AdminFakeBackups(D.Clock);
        Lifecycle = new AdminFakeLifecycle(D.Data);
        Pods = new FakeInstanceHost(D.Clock);
        Catalog = new PackCatalog(D.Data, new FakePackValidator(Options.Mod.Name), new LevelStorage(Options.MapPool), Options, Signal);
        Actions = new AdminActions(D.Data, new SingleRulesProvider(Rules), Options,
            new AdminCollaborators(Lifecycle, Pods, Catalog, PoolSignal: Signal, Ledger: Ledger, Gateway: Gateway, Modules: Modules, Backups: Backups));
    }

    public const string Steam = "76561198000000001";

    public async Task<CharacterRecord> Character(string name = "Ann", bool hardcore = false, string account = Steam, int level = 10)
    {
        var sheet = FakeGameRules.SheetCodec.Write(new FakeGameRules.Sheet("scout", name, level, 0, 0, hardcore, false));
        return await D.Write(async tx =>
        {
            await tx.Characters.EnsureAccount(account);
            return await tx.Characters.Create(account, "scout", name, hardcore, sheet.Data, sheet.SchemaVersion, level, 0);
        });
    }

    public Task<InstanceRecord> Instance(string id = "lvl-1", InstanceKind kind = InstanceKind.Level, string? pod = null, string? rules = "fake") =>
        D.Write(tx => tx.Instances.Add(new InstanceRecord(id, kind, InstanceState.Live, kind == InstanceKind.Hub ? 0 : 3, null, null,
            pod ?? $"descent-{id}", $"uid-{id}", "10.42.0.9", 27015, "hash", null, null, rules, 1234, D.Clock.GetUtcNow(),
            null, null, null, null, null, null, null, 0, 27015)));

    public Task Lease(string characterId, string instanceId = "lvl-1") =>
        D.Write(tx => tx.Leases.Acquire(characterId, instanceId, TimeSpan.FromSeconds(30)));

    /// <summary>A rolled item as the rules would mint it (so it replays).</summary>
    public RolledItem Rolled(int tier = 0, ulong seed = 7) =>
        Rules.RollItem(new ItemRollContext("scout", 10, 3, 15, tier, "depth3", "kill"), seed);

    public Task<ItemRecord> Mint(OwnerKind owner, string ownerId, int slot = -1, string? instanceId = null, string? forCharacter = null)
    {
        var r = Rolled(seed: (ulong)++_n);
        return D.Write(tx => tx.Items.Mint(new NewItem(r.Seed, r.BaseType, r.Rarity, r.ItemLevel, r.Count, r.Identified, r.Instance,
            r.SchemaVersion, r.Tier, 10, forCharacter), owner, ownerId, instanceId, slot, "test", null));
    }

    public Task<IReadOnlyList<AuditEntry>> Audit(string? action = null) => D.Read(tx => tx.Audit.List(new AuditQuery(Action: action)));
    public Task<ItemRecord?> Item(string id) => D.Read(tx => tx.Items.Get(id));
    public Task<CharacterRecord?> Get(string id) => D.Read(tx => tx.Characters.Get(id));

    public async ValueTask DisposeAsync()
    {
        await D.DisposeAsync();
        if (Directory.Exists(Options.MapPool.MapsPath)) Directory.Delete(Options.MapPool.MapsPath, recursive: true);
    }
}
