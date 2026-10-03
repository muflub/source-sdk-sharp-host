using TF2.Descent.Service.Ledger;
using SourceSharp.Host.Abstractions;
using P = SourceSharp.Host.Proto;

namespace TF2.Descent.Service.Travel;

/// <summary>
/// D-H16: teleporter entrances. A character places one (a new one replaces the old) on the level
/// copy it stands on; town lays out one exit pad per anchor of the player's party and hears
/// AnchorsChanged when one of them changes; a level gets its anchors at MapReady, so a fresh
/// instance of that copy rebuilds them. The way through is <see cref="TravelCoordinator.ToAnchor"/>.
/// </summary>
public sealed class Anchors(IHostData data, IInstanceCommands commands)
{
    public const string PlacedAction = "travel.anchor";
    public const string ClearedAction = "travel.anchor_cleared";

    public static async Task<P.Anchor> Place(IWriteTx tx, InstanceRecord caller, string characterId, byte[] token, P.Vec3? position, float yaw)
    {
        if (caller.Kind != InstanceKind.Level) throw HostRefusal.Denied("not_a_level", $"{caller.Id} is a hub: a teleporter goes on a level");
        var level = caller.LevelCopy ?? throw HostRefusal.Precondition("no_level", $"{caller.Id} runs no level yet");
        await DescentLedger.Leased(tx, caller, characterId, token);
        var p = position ?? new P.Vec3();
        var anchor = await tx.Anchors.Set(new AnchorRecord(characterId, level, caller.Depth, p.X, p.Y, p.Z, yaw, tx.Now));
        await tx.Audit.Write(PlacedAction, characterId, null, new { level, depth = caller.Depth, x = p.X, y = p.Y, z = p.Z, yaw }, $"instance:{caller.Id}");
        return ToProto(anchor);
    }

    public static async Task<P.Ack> Clear(IWriteTx tx, InstanceRecord caller, string characterId, byte[] token)
    {
        await DescentLedger.Leased(tx, caller, characterId, token);
        var had = await tx.Anchors.Get(characterId);
        if (had is not null && await tx.Anchors.Remove(characterId))
            await tx.Audit.Write(ClearedAction, characterId, new { level = had.LevelId, depth = had.Depth }, null, $"instance:{caller.Id}");
        return new P.Ack();
    }

    /// <summary>Hub only: the anchors of the caller's party, or the caller's own when in no party, oldest first.</summary>
    public static async Task<P.AnchorList> ListParty(IReadTx tx, InstanceRecord caller, string characterId, byte[] token)
    {
        DescentLedger.RequireHub(caller);
        var lease = await tx.Leases.Verify(characterId, token);
        if (lease.InstanceId != caller.Id)
            throw HostRefusal.Denied("wrong_instance", $"{characterId} is leased by {lease.InstanceId}, not {caller.Id}");
        var party = await tx.Parties.ForCharacter(characterId);
        var list = new P.AnchorList();
        list.Anchors.AddRange((await tx.Anchors.For(party?.Members ?? [characterId])).Select(ToProto));
        return list;
    }

    /// <summary>The hubs holding a lease of <paramref name="characterId"/>'s party (or of it alone) hear that its anchors changed.</summary>
    public async Task Notify(string characterId, CancellationToken ct)
    {
        var (partyId, hubs) = await data.ReadAsync(async (tx, _) =>
        {
            var party = await tx.Parties.ForCharacter(characterId);
            var hubs = new HashSet<string>();
            foreach (var member in party?.Members ?? [characterId])
                if (await tx.Leases.Get(member) is { } l && await tx.Instances.Get(l.InstanceId) is { Kind: InstanceKind.Hub, Terminal: false })
                    hubs.Add(l.InstanceId);
            return (party?.Id ?? "", hubs);
        }, ct);
        foreach (var hub in hubs) await commands.Send(hub, new InstanceCommand.AnchorsChanged(partyId), ct);
    }

    public static P.Anchor ToProto(AnchorRecord a) => new()
    {
        Owner = a.CharacterId, LevelId = a.LevelId, LevelHash = a.LevelHash, Depth = a.Depth,
        Position = new P.Vec3 { X = a.X, Y = a.Y, Z = a.Z }, Yaw = a.Yaw, PlacedUnixMs = a.Placed.ToUnixTimeMilliseconds(),
    };
}
