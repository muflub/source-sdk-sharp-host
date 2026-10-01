using Descent.Service.Ledger;
using Microsoft.Extensions.Options;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;
using SourceSharp.Host.Testing;

namespace Descent.Service.Tests.Ledger;

/// <summary>The real stores on :memory:, FakeGameRules, a hub and a level instance, leased characters.</summary>
public sealed class LedgerHarness : IAsyncDisposable
{
    public TestData D { get; } = new();
    public FakeGameRules Rules { get; } = new();
    public DescentLedger Ledger { get; }
    public ServiceOptions Options { get; } = new();
    public InstanceRecord Hub { get; private set; } = null!;
    public InstanceRecord Level { get; private set; } = null!;
    readonly Dictionary<string, byte[]> _tokens = [];
    int _requests;

    LedgerHarness()
    {
        Ledger = new DescentLedger(new SingleRulesProvider(Rules), Microsoft.Extensions.Options.Options.Create(Options));
    }

    public static async Task<LedgerHarness> Create(int depth = 3)
    {
        var h = new LedgerHarness();
        h.Hub = await h.AddInstance("hub-1", InstanceKind.Hub, 0);
        h.Level = await h.AddInstance("lvl-1", InstanceKind.Level, depth);
        return h;
    }

    public Task<InstanceRecord> AddInstance(string id, InstanceKind kind, int depth) =>
        D.Write(tx => tx.Instances.Add(new InstanceRecord(id, kind, InstanceState.Live, depth, null, null, $"descent-{id}", $"uid-{id}",
            "10.42.0.9", 27015, "hash", null, null, "fake", Seed: 1234, D.Clock.GetUtcNow(), null, null, null, null, null, null, null, 0, 27015)));

    public string Req() => $"req-{++_requests}";

    public async Task<string> Character(string name = "Ann", bool hardcore = false, string account = "76561198000000001", int level = 10)
    {
        var sheet = FakeGameRules.SheetCodec.Write(new FakeGameRules.Sheet("scout", name, level, 0, 0, hardcore, false));
        var ch = await D.Write(tx => tx.Characters.Create(account, "scout", name, hardcore, sheet.Data, sheet.SchemaVersion, level, 0));
        return ch.Id;
    }

    /// <summary>Leases <paramref name="characterId"/> on <paramref name="instance"/> (releasing any previous lease) and remembers the token.</summary>
    public async Task<byte[]> Lease(string characterId, InstanceRecord instance)
    {
        await D.Write(tx => tx.Leases.Release(characterId, null));
        var g = (LeaseOutcome.Granted)await D.Write(tx => tx.Leases.Acquire(characterId, instance.Id, TimeSpan.FromSeconds(30)));
        return _tokens[characterId] = g.Lease.Token;
    }

    public byte[] Token(string characterId) => _tokens[characterId];

    public Task<T> W<T>(Func<IWriteTx, Task<T>> work) => D.Write(work);
    public Task<T> R<T>(Func<IReadTx, Task<T>> work) => D.Read(work);

    /// <summary>The first kill sequence whose replayed roll on this instance drops at <paramref name="tier"/> (or does not drop, for -1).</summary>
    public uint KillFor(InstanceRecord instance, int tier, int partySize = 1, string robot = "heavy", uint from = 0)
    {
        for (var k = from; k < from + 10_000; k++)
        {
            var roll = Rules.KillRoll(instance.Seed, k, robot, instance.Depth, partySize);
            if (tier < 0 ? !roll.Drop : roll.Drop && roll.Tier == tier) return k;
        }
        throw new InvalidOperationException($"no kill rolls tier {tier}");
    }

    /// <summary>Mints an item straight into a backpack, as an admin gift would.</summary>
    public async Task<ItemRecord> Give(string characterId, int tier = 0, int slot = -1)
    {
        var rolled = Rules.RollItem(new ItemRollContext("scout", 10, 3, 15, tier, "depth3", "test"), (ulong)Random.Shared.NextInt64());
        var used = (await R(tx => tx.Items.Query(new ItemQuery(OwnerKind: OwnerKind.Character, OwnerId: characterId)))).Select(i => i.Slot).ToHashSet();
        if (slot < 0) slot = Enumerable.Range(0, 100).First(s => !used.Contains(s));
        return await W(tx => tx.Items.Mint(new NewItem(rolled.Seed, rolled.BaseType, rolled.Rarity, rolled.ItemLevel, rolled.Count, rolled.Identified,
            rolled.Instance, rolled.SchemaVersion, rolled.Tier, 10), OwnerKind.Character, characterId, null, slot, "admin", null));
    }

    public async Task<(long Minted, long Terminal, long Live)> Totals() => await R(tx => tx.Items.Totals());

    public async Task AssertBalanced()
    {
        var (minted, terminal, live) = await Totals();
        Assert.Equal(minted - terminal, live);
    }

    public ValueTask DisposeAsync() => D.DisposeAsync();
}
