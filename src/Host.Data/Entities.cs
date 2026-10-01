using SourceSharp.Host.Abstractions;

namespace SourceSharp.Host.Data;

// EF rows of plan §4.1. Internal: stores hand out the Abstractions records.

internal sealed class AccountRow
{
    public string SteamId { get; set; } = "";
    public DateTimeOffset Created { get; set; }
    public bool Banned { get; set; }
    public string? Note { get; set; }
    public Account ToRecord() => new(SteamId, Created, Banned, Note);
}

internal sealed class CharacterRow
{
    public string Id { get; set; } = "";
    public string Account { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Hardcore { get; set; }
    public bool Fallen { get; set; }
    public int Level { get; set; }
    public long Xp { get; set; }
    public int ReachedDepth { get; set; }
    public int Difficulty { get; set; }
    public byte[] Sheet { get; set; } = [];
    public int SheetVersion { get; set; }
    public long Australium { get; set; }
    public long Version { get; set; }
    public bool Deleted { get; set; }

    public CharacterRecord ToRecord() => new(Id, Account, ClassName, Name, Hardcore, Fallen, Level, Xp, ReachedDepth, Difficulty, Sheet, SheetVersion, Australium, Version, Deleted);

    public void Apply(CharacterRecord r)
    {
        ClassName = r.ClassName; Name = r.Name; Hardcore = r.Hardcore; Fallen = r.Fallen; Level = r.Level; Xp = r.Xp;
        ReachedDepth = r.ReachedDepth; Difficulty = r.Difficulty; Sheet = r.Sheet; SheetVersion = r.SheetVersion; Deleted = r.Deleted;
    }
}

internal sealed class LeaseRow
{
    public string CharacterId { get; set; } = "";
    public string InstanceId { get; set; } = "";
    public byte[] Token { get; set; } = [];
    public DateTimeOffset Issued { get; set; }
    public DateTimeOffset Expires { get; set; }
    public DateTimeOffset Heartbeat { get; set; }
    public Lease ToRecord() => new(CharacterId, InstanceId, Token, Issued, Expires, Heartbeat);
}

internal sealed class ItemRow
{
    public string Id { get; set; } = "";
    public ulong Seed { get; set; }
    public string BaseType { get; set; } = "";
    public int Rarity { get; set; }
    public int ItemLevel { get; set; }
    public int Count { get; set; }
    public bool Identified { get; set; }
    public byte[] Instance { get; set; } = [];
    public int SchemaVersion { get; set; }
    public string? ForCharacter { get; set; }
    public OwnerKind OwnerKind { get; set; }
    public string OwnerId { get; set; } = "";
    public int Slot { get; set; }
    public string? InstanceId { get; set; }
    public int Tier { get; set; }
    public int RolledForLevel { get; set; }
    public string MintedBy { get; set; } = "";
    public DateTimeOffset MintedAt { get; set; }
    public long Version { get; set; }
    public ItemState State { get; set; }
    public string? TerminalReason { get; set; }
    public DateTimeOffset? TerminalAt { get; set; }
    public bool AdminEdited { get; set; }

