using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;

namespace Descent.Service.Ledger;

public sealed partial class DescentLedger
{
    /// <summary>A hub creates a character with the rules' first sheet; the host keeps only the summary columns.</summary>
    public async Task<CharacterRecord> CreateCharacter(IWriteTx tx, InstanceRecord caller, string account, string className, string name, bool hardcore)
    {
        RequireHub(caller);
        var r = RulesFor(caller);
        var sheet = r.NewCharacter(className, name, hardcore);
        var s = r.Sheets.Summarize(sheet);
        var ch = await tx.Characters.Create(account, s.ClassName, s.Name, hardcore, sheet.Data, sheet.SchemaVersion, s.Level, s.Xp);
        await tx.Audit.Write("character.create", ch.Id, null, new { account, className, name, hardcore, instance = caller.Id });
        return ch;
    }

    /// <summary>A hub deletes; refused while any instance leases the character.</summary>
    public static async Task DeleteCharacter(IWriteTx tx, InstanceRecord caller, string characterId)
    {
        RequireHub(caller);
        if (await tx.Leases.Get(characterId) is { } lease)
            throw HostRefusal.Precondition("already_leased", $"{characterId} is leased by {lease.InstanceId}");
        await tx.Characters.MarkDeleted(characterId);
        await tx.Audit.Write("character.delete", characterId, null, new { instance = caller.Id });
    }

    public async Task<(Lease Lease, CharacterRecord Character)> LeaseCharacter(IWriteTx tx, InstanceRecord caller, string characterId)
    {
        var ch = await tx.Characters.Get(characterId) ?? throw HostRefusal.NotFound("no_character", characterId);
        if (ch.Deleted) throw HostRefusal.NotFound("no_character", characterId);
        var outcome = await tx.Leases.Acquire(characterId, caller.Id, O.Lease.Ttl);
        return outcome switch
        {
            LeaseOutcome.Granted g => (g.Lease, ch),
            LeaseOutcome.AlreadyLeased a => throw HostRefusal.Precondition("already_leased", $"{characterId} is leased by {a.InstanceId}"),
            _ => throw new InvalidOperationException(),
        };
    }

    /// <summary>Release on hop or logout, after the final checkpoint; the reserve on this instance goes with the lease (§5.2a).</summary>
    public static async Task ReleaseCharacter(IWriteTx tx, InstanceRecord caller, string characterId, byte[] token)
    {
        await Leased(tx, caller, characterId, token);
        var swept = await SweepReserve(tx, characterId, caller.Id, "reserve_released");
        await tx.Leases.Release(characterId, token);
        await tx.Audit.Write("lease.release", characterId, null, new { instance = caller.Id, reserveSwept = swept });
    }

    /// <summary>
    /// §6.2: idempotent at the API; the sheet is replayed through IGameRules.ValidateCheckpoint
    /// and refused with the first failing rule named; every carried item that is not
    /// admin-edited must still replay (the forged-item fact: an instance the seed does not
    /// reproduce is refused, replay_mismatch). A level-up sweeps stale reserve tiers.
    /// </summary>
    public async Task<(CharacterRecord Character, IReadOnlyList<int> StaleTiers)> Checkpoint(IWriteTx tx, InstanceRecord caller,
        string characterId, byte[] token, byte[] sheet, int sheetVersion, byte[] evidence, int evidenceVersion, long expectedVersion)
    {
        var (ch, _) = await Leased(tx, caller, characterId, token);
        if (ch.Version != expectedVersion)
            throw HostRefusal.Precondition("version_conflict", $"{characterId} is at version {ch.Version}, not {expectedVersion}");
        var r = RulesFor(caller);
        var before = new CharacterSheet(ch.Sheet, ch.SheetVersion);
        var after = new CharacterSheet(sheet, sheetVersion);
        var verdict = r.ValidateCheckpoint(before, after, new CheckpointEvidence(evidence, evidenceVersion));
        if (!verdict.Ok) throw HostRefusal.Precondition(verdict.FailedRule ?? "invalid_checkpoint", $"checkpoint of {characterId} refused by the rules");

        foreach (var item in await Backpack(tx, characterId))
        {
            if (item.AdminEdited) continue;
            RolledItem rolled;
            try { rolled = r.Items.Decode(item.Instance, item.SchemaVersion); }
            catch (Exception) { throw HostRefusal.Precondition("replay_mismatch", $"item {item.Id} does not decode"); }
            if (!r.Replays(rolled)) throw HostRefusal.Precondition("replay_mismatch", $"item {item.Id} does not replay from its seed");
        }

        var s = r.Sheets.Summarize(after);
        var updated = await tx.Characters.Update(characterId, ch.Version, c => c with
        {
            Sheet = sheet, SheetVersion = sheetVersion, Level = s.Level, Xp = s.Xp,
            ReachedDepth = Math.Max(c.ReachedDepth, s.ReachedDepth), Fallen = c.Fallen || s.Fallen,
        });
        var stale = s.Level > ch.Level ? await SweepStaleTiers(tx, characterId, caller.Id, s.Level) : [];
        return (updated, stale);
    }

    public static async Task<CharacterRecord> SetReachedDepth(IWriteTx tx, InstanceRecord caller, string characterId, byte[] token, int depth)
    {
        var (ch, _) = await Leased(tx, caller, characterId, token);
        return depth <= ch.ReachedDepth ? ch : await tx.Characters.Update(characterId, ch.Version, c => c with { ReachedDepth = depth });
    }
}
