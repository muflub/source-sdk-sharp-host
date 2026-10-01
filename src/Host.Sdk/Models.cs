using System.Numerics;
using Google.Protobuf;
using P = SourceSharp.Host.Proto;

namespace SourceSharp.Host.Sdk;

/// <summary>
/// A sheet, an item's instance data or checkpoint evidence: bytes + schema version, decoded only by
/// the rules module's own codecs (IGameRules.Sheets / .Items). The SDK never learns a stat name.
/// </summary>
public sealed record OpaquePayload(byte[] Data, uint SchemaVersion)
{
    public static OpaquePayload Empty { get; } = new([], 0);
}

public enum HostOwnerKind { Unspecified, Reserve, World, Corpse, Vendor, Character, Stash, TradeEscrow, LostAndFound, Admin }

/// <summary>An item as the ledger holds it. Only the host mints items; the SDK only ever receives them.</summary>
public sealed record HostItem(
    string Id, ulong Seed, string BaseType, int Rarity, int ItemLevel, int Count, bool Identified, OpaquePayload Instance,
    HostOwnerKind Owner, string OwnerId, int Slot, long Version, string ForCharacter, string InstanceId, int Tier);

public sealed record HostCharacter(
    string Id, string Account, string ClassName, string Name, bool Hardcore, bool Fallen, int Level, long Xp,
    int ReachedDepth, int Difficulty, OpaquePayload Sheet, long Australium, long Version);

public enum ModuleAnswer { Unspecified, Known, Send, Pending }

/// <summary>What Booting told this instance: the handshake's answer and the seed KillRoll replays from.</summary>
public sealed record BootInfo(ModuleAnswer Module, ulong InstanceSeed, string InstanceKind, int Depth, string LevelHash, bool Uploaded);

/// <summary>PlayerJoined's answer: whether to let the player in, and who the player really is (address and session).</summary>
public sealed record PlayerJoin(bool Allowed, string Reason, string SessionId, string ClientAddr);

public enum PeerSource { Sidecar, Service }

/// <summary>Who is behind a peer the engine sees.</summary>
public sealed record PeerIdentity(string Peer, string ClientAddr, string SessionId, string SteamId, PeerSource Source);

public sealed record CheckpointOutcome(long Version, IReadOnlyList<int> StaleReserveTiers);
public sealed record RevealOutcome(HostItem Item, long AustraliumBooked);

public sealed record HostParty(string Id, string Leader, IReadOnlyList<string> Members, IReadOnlyList<string> Invited, string LiveInstance);

public enum TradeStateKind { Unspecified, Open, Locked, Committed, Cancelled }

public sealed record HostTrade(
    string Id, string A, string B, TradeStateKind State, IReadOnlyList<string> OfferA, IReadOnlyList<string> OfferB,
    long AustraliumA, long AustraliumB, bool LockedA, bool LockedB, bool ConfirmedA, bool ConfirmedB)
{
    public bool BothLocked => LockedA && LockedB;
}

public sealed record LostAndFoundEntry(string Id, HostItem Item, long Fee, DateTimeOffset Since, string OriginInstance);

public enum TravelState { Unspecified, Started, Waiting, Refused }

public sealed record TravelOutcome(TravelState State, string InstanceId, TimeSpan Eta, string Reason);

/// <summary>Proto ↔ SDK records; the game never sees a generated type.</summary>
internal static class Map
{
    public static OpaquePayload ToSdk(this P.Blob? b) => b is null ? OpaquePayload.Empty : new(b.Data.ToByteArray(), b.SchemaVersion);
    public static P.Blob ToProto(this OpaquePayload p) => new() { Data = ByteString.CopyFrom(p.Data), SchemaVersion = p.SchemaVersion };
    public static P.Vec3 ToProto(this Vector3 v) => new() { X = v.X, Y = v.Y, Z = v.Z };

    public static HostItem ToSdk(this P.Item i) => new(i.Id, i.Seed, i.BaseType, i.Rarity, i.Ilvl, i.Count, i.Identified, i.Instance.ToSdk(),
        (HostOwnerKind)(int)i.OwnerKind, i.OwnerId, i.Slot, i.Version, i.ForCharacter, i.InstanceId, i.Tier);

    public static IReadOnlyList<HostItem> ToSdk(this P.ItemsResponse r) => r.Items.Select(ToSdk).ToList();

    public static HostCharacter ToSdk(this P.Character c) => new(c.Id, c.Account, c.ClassName, c.Name, c.Hardcore, c.Fallen, c.Level, c.Xp,
        c.ReachedDepth, c.Difficulty, c.Sheet.ToSdk(), c.Australium, c.Version);

    public static HostParty ToSdk(this P.Party p) => new(p.Id, p.Leader, p.Members.ToList(), p.Invited.ToList(), p.LiveInstance);

    public static HostTrade ToSdk(this P.Trade t) => new(t.Id, t.A, t.B, (TradeStateKind)(int)t.State, t.OfferA.ToList(), t.OfferB.ToList(),
        t.AustraliumA, t.AustraliumB, t.LockedA, t.LockedB, t.ConfirmedA, t.ConfirmedB);

    public static TravelOutcome ToSdk(this P.TravelResponse r) =>
        new((TravelState)(int)r.State, r.InstanceId, TimeSpan.FromMilliseconds(r.EtaMs), r.Reason);
}