    public ItemRecord ToRecord() => new(Id, Seed, BaseType, Rarity, ItemLevel, Count, Identified, Instance, SchemaVersion,
        ForCharacter, OwnerKind, OwnerId, Slot, InstanceId, Tier, RolledForLevel, MintedBy, MintedAt, Version, State, TerminalReason, AdminEdited);
}

internal sealed class ItemEventRow
{
    public long Id { get; set; }
    public string ItemId { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public string Kind { get; set; } = "";
    public string? FromOwner { get; set; }
    public string? ToOwner { get; set; }
    public string? InstanceId { get; set; }
    public string? RequestId { get; set; }
    public string Actor { get; set; } = "";
    public ItemEvent ToRecord() => new(Id, ItemId, At, Kind, FromOwner, ToOwner, InstanceId, RequestId, Actor);
}

internal sealed class AustraliumRow
{
    public long Id { get; set; }
    public string CharacterId { get; set; } = "";
    public long Delta { get; set; }
    public string Reason { get; set; } = "";
    public string? Ref { get; set; }
    public DateTimeOffset At { get; set; }
    public AustraliumEntry ToRecord() => new(Id, CharacterId, Delta, Reason, Ref, At);
}

internal sealed class LostAndFoundRow
{
    public string Id { get; set; } = "";
    public string CharacterId { get; set; } = "";
    public string ItemId { get; set; } = "";
    public long Fee { get; set; }
    public DateTimeOffset Since { get; set; }
    public string? OriginInstance { get; set; }
    public LostAndFoundRecord ToRecord() => new(Id, CharacterId, ItemId, Fee, Since, OriginInstance);
}

internal sealed class PartyRow
{
    public string Id { get; set; } = "";
    public string Leader { get; set; } = "";
    public DateTimeOffset Created { get; set; }
}

internal sealed class PartyMemberRow
{
    public string PartyId { get; set; } = "";
    public string CharacterId { get; set; } = "";
    public bool Invited { get; set; }
    public DateTimeOffset Since { get; set; }
}

internal sealed class TradeRow
{
    public string Id { get; set; } = "";
    public string HubInstance { get; set; } = "";
    public string A { get; set; } = "";
    public string B { get; set; } = "";
    public TradeStatus State { get; set; }
    public string OfferJson { get; set; } = "{}";
    public bool LockedA { get; set; }
    public bool LockedB { get; set; }
    public bool ConfirmedA { get; set; }
    public bool ConfirmedB { get; set; }
    public DateTimeOffset? Committed { get; set; }
    public DateTimeOffset Created { get; set; }
}

internal sealed class InstanceRow
{
    public string Id { get; set; } = "";
    public InstanceKind Kind { get; set; }
    public InstanceState State { get; set; }
    public int Depth { get; set; }
    public string? PartyId { get; set; }
    public string? LevelHash { get; set; }
    public string? PodName { get; set; }
    public string? PodUid { get; set; }
    public string? PodIp { get; set; }
    public int Port { get; set; }
    public string TokenHash { get; set; } = "";
    public string? ModImage { get; set; }
    public string? ModImageDigest { get; set; }
    public string? RulesSha256 { get; set; }
    public ulong Seed { get; set; }
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset? Booted { get; set; }
    public DateTimeOffset? LiveAt { get; set; }
    public DateTimeOffset? DrainAt { get; set; }
    public DateTimeOffset? ReapedAt { get; set; }
    public int? ExitCode { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset? LastHeartbeat { get; set; }
    public int Players { get; set; }
    public int ExpectedPort { get; set; }

    public InstanceRecord ToRecord() => new(Id, Kind, State, Depth, PartyId, LevelHash, PodName, PodUid, PodIp, Port, TokenHash,
        ModImage, ModImageDigest, RulesSha256, Seed, Created, Booted, LiveAt, DrainAt, ReapedAt, ExitCode, Reason, LastHeartbeat, Players, ExpectedPort);

    public static InstanceRow From(InstanceRecord r) => new InstanceRow { Id = r.Id }.Apply(r);

    public InstanceRow Apply(InstanceRecord r)
    {
        Kind = r.Kind; State = r.State; Depth = r.Depth; PartyId = r.PartyId; LevelHash = r.LevelHash; PodName = r.PodName; PodUid = r.PodUid;
        PodIp = r.PodIp; Port = r.Port; TokenHash = r.TokenHash; ModImage = r.ModImage; ModImageDigest = r.ModImageDigest; RulesSha256 = r.RulesSha256;
        Seed = r.Seed; Created = r.Created; Booted = r.Booted; LiveAt = r.LiveAt; DrainAt = r.DrainAt; ReapedAt = r.ReapedAt; ExitCode = r.ExitCode;
        Reason = r.Reason; LastHeartbeat = r.LastHeartbeat; Players = r.Players; ExpectedPort = r.ExpectedPort;
        return this;
    }
}

internal sealed class SessionRow
{
    public string Id { get; set; } = "";
    public string ClientAddr { get; set; } = "";
    public string? SteamId { get; set; }
    public string? InstanceId { get; set; }
    public string Backend { get; set; } = "";
    public string Peer { get; set; } = "";
    public string State { get; set; } = "";
    public DateTimeOffset Opened { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public DateTimeOffset? IdentifiedAt { get; set; }
    public string? CloseReason { get; set; }
    public SessionRecord ToRecord() => new(Id, ClientAddr, SteamId, InstanceId, Backend, Peer, State, Opened, LastSeen, IdentifiedAt);
}

internal sealed class PlayerRow
{
    public string SteamId { get; set; } = "";
    public string PinnedPeer { get; set; } = "";
    public string Allocator { get; set; } = "";
    public DateTimeOffset Since { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public string PreviousJson { get; set; } = "[]";
}

internal sealed class PackRow
{
    public long Id { get; set; }
    public string Mod { get; set; } = "";
    public string Library { get; set; } = "";
    public int Version { get; set; }
    public string PackId { get; set; } = "";
    public string LibraryVersion { get; set; } = "";
    public string Source { get; set; } = "";
    public string UploadedBy { get; set; } = "";
    public DateTimeOffset UploadedAt { get; set; }
    public long Bytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string? LintJson { get; set; }
    public string? Notes { get; set; }
    public PackState State { get; set; }
    public PackRecord ToRecord() => new(Id, Mod, Library, Version, PackId, LibraryVersion, Source, UploadedBy, UploadedAt, Bytes, Sha256, LintJson, Notes, State);
}

internal sealed class PackAssignmentRow
{
    public string Mod { get; set; } = "";
    public int Depth { get; set; }
    public long PackId { get; set; }
    public bool Pinned { get; set; }
    public string AssignedBy { get; set; } = "";
    public DateTimeOffset AssignedAt { get; set; }
    public OldLevelsPolicy OldLevels { get; set; }
    public PackAssignment ToRecord() => new(Mod, Depth, PackId, Pinned, AssignedBy, AssignedAt, OldLevels);
}

internal sealed class LevelRow
{
    public string Hash { get; set; } = "";
    public long PackId { get; set; }
    public int Depth { get; set; }
    public string Tileset { get; set; } = "";
    public ulong Seed { get; set; }
    public int Difficulty { get; set; }
    public LevelState State { get; set; }
    public DateTimeOffset Created { get; set; }
    public string? HandedTo { get; set; }
    public long Bytes { get; set; }
    public string? Error { get; set; }
    public string? RulesSha256 { get; set; }
    public LevelRecord ToRecord() => new(Hash, PackId, Depth, Tileset, Seed, Difficulty, State, Created, HandedTo, Bytes, Error, RulesSha256);
}

internal sealed class MapJobRow
{
    public long Id { get; set; }
    public MapJobKind Kind { get; set; }
    public string Key { get; set; } = "";
    public int Priority { get; set; }
    public MapJobState State { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset? Started { get; set; }
    public DateTimeOffset? Finished { get; set; }
    public string? Log { get; set; }
    public MapJob ToRecord() => new(Id, Kind, Key, Priority, State, Attempts, Created, Started, Finished, Log);
}

internal sealed class VendorStockRow
{
    public string HubInstance { get; set; } = "";
    public string Vendor { get; set; } = "";
    public DateTimeOffset RolledAt { get; set; }
    public string ItemId { get; set; } = "";
}

internal sealed class IdempotencyRow
{
    public string RequestId { get; set; } = "";
    public string? InstanceId { get; set; }
    public byte[] Response { get; set; } = [];
    public DateTimeOffset At { get; set; }
}

internal sealed class AuditRow
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public string Target { get; set; } = "";
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
    public AuditEntry ToRecord() => new(Id, At, Actor, Action, Target, BeforeJson, AfterJson);
}

internal sealed class SettingRow
{
    public string Key { get; set; } = "";
    public string ValueJson { get; set; } = "";
    public string UpdatedBy { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class RulesModuleRow
{
    public string Sha256 { get; set; } = "";
    public string Assembly { get; set; } = "";
    public string Version { get; set; } = "";
    public string ContractVersion { get; set; } = "";
    public string DepsJson { get; set; } = "[]";
    public DateTimeOffset FirstSeen { get; set; }
    public string? FirstInstance { get; set; }
    public ModuleState State { get; set; }
    public long Bytes { get; set; }
    public RulesModuleRecord ToRecord() => new(Sha256, Assembly, Version, ContractVersion, DepsJson, FirstSeen, FirstInstance, State, Bytes);
}

internal sealed class LevelClaimRow
{
    public string CharacterId { get; set; } = "";
    public int Depth { get; set; }
    public string LevelHash { get; set; } = "";
    public DateTimeOffset Since { get; set; }
    public DateTimeOffset LastUsed { get; set; }
    public LevelClaim ToRecord() => new(CharacterId, Depth, LevelHash, Since, LastUsed);
}
