namespace SourceSharp.Host.Abstractions;

/// <summary>
/// The data seam (plan §4.2). Every write runs on one Channel-fed writer, in one
/// transaction, so a ledger move, its idempotency row and its audit row commit together
/// or not at all. Stores are handed out bound to that transaction; reads get a snapshot
/// on their own connection. No EF type crosses this interface.
/// </summary>
public interface IHostData
{
    Task<T> WriteAsync<T>(Func<IWriteTx, CancellationToken, Task<T>> work, CancellationToken ct = default);

    /// <summary>
    /// §4.4: a hit on <paramref name="requestId"/> replays the stored bytes; a miss runs
    /// <paramref name="work"/> and stores its bytes in the same transaction. A
    /// <see cref="HostRefusal"/> rolls back and stores nothing, so a retry is evaluated afresh.
    /// </summary>
    Task<IdempotentResult> IdempotentAsync(string requestId, string? instanceId,
        Func<IWriteTx, CancellationToken, Task<byte[]>> work, CancellationToken ct = default);

    Task<T> ReadAsync<T>(Func<IReadTx, CancellationToken, Task<T>> work, CancellationToken ct = default);

    /// <summary>
    /// For calls that orchestrate several transactions (travel) and so cannot run inside one:
    /// look the request up first, and store the response after. A concurrent duplicate can slip
    /// between the two; callers retry sequentially, and the orchestration itself is idempotent
    /// on its own state (claims, instance rows).
    /// </summary>
    Task<byte[]?> IdempotentLookupAsync(string requestId, CancellationToken ct = default);
    Task IdempotentStoreAsync(string requestId, string? instanceId, byte[] response, CancellationToken ct = default);
}

public readonly record struct IdempotentResult(byte[] Response, bool Replayed);

public interface IReadTx
{
    ICharacterStore Characters { get; }
    ILeaseStore Leases { get; }
    IItemLedger Items { get; }
    ILostAndFoundStore LostAndFound { get; }
    IPartyStore Parties { get; }
    ITradeStore Trades { get; }
    IInstanceStore Instances { get; }
    ISessionStore Sessions { get; }
    ILibraryStore Library { get; }
    ILevelStore Levels { get; }
    IMapJobStore MapJobs { get; }
    IVendorStore Vendors { get; }
    IAuditLog Audit { get; }
    ISettingsStore Settings { get; }
    IRulesModuleStore Modules { get; }
    ILevelClaimStore LevelClaims { get; }
    DateTimeOffset Now { get; }
}

/// <summary>The same stores, writable, inside the writer's transaction.</summary>
public interface IWriteTx : IReadTx
{
    string Actor { get; }
}

/// <summary>
/// A business refusal (proto/common.proto's reason list): rolls the transaction back and
/// maps to a gRPC status with "reason: text" at the API edge.
/// </summary>
public sealed class HostRefusal(RefusalCode code, string reason, string message) : Exception($"{reason}: {message}")
{
    public RefusalCode Code { get; } = code;
    public string Reason { get; } = reason;

    public static HostRefusal Precondition(string reason, string message) => new(RefusalCode.FailedPrecondition, reason, message);
    public static HostRefusal NotFound(string reason, string message) => new(RefusalCode.NotFound, reason, message);
    public static HostRefusal Denied(string reason, string message) => new(RefusalCode.PermissionDenied, reason, message);
    public static HostRefusal Exhausted(string reason, string message) => new(RefusalCode.ResourceExhausted, reason, message);
}

public enum RefusalCode { FailedPrecondition, NotFound, PermissionDenied, ResourceExhausted, Unauthenticated, Unimplemented, InvalidArgument }

public interface ICharacterStore
{
    Task<Account> EnsureAccount(string steamId);
    Task<Account?> GetAccount(string steamId);
    Task<IReadOnlyList<Account>> ListAccounts(int skip = 0, int take = 100);
    Task SetBanned(string steamId, bool banned, string? note);

    Task<IReadOnlyList<CharacterRecord>> List(string account, bool includeDeleted = false);
    Task<IReadOnlyList<CharacterRecord>> ListAll(int skip = 0, int take = 100);
    Task<CharacterRecord?> Get(string id);
    Task<CharacterRecord> Create(string account, string className, string name, bool hardcore, byte[] sheet, int sheetVersion, int level, long xp);

    /// <summary>Optimistic: refuses with version_conflict unless the row is at <paramref name="expectedVersion"/>; bumps the version.</summary>
    Task<CharacterRecord> Update(string id, long expectedVersion, Func<CharacterRecord, CharacterRecord> change);
    Task MarkDeleted(string id);

    /// <summary>Books a delta on the Australium ledger and the cached sum; refuses insufficient_funds below zero.</summary>
    Task<long> AddAustralium(string characterId, long delta, string reason, string? reference);
    Task<IReadOnlyList<AustraliumEntry>> AustraliumLedger(string characterId);
    Task<long> AustraliumLedgerSum(string characterId);
    /// <summary>Whether a ledger entry with this reference was already booked for the character (once-only bookings).</summary>
    Task<bool> HasAustraliumRef(string characterId, string reference);
    /// <summary>§5.4: the one thing reconcile fixes: the cached sum set back to its ledger. Returns the old cached value.</summary>
    Task<long> RecomputeAustralium(string characterId);
}

