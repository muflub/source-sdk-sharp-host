namespace SourceSharp.Host.Abstractions;

// The rows of plan §4.1, as the stores hand them out: plain records, no EF types.
// Times are DateTimeOffset (UTC); ids are ULIDs as strings unless the table says otherwise.

public sealed record Account(string SteamId, DateTimeOffset Created, bool Banned, string? Note);

public sealed record CharacterRecord(
    string Id, string Account, string ClassName, string Name, bool Hardcore, bool Fallen,
    int Level, long Xp, int ReachedDepth, int Difficulty,
    byte[] Sheet, int SheetVersion, long Australium, long Version, bool Deleted);

public sealed record Lease(string CharacterId, string InstanceId, byte[] Token, DateTimeOffset Issued, DateTimeOffset Expires, DateTimeOffset Heartbeat);

/// <summary>Owners of §5.1: exactly one at a time.</summary>
public enum OwnerKind { Reserve = 1, World, Corpse, Vendor, Character, Stash, TradeEscrow, LostAndFound, Admin }

public enum ItemState { Live = 1, Swept, Destroyed, Consumed }

public sealed record ItemRecord(
    string Id, ulong Seed, string BaseType, int Rarity, int ItemLevel, int Count, bool Identified,
    byte[] Instance, int SchemaVersion,
    string? ForCharacter, OwnerKind OwnerKind, string OwnerId, int Slot, string? InstanceId,
    int Tier, int RolledForLevel,
    string MintedBy, DateTimeOffset MintedAt, long Version, ItemState State, string? TerminalReason, bool AdminEdited);

/// <summary>What a mint needs from the rules' roll; the ledger adds id, owner and bookkeeping.</summary>
public sealed record NewItem(
    ulong Seed, string BaseType, int Rarity, int ItemLevel, int Count, bool Identified,
    byte[] Instance, int SchemaVersion, int Tier = -1, int RolledForLevel = 0, string? ForCharacter = null);

public sealed record ItemEvent(
    long Id, string ItemId, DateTimeOffset At, string Kind, string? FromOwner, string? ToOwner,
    string? InstanceId, string? RequestId, string Actor);

public sealed record AustraliumEntry(long Id, string CharacterId, long Delta, string Reason, string? Ref, DateTimeOffset At);

public sealed record LostAndFoundRecord(string Id, string CharacterId, string ItemId, long Fee, DateTimeOffset Since, string? OriginInstance);

public sealed record Party(string Id, string Leader, DateTimeOffset Created, IReadOnlyList<string> Members, IReadOnlyList<string> Invited);

public enum TradeStatus { Open = 1, Locked, Committed, Cancelled }

public sealed record TradeRecord(
    string Id, string HubInstance, string A, string B, TradeStatus State,
    IReadOnlyList<string> OfferA, IReadOnlyList<string> OfferB, long AustraliumA, long AustraliumB,
    bool LockedA, bool LockedB, bool ConfirmedA, bool ConfirmedB, DateTimeOffset? Committed);

public enum InstanceKind { Hub = 1, Level }

/// <summary>§7.3: requested → level_ready → creating → booting → live → draining → reaped, plus crashed and failed.</summary>
public enum InstanceState { Requested = 1, LevelReady, Creating, Booting, Live, Suspect, Draining, Reaped, Crashed, Failed }

public sealed record InstanceRecord(
    string Id, InstanceKind Kind, InstanceState State, int Depth, string? PartyId, string? LevelHash,
    string? PodName, string? PodUid, string? PodIp, int Port, string TokenHash,
    string? ModImage, string? ModImageDigest, string? RulesSha256, ulong Seed,
    DateTimeOffset Created, DateTimeOffset? Booted, DateTimeOffset? LiveAt, DateTimeOffset? DrainAt, DateTimeOffset? ReapedAt,
    int? ExitCode, string? Reason, DateTimeOffset? LastHeartbeat, int Players, int ExpectedPort)
{
    public bool Terminal => State is InstanceState.Reaped or InstanceState.Crashed or InstanceState.Failed;
}

public sealed record SessionRecord(
    string Id, string ClientAddr, string? SteamId, string? InstanceId, string Backend, string Peer,
    string State, DateTimeOffset Opened, DateTimeOffset LastSeen, DateTimeOffset? IdentifiedAt);

public sealed record PlayerPin(string SteamId, string PinnedPeer, string Allocator, DateTimeOffset Since, DateTimeOffset LastSeen, IReadOnlyList<string> Previous);

public enum PackState { Validating = 1, Ready, Rejected, Retired }

public sealed record PackRecord(
    long Id, string Mod, string Library, int Version, string PackId, string LibraryVersion, string Source,
    string UploadedBy, DateTimeOffset UploadedAt, long Bytes, string Sha256, string? LintJson, string? Notes, PackState State);

public enum OldLevelsPolicy { Drain = 1, Retire }

public sealed record PackAssignment(string Mod, int Depth, long PackId, bool Pinned, string AssignedBy, DateTimeOffset AssignedAt, OldLevelsPolicy OldLevels);

public enum LevelState { Generating = 1, Ready, HandedOut, Retired, Failed }

public sealed record LevelRecord(
    string Hash, long PackId, int Depth, string Tileset, ulong Seed, int Difficulty, LevelState State,
    DateTimeOffset Created, string? HandedTo, long Bytes, string? Error, string? RulesSha256);

public enum MapJobKind { Bake = 1, Link }
public enum MapJobState { Queued = 1, Running, Done, Failed }

public sealed record MapJob(
    long Id, MapJobKind Kind, string Key, int Priority, MapJobState State, int Attempts,
    DateTimeOffset Created, DateTimeOffset? Started, DateTimeOffset? Finished, string? Log);

public sealed record VendorStockRecord(string HubInstance, string Vendor, DateTimeOffset RolledAt, string ItemId);

public sealed record AuditEntry(long Id, DateTimeOffset At, string Actor, string Action, string Target, string? BeforeJson, string? AfterJson);

public enum ModuleState { Pending = 1, Approved, Quarantined }

public sealed record RulesModuleRecord(
    string Sha256, string Assembly, string Version, string ContractVersion, string DepsJson,
    DateTimeOffset FirstSeen, string? FirstInstance, ModuleState State, long Bytes);

public sealed record Setting(string Key, string ValueJson, string UpdatedBy, DateTimeOffset UpdatedAt);

/// <summary>D-H12: a character's sticky level for a depth, for its session.</summary>
public sealed record LevelClaim(string CharacterId, int Depth, string LevelHash, DateTimeOffset Since, DateTimeOffset LastUsed);
