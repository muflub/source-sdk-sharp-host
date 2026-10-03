using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Proto;
using HostRefusal = SourceSharp.Host.Sdk.HostRefusal;

namespace SourceSharp.Host.Sdk.Tests;

/// <summary>SDK 2.5.0 (D-H16..18) through the real service: the store, teleporters, their notices.</summary>
public class StoreAndAnchorFacts
{
    /// <summary>A hub and a level (copy L1~X, depth 2) with SDKs, Ann leased on the level, Bob on the hub.</summary>
    static async Task<(HostSdk Hub, HostSdk Lvl, ICharacterLease Ann, ICharacterLease Bob)> World(SdkHarness h)
    {
        await h.Instance("hub-1", InstanceKind.Hub);
        await h.Instance("lvl-1", InstanceKind.Level, 2);
        await h.Data.WriteAsync((tx, _) => tx.Instances.Update("lvl-1", r => r with { LevelHash = "L1", LevelId = "L1~X" }));
        // FastOptions reopens a stream after 300 ms without an ack, which the service rightly takes as a
        // crash (measured: 2 runs in 10 lost lvl-1 to stream_closed on a busy box). Nothing here tests that.
        var o = SdkHarness.FastOptions();
        o.MissedAcksBeforeReconnect = 100;
        var hub = h.Sdk("hub-1", o);
        var lvl = h.Sdk("lvl-1", o);
        var ann = (await hub.Pumped(hub.Characters.Create("76561198000000001", "scout", "Ann"))).Value;
        var bob = (await hub.Pumped(hub.Characters.Create("76561198000000002", "heavy", "Bob"))).Value;
        var annLease = await lvl.Pumped(lvl.Characters.Lease(ann.Id));
        if (!annLease.Ok)
        {
            var row = await h.Data.ReadAsync((tx, _) => tx.Instances.Get("lvl-1"));
            throw new Xunit.Sdk.XunitException($"lease on lvl-1 refused {annLease.Refusal}: row {row?.State} {row?.Reason}");
        }
        return (hub, lvl, annLease.Value, (await hub.Pumped(hub.Characters.Lease(bob.Id))).Value);
    }

    [Fact]
    public async Task Instance_data_written_is_read_back_with_its_version()
    {
        await using var h = await SdkHarness.Start();
        var (_, lvl, _, _) = await World(h);
        var v = (await lvl.Pumped(lvl.Store.Instance.Set("k", [4]))).Value;
        var got = (await lvl.Pumped(lvl.Store.Instance.Get("k"))).Value!;
        Assert.Equal([4], got.Value);
        Assert.Equal(v, got.Version);
    }

    [Fact]
    public async Task A_missing_key_reads_as_null()
    {
        await using var h = await SdkHarness.Start();
        var (_, lvl, _, _) = await World(h);
        var r = await lvl.Pumped(lvl.Store.Level.Get("nothing"));
        Assert.True(r.Ok);
        Assert.Null(r.Value);
    }

    [Fact]
    public async Task A_stale_compare_and_set_is_a_typed_refusal()
    {
        await using var h = await SdkHarness.Start();
        var (_, lvl, _, _) = await World(h);
        var v = (await lvl.Pumped(lvl.Store.Level.Set("k", [1]))).Value;
        await lvl.Pumped(lvl.Store.Level.Set("k", [2]));
        Assert.Equal(HostError.VersionMismatch, (await lvl.Pumped(lvl.Store.Level.Set("k", [3], v))).Error);
    }

    [Fact]
    public async Task Character_data_rides_on_the_lease()
    {
        await using var h = await SdkHarness.Start();
        var (_, lvl, ann, _) = await World(h);
        await lvl.Pumped(lvl.Store.Character(ann).Set("quest", [9]));
        Assert.Equal([9], (await lvl.Pumped(lvl.Store.Character(ann).Get("quest"))).Value!.Value);
    }

    [Fact]
    public async Task Town_hears_when_a_level_writes_town_data()
    {
        await using var h = await SdkHarness.Start();
        var (hub, lvl, _, _) = await World(h);
        var heard = new List<StoreChangedInfo>();
        hub.Commands.StoreChanged += heard.Add;
        await hub.PumpUntil(() => hub.Session.Connected, what: "the hub's stream");
        await lvl.Pumped(lvl.Store.Town.Set("anchors/ann/view", [1]));
        await hub.PumpUntil(() => heard.Count > 0, what: "StoreChanged");
        Assert.Equal((HostStoreScope.Town, "town", "anchors/ann/view"), (heard[0].Scope, heard[0].Owner, heard[0].Key));
    }

