using SourceSharp.Host.Abstractions;

namespace Descent.Service.Tests.Ledger;

/// <summary>
/// Plan §5.3 / CLAUDE.md: the property test over random RPC sequences with mid-sequence
/// reaps. After every step: minted − terminal = live, every live item has one owner that
/// exists, nothing Live is left on a reaped instance, and every item's event chain is
/// continuous (each event's from is the previous event's to). Refusals are expected and
/// ignored — a refusal must leave the ledger unchanged, which the invariants check too.
/// </summary>
public class LedgerPropertyFacts
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Random_sequences_with_reaps_keep_every_item_owned_exactly_once(int seed)
    {
        var rng = new Random(seed);
        await using var h = await LedgerHarness.Create();
        var chars = new List<string>();
        for (var i = 0; i < 3; i++) chars.Add(await h.Character($"C{i}", account: $"{i + 1}"));
        var levels = new List<InstanceRecord> { h.Level };
        foreach (var c in chars) await h.Lease(c, h.Level);
        uint kill = 0;
        var reaps = 0;

        for (var step = 0; step < 150; step++)
        {
            var c = chars[rng.Next(chars.Count)];
            var lease = await h.R(tx => tx.Leases.Get(c));
            var at = lease is null ? null : await h.R(tx => tx.Instances.Get(lease.InstanceId));
            try
            {
                switch (rng.Next(10))
                {
                    case 0 when at is not null:
                        await h.W(tx => h.Ledger.TakeReserve(tx, at, c, h.Token(c), [(0, 2), (1, 1)], h.Req()));
                        break;
                    case 1 or 2 when at is not null:
                    {
                        var reserve = await h.R(tx => tx.Items.Query(new ItemQuery(OwnerKind: OwnerKind.Reserve, OwnerId: c, InstanceId: at.Id)));
                        if (reserve.Count == 0) break;
                        var pick = reserve[rng.Next(reserve.Count)];
                        var k = h.KillFor(at, pick.Tier, from: ++kill + (uint)rng.Next(5));
                        kill = k;
                        await h.W(tx => h.Ledger.Reveal(tx, at, c, h.Token(c), pick.Id, k, "heavy", [], h.Req()));
                        break;
                    }
                    case 3 when at is not null:
                    {
                        var world = await h.R(tx => tx.Items.Query(new ItemQuery(OwnerKind: OwnerKind.World, InstanceId: at.Id)));
                        if (world.Count > 0) await h.W(tx => h.Ledger.Claim(tx, at, c, h.Token(c), world[rng.Next(world.Count)].Id, h.Req()));
                        break;
                    }
                    case 4 when at is not null:
                    {
                        var carried = await h.R(tx => tx.Items.Query(new ItemQuery(OwnerKind: OwnerKind.Character, OwnerId: c)));
                        if (carried.Count > 0) await h.W(tx => h.Ledger.Drop(tx, at, c, h.Token(c), carried[rng.Next(carried.Count)].Id, h.Req()));
                        break;
                    }
                    case 5 when at is not null && at.Kind == InstanceKind.Level:
                        await h.W(tx => h.Ledger.RecordDeath(tx, at, c, h.Token(c), h.Req()));
                        break;
                    case 6 when at is not null:
                        await h.W(tx => h.Ledger.LootCorpse(tx, at, c, h.Token(c), h.Req()));
                        break;
                    case 7:
                    {
                        // The reap of a random live level, mid-sequence: sweep, then release its leases.
                        var live = levels.Where(l => l.State == InstanceState.Live).ToList();
                        if (live.Count == 0) break;
                        var victim = live[rng.Next(live.Count)];
                        await h.W(async tx =>
                        {
                            await h.Ledger.SweepInstance(tx, victim);
                            foreach (var l in await tx.Leases.ForInstance(victim.Id)) await tx.Leases.Release(l.CharacterId, null);
                            return await tx.Instances.Update(victim.Id, i => i with { State = InstanceState.Reaped });
                        });
                        levels[levels.IndexOf(victim)] = victim with { State = InstanceState.Reaped };
                        reaps++;
                        break;
                    }
                    case 8:
                    {
                        // A hop: the reserve on the old instance is swept with the lease, the character moves to a fresh level.
                        var next = await h.AddInstance($"lvl-{levels.Count + 1}", InstanceKind.Level, 1 + rng.Next(5));
                        levels.Add(next);
                        if (at is not null)
                            await h.W(tx => Descent.Service.Ledger.DescentLedger.SweepReserve(tx, c, at.Id, "reserve_released"));
                        await h.Lease(c, next);
                        break;
                    }
                    case 9 when at is null:
                        await h.Lease(c, levels.LastOrDefault(l => l.State == InstanceState.Live) ?? h.Hub);
                        break;
                }
            }
            catch (HostRefusal) { }

            await AssertInvariants(h, levels);
        }
        Assert.True(reaps > 0, "the sequence never reaped: the property was not exercised");
        Assert.True((await h.Totals()).Minted > 10, "the sequence minted almost nothing");
    }

    static async Task AssertInvariants(LedgerHarness h, List<InstanceRecord> levels)
    {
        var (minted, terminal, live) = await h.Totals();
        Assert.Equal(minted - terminal, live);
        var items = await h.R(tx => tx.Items.Query(new ItemQuery(Take: 100_000)));
        var reaped = levels.Where(l => l.State == InstanceState.Reaped).Select(l => l.Id).ToHashSet();
        foreach (var i in items)
        {
            if (i.OwnerKind is OwnerKind.World or OwnerKind.Reserve or OwnerKind.Corpse)
                Assert.False(i.InstanceId is not null && reaped.Contains(i.InstanceId), $"{i.Id} is {i.OwnerKind} on reaped {i.InstanceId}");
            var events = await h.R(tx => tx.Items.Events(i.Id));
            for (var e = 1; e < events.Count; e++)
                Assert.True(events[e].FromOwner == events[e - 1].ToOwner || events[e].FromOwner is null,
                    $"{i.Id}: event {e} from {events[e].FromOwner} but the previous went to {events[e - 1].ToOwner}");
            Assert.Equal($"{i.OwnerKind}:{i.OwnerId}", events[^1].ToOwner ?? events[^1].FromOwner);
        }
    }
}
