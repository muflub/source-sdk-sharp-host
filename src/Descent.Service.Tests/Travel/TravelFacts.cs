using Descent.Service.Gateway;
using Descent.Service.Tests.Gateway;
using Descent.Service.Tests.Ledger;
using Descent.Service.Travel;
using Microsoft.Extensions.Logging.Abstractions;
using SourceSharp.Host.Abstractions;
using P = SourceSharp.Host.Proto;

namespace Descent.Service.Tests.Travel;

/// <summary>Plan §6.5 / §10 and D-H12: travel and sticky levels, against the real stores.</summary>
public class TravelFacts
{
    sealed class FakeLifecycle(LedgerHarness h) : IInstanceLifecycle
    {
        public int Max { get; set; } = 16;
        public List<string> Requests { get; } = [];
        int _n;
        public async Task<LevelRequestResult> RequestLevel(int depth, string? partyId, string? levelHash, long? packVersion, CancellationToken ct = default)
        {
            if (Requests.Count >= Max) return new LevelRequestResult.AtCapacity(Requests.Count, Max);
            Requests.Add(levelHash ?? "(none)");
            var id = $"lvl-new-{++_n}";
            var row = await h.AddInstance(id, InstanceKind.Level, depth);
            row = await h.W(tx => tx.Instances.Update(id, r => r with
            {
                State = levelHash is null ? InstanceState.Requested : InstanceState.LevelReady, LevelHash = levelHash, PodIp = $"10.42.1.{_n}",
            }));
            return new LevelRequestResult.Accepted(row);
        }
        public Task<InstanceRecord> AssignLevel(string instanceId, string levelHash, long packVersion, CancellationToken ct = default) =>
            h.W(tx => tx.Instances.Update(instanceId, r => r with { State = InstanceState.LevelReady, LevelHash = levelHash }));
        public Task<InstanceRecord?> Authenticate(string instanceId, string token, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<InstanceRecord> OnBooting(string instanceId, string rulesSha256, string? modImageDigest, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<MapReadyResult> OnMapReady(string instanceId, int port, CancellationToken ct = default) => throw new NotImplementedException();
        public Task OnStreamOpened(string instanceId, CancellationToken ct = default) => Task.CompletedTask;
        public Task OnHeartbeat(string instanceId, int players, CancellationToken ct = default) => Task.CompletedTask;
        public Task OnStreamClosed(string instanceId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<InstanceRecord> Drain(string instanceId, string reason, string actor, CancellationToken ct = default) => throw new NotImplementedException();
        public Task Kick(string instanceId, string steamId, string reason, string actor, CancellationToken ct = default) => Task.CompletedTask;
        public Task Exec(string instanceId, string command, string actor, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResumeHubs(string actor, CancellationToken ct = default) => Task.CompletedTask;
    }

    sealed class FakePool(LedgerHarness h) : IMapPool
    {
        public List<string> Fallbacks { get; } = [];
        public List<string> Retired { get; } = [];
        public PoolStatus Status => PoolStatus.Starting;
        public Task<PoolRequestResult> RequestLevelAsync(int depth, string instanceId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<LevelRecord?> HandOutAsync(IWriteTx tx, int depth, string instanceId) => tx.Levels.HandOut(depth, 1, instanceId);
        public Task RequestFallbackAsync(int depth, CancellationToken ct = default) { Fallbacks.Add($"depth {depth}"); return Task.CompletedTask; }
        public Task RetireLevelAsync(string hash, CancellationToken ct = default) { Retired.Add(hash); return Task.CompletedTask; }
        public Task AddReady(string hash, int depth) => h.W(tx => tx.Levels.Add(new LevelRecord(hash, 1, depth, "t", 1, 0, LevelState.Ready, h.D.Clock.GetUtcNow(), null, 1, null, null)));
    }

    sealed class RecordingCommands : IInstanceCommands
    {
        public List<string> Sent { get; } = [];
        public Task<bool> Send(string instanceId, InstanceCommand command, CancellationToken ct = default)
        { Sent.Add($"{instanceId} {command}"); return Task.FromResult(true); }
    }

    sealed record World(LedgerHarness H, TravelCoordinator Travel, FakeLifecycle Life, FakePool Pool, RecordingCommands Commands, RecordingGatewayControl Gateway, string Leader, string Mate)
    {
        public Task<P.TravelResponse> Descend(int depth, string? who = null) =>
            Travel.Go(H.Hub, who ?? Leader, H.Token(who ?? Leader), null, depth, TravelKind.Descend, default);
    }

    static async Task<World> Setup()
    {
        var h = await LedgerHarness.Create();
        var life = new FakeLifecycle(h);
        var pool = new FakePool(h);
        var commands = new RecordingCommands();
        var gateway = new RecordingGatewayControl();
        var travel = new TravelCoordinator(h.D.Data, life, pool, commands, gateway, h.Options, NullLogger<TravelCoordinator>.Instance);
        var leader = await h.Character("Ann", account: "765");
        var mate = await h.Character("Bo", account: "766");
        await h.Lease(leader, h.Hub);
        await h.Lease(mate, h.Hub);
        await h.W(tx => tx.Characters.Update(leader, 1, c => c with { ReachedDepth = 5 }));
        var party = await h.W(tx => tx.Parties.Create(leader));
        await h.W(tx => tx.Parties.Invite(party.Id, mate));
        await h.W(tx => tx.Parties.Join(party.Id, mate));
        return new World(h, travel, life, pool, commands, gateway, leader, mate);
    }

    [Fact]
    public async Task A_first_descent_hands_out_a_level_and_claims_it_for_every_member()
    {
        var w = await Setup();
        await using var _ = w.H;
        await w.Pool.AddReady("L1", 1);
        var r = await w.Descend(1);
        Assert.Equal(P.TravelState.Started, r.State);
        Assert.Equal(("L1", "L1"), ((await w.H.R(tx => tx.LevelClaims.Get(w.Leader, 1)))!.LevelHash, (await w.H.R(tx => tx.LevelClaims.Get(w.Mate, 1)))!.LevelHash));
        Assert.Equal("L1", (await w.H.R(tx => tx.Instances.Get(r.InstanceId)))!.LevelHash);
    }

    [Fact]
    public async Task Returning_to_a_depth_in_the_same_session_gets_the_same_level()
    {
        var w = await Setup();
        await using var _ = w.H;
        await w.Pool.AddReady("L1", 1);
        await w.Pool.AddReady("L2", 1);
        var first = await w.Descend(1);
        await w.H.W(tx => tx.Instances.Update(first.InstanceId, i => i with { State = InstanceState.Reaped }));
        var again = await w.Descend(1);
        Assert.Equal("L1", (await w.H.R(tx => tx.Instances.Get(again.InstanceId)))!.LevelHash);
        Assert.Equal(["(none)", "L1"], w.Life.Requests);
        Assert.Equal(1, await w.H.R(tx => tx.Levels.Count(1, null, LevelState.Ready))); // L2 was never taken
    }

    [Fact]
    public async Task A_descent_joins_the_instance_already_running_the_claimed_level()
    {
        var w = await Setup();
        await using var _ = w.H;
        await w.Pool.AddReady("L1", 1);
        var first = await w.Descend(1);
        var again = await w.Descend(1);
        Assert.Equal(first.InstanceId, again.InstanceId);
        Assert.Single(w.Life.Requests);
    }

    [Fact]
    public async Task Another_player_gets_a_different_level_for_the_same_depth()
    {
        var w = await Setup();
        await using var _ = w.H;
        await w.Pool.AddReady("L1", 1);
        await w.Pool.AddReady("L2", 1);
        await w.Descend(1);
        var solo = await w.H.Character("Cy", account: "767");
        await w.H.Lease(solo, w.H.Hub);
        var r = await w.Travel.Go(w.H.Hub, solo, w.H.Token(solo), null, 1, TravelKind.Descend, default);
        Assert.Equal("L2", (await w.H.R(tx => tx.Instances.Get(r.InstanceId)))!.LevelHash);
    }

    [Fact]
    public async Task An_empty_pool_waits_asks_for_a_fallback_and_a_retry_assigns_the_level()
    {
        var w = await Setup();
        await using var _ = w.H;
        var r = await w.Descend(2);
        Assert.Equal(P.TravelState.Waiting, r.State);
        Assert.Equal(["depth 2"], w.Pool.Fallbacks);
        await w.Pool.AddReady("L9", 2);
        await w.Travel.RetryWaiting(default);
        Assert.Equal("L9", (await w.H.R(tx => tx.Instances.Get(r.InstanceId)))!.LevelHash);
        Assert.Equal("L9", (await w.H.R(tx => tx.LevelClaims.Get(w.Leader, 2)))!.LevelHash);
    }

    [Fact]
    public async Task At_the_instance_cap_the_trip_waits_with_the_reason()
    {
        var w = await Setup();
        await using var _ = w.H;
        w.Life.Max = 0;
        var r = await w.Descend(1);
        Assert.Equal(P.TravelState.Waiting, r.State);
        Assert.StartsWith("instance_cap", r.Reason);
    }

    [Fact]
    public async Task A_depth_beyond_the_one_reached_is_refused()
    {
        var w = await Setup();
        await using var _ = w.H;
        var e = await Assert.ThrowsAsync<HostRefusal>(() => w.Descend(7));
        Assert.Equal("depth_locked", e.Reason);
    }

    [Fact]
    public async Task When_the_target_goes_live_each_member_is_routed_then_asked_to_prepare_the_hop()
    {
        var w = await Setup();
        await using var _ = w.H;
        await w.Pool.AddReady("L1", 1);
        var now = w.H.D.Clock.GetUtcNow();
        await w.H.W(tx => tx.Sessions.Upsert(new SessionRecord("s1", "1.2.3.4:27005", "765", w.H.Hub.Id, "10.42.0.9:5010", "127.1.0.1:29005", "open", now, now, now)));
        var r = await w.Descend(1);
        var target = await w.H.W(tx => tx.Instances.Update(r.InstanceId, i => i with { State = InstanceState.Live }));
        await w.Travel.OnInstanceLive(target, default);
        Assert.Contains($"route 1.2.3.4:27005 -> {target.PodIp}:5010 steam=765 hold=False", w.Gateway.Calls);
        Assert.Equal(2, w.Commands.Sent.Count(c => c.Contains("PrepareHop")));
    }

    [Fact]
    public async Task HopReady_answers_with_retry_to_the_source()
    {
        var w = await Setup();
        await using var _ = w.H;
        await w.Pool.AddReady("L1", 1);
        var r = await w.Descend(1);
        var target = await w.H.W(tx => tx.Instances.Update(r.InstanceId, i => i with { State = InstanceState.Live }));
        await w.Travel.OnInstanceLive(target, default);
        await w.Travel.HopReady(w.H.Hub, Descent.Service.Api.InstanceStreams.HopId(target.Id, "765"), "765", default);
        Assert.Contains($"{w.H.Hub.Id} Retry {{ SteamId = 765 }}", w.Commands.Sent);
    }

    [Fact]
    public async Task A_session_idle_past_the_limit_drops_its_claims_and_retires_the_level()
    {
        var w = await Setup();
        await using var _ = w.H;
        await w.Pool.AddReady("L1", 1);
        var r = await w.Descend(1);
        await w.H.W(tx => tx.Instances.Update(r.InstanceId, i => i with { State = InstanceState.Reaped }));
        foreach (var c in new[] { w.Leader, w.Mate }) await w.H.W(tx => tx.Leases.Release(c, null));
        var worker = new TravelWorker(w.Travel, w.H.D.Data, w.Pool, w.H.Options, w.H.D.Clock, NullLogger<TravelWorker>.Instance);
        Assert.Equal(0, await worker.EndIdleSessions(default));
        w.H.D.Clock.Advance(w.H.Options.Travel.SessionIdle + TimeSpan.FromMinutes(1));
        Assert.Equal(1, await worker.EndIdleSessions(default));
        Assert.Equal(["L1"], w.Pool.Retired);
        Assert.Empty(await w.H.R(tx => tx.LevelClaims.ForCharacter(w.Leader)));
    }

    [Fact]
    public async Task A_leased_character_keeps_its_claims_however_long_it_plays()
    {
        var w = await Setup();
        await using var _ = w.H;
        await w.Pool.AddReady("L1", 1);
        await w.Descend(1);
        var worker = new TravelWorker(w.Travel, w.H.D.Data, w.Pool, w.H.Options, w.H.D.Clock, NullLogger<TravelWorker>.Instance);
        for (var i = 0; i < 3; i++)
        {
            w.H.D.Clock.Advance(w.H.Options.Travel.SessionIdle);
            await worker.EndIdleSessions(default);
        }
        Assert.NotNull(await w.H.R(tx => tx.LevelClaims.Get(w.Leader, 1)));
    }
}