    [Fact]
    public async Task Town_writes_a_levels_arrival_and_the_level_hears_and_reads_it()
    {
        await using var h = await SdkHarness.Start();
        var (hub, lvl, _, _) = await World(h);
        var heard = new List<StoreChangedInfo>();
        lvl.Commands.StoreChanged += heard.Add;
        await lvl.PumpUntil(() => lvl.Session.Connected, what: "the level's stream");
        await hub.Pumped(hub.Store.LevelCopy("L1~X").Set("arrival/ann", [7]));
        await lvl.PumpUntil(() => heard.Count > 0, what: "StoreChanged");
        Assert.Equal([7], (await lvl.Pumped(lvl.Store.Level.Get("arrival/ann"))).Value!.Value);
    }

    [Fact]
    public async Task A_teleporter_placed_on_the_level_is_listed_in_town()
    {
        await using var h = await SdkHarness.Start();
        var (hub, lvl, ann, bob) = await World(h);
        await h.Data.WriteAsync(async (tx, _) =>
        {
            var p = await tx.Parties.Create(bob.CharacterId);
            await tx.Parties.Invite(p.Id, ann.CharacterId);
            await tx.Parties.Join(p.Id, ann.CharacterId);
            return true;
        });
        var placed = (await lvl.Pumped(lvl.Travel.PlaceAnchor(ann, new HostVec3(1, 2, 3), 45))).Value;
        Assert.Equal(("L1~X", new HostVec3(1, 2, 3)), (placed.LevelId, placed.Position));
        var pads = (await hub.Pumped(hub.Travel.ListPartyAnchors(bob))).Value;
        Assert.Equal([ann.CharacterId], pads.Select(a => a.Owner));
    }

    [Fact]
    public async Task Town_hears_when_a_party_mate_places_a_teleporter()
    {
        await using var h = await SdkHarness.Start();
        var (hub, lvl, ann, bob) = await World(h);
        await h.Data.WriteAsync(async (tx, _) =>
        {
            var p = await tx.Parties.Create(bob.CharacterId);
            await tx.Parties.Invite(p.Id, ann.CharacterId);
            await tx.Parties.Join(p.Id, ann.CharacterId);
            return true;
        });
        var heard = new List<AnchorsChangedInfo>();
        hub.Commands.AnchorsChanged += heard.Add;
        await hub.PumpUntil(() => hub.Session.Connected, what: "the hub's stream");
        await lvl.Pumped(lvl.Travel.PlaceAnchor(ann, new HostVec3(1, 2, 3), 0));
        await hub.PumpUntil(() => heard.Count > 0, what: "AnchorsChanged");
    }

    [Fact]
    public async Task Map_ready_hands_the_level_its_teleporters()
    {
        await using var h = await SdkHarness.Start();
        var (_, lvl, ann, _) = await World(h);
        await lvl.Pumped(lvl.Travel.PlaceAnchor(ann, new HostVec3(1, 2, 3), 0));
        await h.Data.WriteAsync((tx, _) => tx.Instances.Update("lvl-1", r => r with { State = InstanceState.Booting }));
        Assert.Empty(lvl.Session.LevelAnchors);
        Assert.True((await lvl.Pumped(lvl.Session.MapReady(27015, "tf2_descent-2-L1"))).Ok);
        Assert.Equal([ann.CharacterId], lvl.Session.LevelAnchors.Select(a => a.Owner));
    }

    [Fact]
    public async Task Return_to_anchor_reaches_the_service_with_the_owner()
    {
        await using var h = await SdkHarness.Start();
        var (hub, _, ann, bob) = await World(h);
        Assert.True((await hub.Pumped(hub.Travel.ReturnToAnchor(bob, ann.CharacterId))).Ok);
        Assert.Equal([$"ReturnToAnchor {ann.CharacterId}"], h.TravelApi.Calls.Select(c => c.Method));
    }

    [Fact]
    public async Task Booting_answers_the_level_copy()
    {
        await using var h = await SdkHarness.Start();
        var (_, lvl, _, _) = await World(h);
        await h.Data.WriteAsync((tx, _) => tx.Instances.Update("lvl-1", r => r with { State = InstanceState.Booting }));
        var boot = await lvl.Pumped(lvl.Session.Booting("tf2_descent-2-L1"));
        Assert.Equal("L1~X", boot.Value.LevelId);
    }

    [Fact]
    public void The_new_reasons_map_to_typed_errors() =>
        Assert.Equal(
            [HostError.VersionMismatch, HostError.StoreFull, HostError.NoLevel, HostError.NotYourLevel, HostError.NoAnchor, HostError.NotALevel],
            new[] { "version_mismatch", "store_full", "no_level", "not_your_level", "no_anchor", "not_a_level" }
                .Select(r => HostRefusal.FromRpc(new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.FailedPrecondition, $"{r}: x"))).Error));
}
