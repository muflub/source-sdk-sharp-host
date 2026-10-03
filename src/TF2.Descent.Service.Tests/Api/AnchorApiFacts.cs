using System.Collections.Concurrent;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using SourceSharp.Host.Abstractions;
using P = SourceSharp.Host.Proto;

namespace TF2.Descent.Service.Tests.Api;

/// <summary>D-H16: teleporter anchors over gRPC: placed on a level, listed by town, rebuilt at MapReady.</summary>
public class AnchorApiFacts
{
    sealed class Commands : IInstanceCommands
    {
        public ConcurrentQueue<string> Sent { get; } = new();
        public Task<bool> Send(string instanceId, InstanceCommand command, CancellationToken ct = default) { Sent.Enqueue($"{instanceId} {command}"); return Task.FromResult(true); }
    }

    sealed record World(ApiHarness H, Commands Sent, Metadata Hub, Metadata Lvl, string Ann, Google.Protobuf.ByteString AnnToken) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => H.DisposeAsync();

        public Task<P.Anchor> Place(Metadata on, string who, Google.Protobuf.ByteString token, float x = 1) =>
            H.Travel.PlaceAnchorAsync(new P.PlaceAnchorRequest { RequestId = H.Req(), CharacterId = who, LeaseToken = token, Position = new P.Vec3 { X = x, Y = 2, Z = 3 }, Yaw = 90 }, on).ResponseAsync;
    }

    /// <summary>A hub, a level running copy L1~X at depth 2, and Ann leased on the level.</summary>
    static async Task<World> Start()
    {
        var sent = new Commands();
        var h = await ApiHarness.Start(b => b.Services.AddSingleton<IInstanceCommands>(sent));
        var (_, hub) = await h.Instance("hub-1", InstanceKind.Hub);
        var (_, lvl) = await h.Instance("lvl-a", InstanceKind.Level, 2);
        await h.Data.WriteAsync((tx, _) => tx.Instances.Update("lvl-a", r => r with { LevelHash = "L1", LevelId = "L1~X" }));
        var (ann, token) = await h.LeasedCharacter(hub, lvl);
        return new World(h, sent, hub, lvl, ann, token);
    }

    static async Task<string> Refused(Func<Task> call) => ApiHarness.Reason(await Assert.ThrowsAsync<RpcException>(call));

    [Fact]
    public async Task A_teleporter_is_placed_on_the_level_copy_the_player_stands_on()
    {
        await using var w = await Start();
        var a = await w.Place(w.Lvl, w.Ann, w.AnnToken);
        Assert.Equal((w.Ann, "L1~X", "L1", 2, 1f, 90f), (a.Owner, a.LevelId, a.LevelHash, a.Depth, a.Position.X, a.Yaw));
    }

    [Fact]
    public async Task A_second_teleporter_replaces_the_first()
    {
        await using var w = await Start();
        await w.Place(w.Lvl, w.Ann, w.AnnToken, x: 1);
        await w.Place(w.Lvl, w.Ann, w.AnnToken, x: 5);
        var on = await w.H.Data.ReadAsync((tx, _) => tx.Anchors.OnLevel("L1~X"));
        Assert.Equal(5f, Assert.Single(on).X);
    }

    [Fact]
    public async Task A_hub_cannot_place_a_teleporter()
    {
        await using var w = await Start();
        var (bo, boToken) = await w.H.LeasedCharacter(w.Hub, w.Hub, account: "76561198000000002");
        Assert.Equal("not_a_level", await Refused(() => w.Place(w.Hub, bo, boToken)));
    }

    [Fact]
    public async Task Placing_for_a_character_leased_elsewhere_is_refused()
    {
        await using var w = await Start();
        var (bo, boToken) = await w.H.LeasedCharacter(w.Hub, w.Hub, account: "76561198000000002");
        Assert.Equal("wrong_instance", await Refused(() => w.Place(w.Lvl, bo, boToken)));
    }

    [Fact]
    public async Task Town_lists_the_teleporters_of_the_players_party()
    {
        await using var w = await Start();
        var (bo, boToken) = await w.H.LeasedCharacter(w.Hub, w.Hub, account: "76561198000000002");
        await w.H.Data.WriteAsync(async (tx, _) =>
        {
            var p = await tx.Parties.Create(bo);
            await tx.Parties.Invite(p.Id, w.Ann);
            await tx.Parties.Join(p.Id, w.Ann);
            return true;
        });
        await w.Place(w.Lvl, w.Ann, w.AnnToken);
        var list = await w.H.Travel.ListPartyAnchorsAsync(new P.TravelRequest { RequestId = w.H.Req(), CharacterId = bo, LeaseToken = boToken }, w.Hub);
        Assert.Equal([w.Ann], list.Anchors.Select(a => a.Owner));
    }

    [Fact]
    public async Task A_level_cannot_list_party_teleporters()
    {
        await using var w = await Start();
        Assert.Equal("not_a_hub", await Refused(() =>
            w.H.Travel.ListPartyAnchorsAsync(new P.TravelRequest { RequestId = w.H.Req(), CharacterId = w.Ann, LeaseToken = w.AnnToken }, w.Lvl).ResponseAsync));
    }

    [Fact]
    public async Task Placing_tells_the_hub_holding_a_party_members_lease()
    {
        await using var w = await Start();
        var (bo, _) = await w.H.LeasedCharacter(w.Hub, w.Hub, account: "76561198000000002");
        var party = await w.H.Data.WriteAsync(async (tx, _) =>
        {
            var p = await tx.Parties.Create(bo);
            await tx.Parties.Invite(p.Id, w.Ann);
            await tx.Parties.Join(p.Id, w.Ann);
            return p;
        });
        await w.Place(w.Lvl, w.Ann, w.AnnToken);
        Assert.Equal([$"hub-1 AnchorsChanged {{ PartyId = {party.Id} }}"], w.Sent.Sent);
    }

    [Fact]
    public async Task Clearing_removes_the_teleporter()
    {
        await using var w = await Start();
        await w.Place(w.Lvl, w.Ann, w.AnnToken);
        await w.H.Travel.ClearAnchorAsync(new P.TravelRequest { RequestId = w.H.Req(), CharacterId = w.Ann, LeaseToken = w.AnnToken }, w.Lvl);
        Assert.Null(await w.H.Data.ReadAsync((tx, _) => tx.Anchors.Get(w.Ann)));
    }

    [Fact]
    public async Task A_fresh_instance_of_the_level_copy_gets_its_teleporters_at_map_ready()
    {
        await using var w = await Start();
        await w.Place(w.Lvl, w.Ann, w.AnnToken);
        var (_, fresh) = await w.H.Instance("lvl-b", InstanceKind.Level, 2, InstanceState.Booting);
        await w.H.Data.WriteAsync((tx, _) => tx.Instances.Update("lvl-b", r => r with { LevelHash = "L1", LevelId = "L1~X" }));
        var ready = await w.H.Instances.MapReadyAsync(new P.MapReadyRequest { RequestId = w.H.Req(), Port = 27015 }, fresh);
        Assert.Equal([w.Ann], ready.Anchors.Select(a => a.Owner));
    }

    [Fact]
    public async Task Another_copy_of_the_level_gets_none_of_its_teleporters()
    {
        await using var w = await Start();
        await w.Place(w.Lvl, w.Ann, w.AnnToken);
        var (_, other) = await w.H.Instance("lvl-b", InstanceKind.Level, 2, InstanceState.Booting);
        await w.H.Data.WriteAsync((tx, _) => tx.Instances.Update("lvl-b", r => r with { LevelHash = "L1", LevelId = "L1" }));
        var ready = await w.H.Instances.MapReadyAsync(new P.MapReadyRequest { RequestId = w.H.Req(), Port = 27015 }, other);
        Assert.Empty(ready.Anchors);
    }

    // ---- D-H18: leaving the party keeps the teleporter, on a copy of the level

    static async Task<(string Bo, Google.Protobuf.ByteString BoToken, string PartyId)> Partied(World w)
    {
        var (bo, boToken) = await w.H.LeasedCharacter(w.Hub, w.Hub, account: "76561198000000002");
        var party = await w.H.Data.WriteAsync(async (tx, _) =>
        {
            var p = await tx.Parties.Create(bo);
            await tx.Parties.Invite(p.Id, w.Ann);
            await tx.Parties.Join(p.Id, w.Ann);
            await tx.LevelClaims.Set(w.Ann, 2, "L1~X");
            await tx.LevelClaims.Set(bo, 2, "L1~X");
            return p;
        });
        await w.Place(w.Lvl, w.Ann, w.AnnToken);
        while (w.Sent.Sent.TryDequeue(out _)) { }
        return (bo, boToken, party.Id);
    }

    [Fact]
    public async Task Leaving_the_party_moves_the_teleporter_to_a_copy_and_tells_town()
    {
        await using var w = await Start();
        var (_, _, partyId) = await Partied(w);
        await w.H.Parties.LeaveAsync(new P.PartyRequest { RequestId = w.H.Req(), CharacterId = w.Ann, LeaseToken = w.AnnToken, PartyId = partyId }, w.Lvl);
        var anchor = (await w.H.Data.ReadAsync((tx, _) => tx.Anchors.Get(w.Ann)))!;
        Assert.Equal(("L1", true), (anchor.LevelHash, anchor.LevelId != "L1~X"));
        Assert.Contains($"hub-1 AnchorsChanged {{ PartyId = {partyId} }}", w.Sent.Sent);
    }

    [Fact]
    public async Task A_kicked_player_keeps_the_teleporter_on_a_copy()
    {
        await using var w = await Start();
        var (bo, boToken, partyId) = await Partied(w);
        await w.H.Parties.KickAsync(new P.PartyRequest { RequestId = w.H.Req(), CharacterId = bo, LeaseToken = boToken, PartyId = partyId, TargetCharacter = w.Ann }, w.Hub);
        Assert.NotEqual("L1~X", (await w.H.Data.ReadAsync((tx, _) => tx.Anchors.Get(w.Ann)))!.LevelId);
        Assert.Equal("L1~X", (await w.H.Data.ReadAsync((tx, _) => tx.LevelClaims.Get(bo, 2)))!.LevelId);
    }

    [Fact]
    public async Task Deleting_a_character_removes_its_teleporter()
    {
        await using var w = await Start();
        await w.Place(w.Lvl, w.Ann, w.AnnToken);
        await w.H.Data.WriteAsync(async (tx, _) => { await tx.Characters.MarkDeleted(w.Ann); return true; });
        Assert.Null(await w.H.Data.ReadAsync((tx, _) => tx.Anchors.Get(w.Ann)));
    }
}
