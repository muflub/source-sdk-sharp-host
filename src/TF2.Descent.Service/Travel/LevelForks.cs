using TF2.Descent.Service.Ledger;
using SourceSharp.Host.Abstractions;

namespace TF2.Descent.Service.Travel;

/// <summary>
/// D-H18: a player who leaves the party keeps their levels and their teleporter on copies of their
/// own. Every level copy the leaver shares with someone staying is forked: a new copy id, the
/// LEVEL store data copied over, the leaver's claim and anchor moved to it. Ledger items are never
/// copied: the leaver's own corpse moves, with its Australium, to a pending place for the fork
/// that the fork's first instance takes over at MapReady (<see cref="AdoptCorpses"/>), and goes to
/// the owner's Lost &amp; Found if the fork ends unplayed (<see cref="EndPending"/>).
/// </summary>
public static class LevelForks
{
    public const string ForkedAction = "level.forked";
    const string PendingPrefix = "level:";

    /// <summary>Where a corpse waits for its level copy's next instance (an item's instance_id).</summary>
    public static string Pending(string levelId) => PendingPrefix + levelId;

    public static async Task<IReadOnlyList<(string From, string To)>> ForLeaver(IWriteTx tx, IKeyValueStore store, string leaver, IReadOnlyCollection<string> stayers, CancellationToken ct)
    {
        var shared = new HashSet<string>();
        foreach (var s in stayers)
            foreach (var c in await tx.LevelClaims.ForCharacter(s)) shared.Add(c.LevelId);
        var forks = new List<(string From, string To)>();
        foreach (var claim in await tx.LevelClaims.ForCharacter(leaver))
        {
            if (!shared.Contains(claim.LevelId)) continue; // already theirs alone
            var fork = LevelIds.Fork(claim.LevelId, tx.Now);
            await tx.LevelClaims.Set(leaver, claim.Depth, fork);
            forks.Add((claim.LevelId, fork));
        }
        if (forks.Count == 0) return forks;
        var to = forks.ToDictionary(f => f.From, f => f.To);

        if (await tx.Anchors.Get(leaver) is { } anchor && to.TryGetValue(anchor.LevelId, out var anchorFork))
            await tx.Anchors.Set(anchor with { LevelId = anchorFork });

        var running = await tx.Instances.NonTerminal();
        foreach (var (from, fork) in forks)
            foreach (var i in running.Where(i => i.LevelCopy == from))
                await DescentLedger.MoveCorpse(tx, leaver, i.Id, Pending(fork), "level_fork");

        // In store.db, inside this transaction's span: a host rollback leaves only unclaimed copies, which the store sweep drops.
        foreach (var (from, fork) in forks)
            await store.Copy(new StoreOwner(StoreScope.Level, from), new StoreOwner(StoreScope.Level, fork), ct);

        await tx.Audit.Write(ForkedAction, leaver, null, new { forks = forks.Select(f => new { from = f.From, to = f.To }) });
        return forks;
    }

    /// <summary>At MapReady: corpses waiting for this instance's level copy now lie on it.</summary>
    public static async Task<int> AdoptCorpses(IWriteTx tx, InstanceRecord instance)
    {
        if (instance.Kind != InstanceKind.Level || instance.LevelCopy is not { } level) return 0;
        var waiting = await tx.Items.Query(new ItemQuery(OwnerKind: OwnerKind.Corpse, InstanceId: Pending(level), Take: 10_000));
        var n = 0;
        foreach (var owner in waiting.Select(i => i.OwnerId).Distinct())
            n += await DescentLedger.MoveCorpse(tx, owner, Pending(level), instance.Id, "corpse_arrived");
        return n;
    }

    /// <summary>A level copy nobody claims any more: corpses still waiting for it go to Lost &amp; Found.</summary>
    public static Task<(int ToLostAndFound, int Destroyed)> EndPending(IWriteTx tx, string levelId) =>
        DescentLedger.CorpsesToLostAndFound(tx, Pending(levelId), null);
}