public abstract record LeaseOutcome
{
    public sealed record Granted(Lease Lease, bool Reentrant) : LeaseOutcome;
    public sealed record AlreadyLeased(string InstanceId) : LeaseOutcome;
}

public interface ILeaseStore
{
    /// <summary>§4.3: a second instance is refused; the same instance gets the same token (re-entrant).</summary>
    Task<LeaseOutcome> Acquire(string characterId, string instanceId, TimeSpan ttl);
    Task<Lease?> Get(string characterId);
    /// <summary>The lease, if <paramref name="token"/> is current; else HostRefusal stale_lease (audited by the caller).</summary>
    Task<Lease> Verify(string characterId, byte[] token);
    Task<int> RenewForInstance(string instanceId, TimeSpan ttl);
    /// <summary>Releases; a null token forces (admin, expiry, crash). False when there was nothing to release.</summary>
    Task<bool> Release(string characterId, byte[]? token);
    Task<IReadOnlyList<Lease>> Expired();
    Task<IReadOnlyList<Lease>> ForInstance(string instanceId);
    Task<IReadOnlyList<Lease>> All();
}

public sealed record ItemMove(
    string ItemId, OwnerKind From, OwnerKind To, string ToOwnerId, string EventKind,
    long? ExpectedVersion = null, string? InstanceId = null, int Slot = -1,
    bool ClearForCharacter = false, string? RequestId = null, string? ExpectedInstanceId = null);

public sealed record ItemQuery(
    OwnerKind? OwnerKind = null, string? OwnerId = null, string? InstanceId = null, ItemState? State = ItemState.Live,
    string? BaseType = null, int? Rarity = null, int? MinItemLevel = null, int? MaxItemLevel = null, string? IdPrefix = null,
    int Skip = 0, int Take = 500);

/// <summary>
/// The ledger's primitives (R-H1, §5.1). Exactly one owner at a time: a move is one
/// conditional UPDATE on (id, version, owner_kind) that must affect one row, else
/// wrong_owner / version_conflict and no event. Policies (reserve, reveal, sweep,
/// trades, fees) live above this, in the service.
/// </summary>
public interface IItemLedger
{
    Task<ItemRecord> Mint(NewItem item, OwnerKind owner, string ownerId, string? instanceId, int slot, string mintedBy, string? requestId);
    Task<ItemRecord?> Get(string id);
    Task<ItemRecord> Move(ItemMove move);
    Task<ItemRecord> Terminate(string itemId, OwnerKind from, ItemState terminal, string reason, string? requestId);
    /// <summary>Changes an item's own data (identify, repair, admin edit): not a move.</summary>
    Task<ItemRecord> Mutate(string itemId, long expectedVersion, byte[] instance, int schemaVersion, bool identified, bool adminEdited, string eventKind, string? requestId);
    Task<IReadOnlyList<ItemRecord>> Query(ItemQuery query);
    Task<int> Count(ItemQuery query);
    Task<IReadOnlyList<ItemEvent>> Events(string itemId);
    /// <summary>Whether any event of <paramref name="kind"/> exists in <paramref name="instanceId"/> (e.g. a kill already revealed).</summary>
    Task<bool> HasEvent(string instanceId, string kind);
    /// <summary>Items minted, and items that reached a terminal state, over the whole ledger: Live = minted − terminal.</summary>
    Task<(long Minted, long Terminal, long Live)> Totals();
    Task<int> PurgeTerminalOlderThan(DateTimeOffset cutoff);
}

public interface ILostAndFoundStore
{
    Task<LostAndFoundRecord> Add(string characterId, string itemId, long fee, string? originInstance);
    Task<IReadOnlyList<LostAndFoundRecord>> ForCharacter(string characterId);
    Task<IReadOnlyList<LostAndFoundRecord>> All(int skip = 0, int take = 200);
    Task<LostAndFoundRecord?> Get(string id);
    Task<LostAndFoundRecord?> ForItem(string itemId);
    Task Remove(string id);
    Task SetFee(string id, long fee);
}

public interface IPartyStore
{
    Task<Party> Create(string leader);
    Task<Party?> Get(string id);
    Task<Party?> ForCharacter(string characterId);
    Task<IReadOnlyList<Party>> All();
    Task<Party> Invite(string partyId, string characterId);
    Task<Party> Join(string partyId, string characterId);
    Task<Party?> Remove(string partyId, string characterId);
    Task Disband(string partyId);
}

public interface ITradeStore
{
    Task<TradeRecord> Open(string hubInstance, string a, string b);
    Task<TradeRecord?> Get(string id);
    Task<TradeRecord> Save(TradeRecord trade);
    Task<IReadOnlyList<TradeRecord>> ForHub(string hubInstance, bool openOnly = true);
    Task<IReadOnlyList<TradeRecord>> All(int skip = 0, int take = 200);
}

