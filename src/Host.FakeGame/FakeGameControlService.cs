using Grpc.Core;
using SourceSharp.Host.FakeGame.Control;

namespace SourceSharp.Host.FakeGame;

/// <summary>FakeGameControl (fakegame.proto): every call runs on the fake's main thread, as a dev command would.</summary>
public sealed class FakeGameControlService(FakeGameServer game) : Control.FakeGameControl.FakeGameControlBase
{
    static Reply R((bool Ok, string Reason) r) => new() { Ok = r.Ok, Reason = r.Reason };
    static ItemsReply I((bool Ok, string Reason, IReadOnlyList<string> Ids) r) => new() { Ok = r.Ok, Reason = r.Reason, ItemIds = { r.Ids } };

    public override Task<StatusReply> Status(Empty request, ServerCallContext context) => game.OnMain(() =>
    {
        var s = new StatusReply
        {
            InstanceId = game.Config.InstanceId, Booted = game.Boot is not null, MapReady = game.MapIsReady,
            ModuleSha256 = game.ModuleSha256 ?? "", Draining = game.Draining, Connected = game.Sdk.Session.Connected,
        };
        if (game.Boot is { } b)
        {
            s.Kind = b.InstanceKind; s.Depth = b.Depth; s.LevelHash = b.LevelHash; s.InstanceSeed = b.InstanceSeed; s.ModuleUploaded = b.Uploaded;
        }
        s.Players.AddRange(game.Players);
        s.Events.AddRange(game.Events);
        return Task.FromResult(s);
    });

    PlayerReply Player(string steamId, bool ok, string reason)
    {
        var r = new PlayerReply { Ok = ok, Reason = reason, Steamid = steamId };
        if (game.PlayerInfo(steamId) is { } p) { r.CharacterId = p.CharacterId; r.Peer = p.Peer; r.Leased = p.Leased; r.ClientAddr = p.ClientAddr; }
        return r;
    }

    public override Task<PlayerReply> Join(JoinRequest request, ServerCallContext context) => game.OnMain(async () =>
    {
        var (ok, reason) = await game.Simulate(request.Steamid);
        return Player(request.Steamid, ok, reason);
    });

    public override Task<Reply> Leave(PlayerRequest request, ServerCallContext context) => game.OnMain(async () => R(await game.LeaveNow(request.Steamid)));

    public override Task<PlayerReply> Player(PlayerRequest request, ServerCallContext context) => game.OnMain(() =>
        Task.FromResult(Player(request.Steamid, game.PlayerInfo(request.Steamid) is not null, "")));

    public override Task<KillReply> Kill(KillRequest request, ServerCallContext context) => game.OnMain(async () =>
    {
        var k = await game.Kill(request.Steamid, request.Robot.Length > 0 ? request.Robot : "heavy", request.Boss);
        return new KillReply { Ok = k.Ok, Reason = k.Reason, Dropped = k.Dropped, Tier = k.Tier, Source = k.Source, ItemIds = { k.Items }, KillSeq = k.Seq };
    });

    public override Task<ItemsReply> Pickup(ItemRequest request, ServerCallContext context) => game.OnMain(async () => I(await game.Pickup(request.Steamid, request.ItemId)));
    public override Task<ItemsReply> Die(PlayerRequest request, ServerCallContext context) => game.OnMain(async () => I(await game.Die(request.Steamid)));
    public override Task<ItemsReply> Backpack(PlayerRequest request, ServerCallContext context) => game.OnMain(async () => I(await game.Backpack(request.Steamid)));
    public override Task<ItemsReply> Reclaim(ItemRequest request, ServerCallContext context) => game.OnMain(async () => I(await game.Reclaim(request.Steamid, request.ItemId)));
    public override Task<ItemsReply> LostAndFound(PlayerRequest request, ServerCallContext context) => game.OnMain(async () => I(await game.LostAndFound(request.Steamid)));

    public override Task<PartyReply> Party(PartyRequest request, ServerCallContext context) => game.OnMain(async () =>
    {
        var (ok, reason, id) = await game.Party(request.Steamids);
        return new PartyReply { Ok = ok, Reason = reason, PartyId = id };
    });

    Task<TravelReply> Go(FakeGameServer.Trip trip, TravelRequest r) => game.OnMain(async () =>
    {
        var (ok, reason, state, instance) = await game.Travel(trip, r.Steamid, r.Depth, r.PartyId);
        return new TravelReply { Ok = ok, Reason = reason, State = state, InstanceId = instance };
    });

    public override Task<TravelReply> Descend(TravelRequest request, ServerCallContext context) => Go(FakeGameServer.Trip.Descend, request);
    public override Task<TravelReply> TownPortal(TravelRequest request, ServerCallContext context) => Go(FakeGameServer.Trip.TownPortal, request);
    public override Task<TravelReply> ReturnThroughPortal(TravelRequest request, ServerCallContext context) => Go(FakeGameServer.Trip.ReturnThroughPortal, request);

    public override Task<Reply> Crash(CrashRequest request, ServerCallContext context)
    {
        var code = request.ExitCode == 0 ? 1 : request.ExitCode;
        // Answer first, then die: the caller learns the crash was taken.
        _ = Task.Run(async () => { await Task.Delay(50); await game.CrashAsync(code); });
        return Task.FromResult(new Reply { Ok = true });
    }

    public override Task<Reply> Hang(Empty request, ServerCallContext context)
    {
        game.Hang(); // not through the main loop: it is the main loop that stops
        return Task.FromResult(new Reply { Ok = true });
    }

    public override Task<Reply> Slow(SlowRequest request, ServerCallContext context)
    {
        game.Slow(request.Ms);
        return Task.FromResult(new Reply { Ok = true });
    }
}