public interface IInstanceStore
{
    Task<InstanceRecord> Add(InstanceRecord instance);
    Task<InstanceRecord?> Get(string id);
    Task<InstanceRecord?> ByPodName(string podName);
    Task<InstanceRecord> Update(string id, Func<InstanceRecord, InstanceRecord> change);
    Task<IReadOnlyList<InstanceRecord>> NonTerminal();
    Task<IReadOnlyList<InstanceRecord>> List(InstanceKind? kind = null, InstanceState? state = null, int skip = 0, int take = 200);
    Task<int> CountLevels(params InstanceState[] states);
}

public interface ISessionStore
{
    Task<SessionRecord> Upsert(SessionRecord session);
    Task<SessionRecord?> Get(string id);
    Task<SessionRecord?> ByClientAddr(string clientAddr);
    Task<SessionRecord?> ByPeer(string peer);
    /// <summary>A peer is unique only within one backend (D-H10: loopback addresses per game instance).</summary>
    Task<SessionRecord?> ByBackendPeer(string backend, string peer);
    Task<IReadOnlyList<SessionRecord>> ForBackend(string backend);
    Task<IReadOnlyList<SessionRecord>> ForSteamId(string steamId);
    Task<IReadOnlyList<SessionRecord>> Open();
    Task Close(string id, string reason);

    Task<PlayerPin?> GetPin(string steamId);
    Task<PlayerPin?> PinByPeer(string peer);
    Task<PlayerPin> SetPin(PlayerPin pin);
    Task<IReadOnlyList<PlayerPin>> Pins();
    Task RemovePin(string steamId);
}

public interface ILibraryStore
{
    Task<PackRecord> AddPack(PackRecord pack);
    Task<PackRecord?> GetPack(long id);
    Task<IReadOnlyList<PackRecord>> Packs(string mod, string? library = null);
    Task<int> NextVersion(string mod, string library);
    Task<PackRecord> SetPackState(long id, PackState state);
    Task<PackAssignment> Assign(PackAssignment assignment);
    Task<PackAssignment?> Assignment(string mod, int depth);
    Task<IReadOnlyList<PackAssignment>> Assignments(string mod);
}

public interface ILevelStore
{
    Task<LevelRecord> Add(LevelRecord level);
    Task<LevelRecord?> Get(string hash);
    Task<LevelRecord> SetState(string hash, LevelState state, string? error = null);
    /// <summary>ready → handed_out, once, ever (§9.3): the oldest ready level of that depth and pack, or null.</summary>
    Task<LevelRecord?> HandOut(int depth, long packId, string instanceId);
    Task<int> Count(int depth, long? packId, LevelState state);
    Task<IReadOnlyList<LevelRecord>> List(int? depth = null, LevelState? state = null, long? packId = null, int skip = 0, int take = 500);
}

public interface IMapJobStore
{
    /// <summary>Queues a job unless one with the same key is queued or running; returns the live one either way.</summary>
    Task<MapJob> Enqueue(MapJobKind kind, string key, int priority);
    /// <summary>The next queued job by (priority, created), moved to running; bakes before links at equal priority.</summary>
    Task<MapJob?> TakeNext();
    Task<MapJob> Complete(long id, string? log);
    Task<MapJob> Fail(long id, string log, bool requeue);
    Task<IReadOnlyList<MapJob>> List(MapJobState? state = null, int take = 200);
}

public interface IVendorStore
{
    Task Add(string hubInstance, string vendor, string itemId);
    Task<IReadOnlyList<VendorStockRecord>> Stock(string hubInstance, string? vendor = null);
    Task Clear(string hubInstance, string? vendor = null);
}

public sealed record AuditQuery(string? Actor = null, string? Action = null, string? Target = null, int Skip = 0, int Take = 200);

public interface IAuditLog
{
    Task Write(string action, string target, object? before = null, object? after = null, string? actor = null);
    Task<IReadOnlyList<AuditEntry>> List(AuditQuery query);
    Task<int> Count(AuditQuery query);
}

public interface ISettingsStore
{
    Task<Setting?> Get(string key);
    Task Set(string key, string valueJson, string updatedBy);
    Task<IReadOnlyList<Setting>> All();
}

public interface IRulesModuleStore
{
    Task<RulesModuleRecord?> Get(string sha256);
    Task<RulesModuleRecord> Add(RulesModuleRecord module);
    Task<RulesModuleRecord> SetState(string sha256, ModuleState state);
    Task<IReadOnlyList<RulesModuleRecord>> List();
}

/// <summary>D-H12: persisted depth → level claims per character; a level is retired only when no claim and no running instance refer to it.</summary>
public interface ILevelClaimStore
{
    Task<LevelClaim?> Get(string characterId, int depth);
    Task<LevelClaim> Set(string characterId, int depth, string levelHash);
    Task<IReadOnlyList<LevelClaim>> ForCharacter(string characterId);
    Task<IReadOnlyList<string>> CharactersWithClaims();
    Task Touch(string characterId);
    /// <summary>Drops every claim of the character; returns the level hashes it held.</summary>
    Task<IReadOnlyList<string>> Drop(string characterId);
    Task<int> ClaimsOn(string levelHash);
}
