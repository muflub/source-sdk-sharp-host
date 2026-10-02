using TF2.Descent.Service.Gateway;
using TF2.Descent.Service.Tests.Gateway;
using TF2.Descent.Service.Tests.Ledger;
using TF2.Descent.Service.Travel;
using Microsoft.Extensions.Logging.Abstractions;
using SourceSharp.Host.Abstractions;
using P = SourceSharp.Host.Proto;

namespace TF2.Descent.Service.Tests.Travel;

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
            if (Requests.Count - Drained.Count >= Max) return new LevelRequestResult.AtCapacity(Requests.Count - Drained.Count, Max);
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
        /// <summary>A drained level frees its slot at once here (the manager frees it at the reap).</summary>
        public List<string> Drained { get; } = [];
        public async Task<InstanceRecord> Drain(string instanceId, string reason, string actor, CancellationToken ct = default)
        {
            Drained.Add($"{instanceId} {reason}");
            return await h.W(tx => tx.Instances.Update(instanceId, r => r with { State = InstanceState.Draining, Reason = reason }));
        }
        public List<string> Left { get; } = [];
        public Task Leave(string instanceId, string reason, CancellationToken ct = default) { Left.Add($"{instanceId} {reason}"); return Task.CompletedTask; }
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

    /// <summary>A local host's connection records: every instance in <see cref="Pods"/> has one (null = all do).</summary>
    sealed class FakeConnections : SourceSharp.Host.Instances.ILocalConnections
    {
        public HashSet<string>? Pods { get; set; }
        public string? For(string instanceId) => Pods is null || Pods.Contains(instanceId)
            ? $$"""{ "service": "http://127.0.0.1:5001", "instanceId": "{{instanceId}}", "token": "t", "map": "map-of-{{instanceId}}", "port": 27015 }"""
            : null;
    }

    sealed record World(LedgerHarness H, TravelCoordinator Travel, FakeLifecycle Life, FakePool Pool, RecordingCommands Commands, RecordingGatewayControl Gateway, string Leader, string Mate)
    {
        public Task<P.TravelResponse> Descend(int depth, string? who = null) =>
            Travel.Go(H.Hub, who ?? Leader, H.Token(who ?? Leader), null, depth, TravelKind.Descend, default);
    }

    static async Task<World> Setup(FakeConnections? sameProcess = null, SourceSharp.Host.MapPool.LevelStorage? levels = null)
    {
        var h = await LedgerHarness.Create();
        var life = new FakeLifecycle(h);
        var pool = new FakePool(h);
        var commands = new RecordingCommands();
        var gateway = new RecordingGatewayControl();
        var travel = new TravelCoordinator(h.D.Data, life, pool, commands, gateway, h.Options, NullLogger<TravelCoordinator>.Instance,
            sameProcess is null ? null : new SameProcessTravel(sameProcess), levels);
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
        await w.Travel.HopReady(w.H.Hub, TF2.Descent.Service.Api.InstanceStreams.HopId(target.Id, "765"), "765", default);
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

    // ------------------------------------------------------------------ D-H13 same-process travel

    static IEnumerable<string> Hops(World w) => w.Commands.Sent.Where(c => c.Contains("PrepareHop"));

    [Fact]
    public async Task An_ordinary_trip_hops_nobody_before_the_target_is_live()
    {
        var w = await Setup();
        await using var _ = w.H;
        await w.Pool.AddReady("L1", 1);
        await w.Descend(1);
        Assert.Empty(Hops(w));
    }

    [Fact]
    public async Task A_same_process_trip_hops_every_member_at_once_with_the_targets_map_and_record()
    {
        var w = await Setup(new FakeConnections());
        await using var _ = w.H;
        await w.Pool.AddReady("L1", 1);
        var r = await w.Descend(1);
        Assert.Equal(P.TravelState.Started, r.State);
        var hops = Hops(w).ToList();
        Assert.Equal(2, hops.Count);
        Assert.All(hops, c => Assert.Contains($"Map = map-of-{r.InstanceId}", c));
        Assert.All(hops, c => Assert.Contains($"\"instanceId\": \"{r.InstanceId}\"", c));
        Assert.All(hops, c => Assert.StartsWith(w.H.Hub.Id + " ", c));
    }

    [Fact]
    public async Task A_same_process_trip_waits_for_the_targets_pod_then_hops()
    {
        var connections = new FakeConnections { Pods = [] };
        var w = await Setup(connections);
        await using var _ = w.H;
        await w.Pool.AddReady("L1", 1);
        var r = await w.Descend(1);
        Assert.Empty(Hops(w));
        connections.Pods.Add(r.InstanceId);
        await w.Travel.OnInstanceBooting((await w.H.R(tx => tx.Instances.Get(r.InstanceId)))!, default);
        Assert.Equal(2, Hops(w).Count());
    }

    [Fact]
    public async Task A_same_process_HopReady_leaves_the_source_and_sends_no_retry()
    {
        var w = await Setup(new FakeConnections());
        await using var _ = w.H;
        await w.Pool.AddReady("L1", 1);
        var r = await w.Descend(1);
        await w.Travel.HopReady(w.H.Hub, TF2.Descent.Service.Api.InstanceStreams.HopId(r.InstanceId, "765"), "765", default);
        Assert.Equal([$"{w.H.Hub.Id} same_process_hop"], w.Life.Left);
        Assert.DoesNotContain(w.Commands.Sent, c => c.Contains("Retry"));
    }

    [Fact]
    public async Task An_ordinary_HopReady_leaves_nothing()
    {
        var w = await Setup();
        await using var _ = w.H;
        await w.Pool.AddReady("L1", 1);
        var r = await w.Descend(1);
        var target = await w.H.W(tx => tx.Instances.Update(r.InstanceId, i => i with { State = InstanceState.Live }));
        await w.Travel.OnInstanceLive(target, default);
        await w.Travel.HopReady(w.H.Hub, TF2.Descent.Service.Api.InstanceStreams.HopId(target.Id, "765"), "765", default);
        Assert.Empty(w.Life.Left);
    }

    /// <summary>The hub this game left is draining; its replacement has a pod and no game yet.</summary>
    static async Task<InstanceRecord> ReplacedHub(World w)
    {
        await w.H.W(tx => tx.Instances.Update(w.H.Hub.Id, i => i with { State = InstanceState.Draining }));
        var next = await w.H.AddInstance("hub-2", InstanceKind.Hub, 0);
        return await w.H.W(tx => tx.Instances.Update(next.Id, i => i with { State = InstanceState.Booting }));
    }

    [Fact]
    public async Task A_same_process_town_portal_goes_to_the_hub_that_has_no_game_yet()
    {
        var w = await Setup(new FakeConnections());
        await using var _ = w.H;
        var hub = await ReplacedHub(w);
        var r = await w.Travel.Go(w.H.Hub, w.Leader, w.H.Token(w.Leader), null, 0, TravelKind.TownPortal, default);
        Assert.Equal((P.TravelState.Started, hub.Id), (r.State, r.InstanceId));
        Assert.Contains(Hops(w), c => c.Contains("Map = map-of-hub-2"));
    }

    [Fact]
    public async Task An_ordinary_town_portal_needs_a_live_hub()
    {
        var w = await Setup();
        await using var _ = w.H;
        await ReplacedHub(w);
        var e = await Assert.ThrowsAsync<HostRefusal>(() => w.Travel.Go(w.H.Hub, w.Leader, w.H.Token(w.Leader), null, 0, TravelKind.TownPortal, default));
        Assert.Equal("no_hub", e.Reason);
    }

    // ------------------------------------------------------------------ D-H14 prewarm

    /// <summary>The party descends to depth 1 and its level goes live: the arrival D-H14 prewarms from.</summary>
    static async Task<InstanceRecord> ArriveAtDepth1(World w)
    {
        var r = await w.Descend(1);
        var level = await w.H.W(tx => tx.Instances.Update(r.InstanceId, i => i with { State = InstanceState.Live }));
        await w.Travel.OnInstanceLive(level, default);
        await w.Travel.RetryWaiting(default); // the tick that runs the queued prewarm
        return level;
    }

    static IEnumerable<string> NextLevels(World w) => w.Commands.Sent.Where(c => c.Contains("NextLevel"));

    [Fact]
    public async Task Arriving_on_a_level_prewarms_the_next_depth_and_tells_its_map()
    {
        var w = await Setup();
        await using var _ = w.H;
        w.H.Options.Instances.FastDlUrl = "http://maps.example:5004/";
        await w.Pool.AddReady("L1", 1);
        await w.Pool.AddReady("L2", 2);
        var level = await ArriveAtDepth1(w);
        Assert.Equal(["(none)", "(none)"], w.Life.Requests); // depth 1, then the prewarm of depth 2
        Assert.Equal([$"{level.Id} NextLevel {{ Depth = 2, Map = tf2_descent-2-L2, DownloadUrl = http://maps.example:5004/, MapSha256 =  }}"], NextLevels(w));
        Assert.Equal(("L2", "L2"), ((await w.H.R(tx => tx.LevelClaims.Get(w.Leader, 2)))!.LevelHash, (await w.H.R(tx => tx.LevelClaims.Get(w.Mate, 2)))!.LevelHash));
    }

    [Fact]
    public async Task The_prewarm_is_linked_to_the_level_its_party_arrived_on()
    {
        var w = await Setup();
        await using var _ = w.H;
        await w.Pool.AddReady("L1", 1);
        await w.Pool.AddReady("L2", 2);
        var level = await ArriveAtDepth1(w);
        var prewarmed = (await w.H.R(tx => tx.Instances.NonTerminal())).Single(i => i.Depth == 2);
        var link = Assert.Single(await w.H.R(tx => tx.Audit.List(new AuditQuery(Action: TravelCoordinator.PrewarmedAction, Target: prewarmed.Id))));
        Assert.Contains($"\"parent\":\"{level.Id}\"", link.AfterJson);
    }

    [Fact]
    public async Task NextLevel_carries_the_sha256_of_the_stored_bsp()
    {
        var storage = new SourceSharp.Host.MapPool.LevelStorage(new MapPoolOptions { MapsPath = Path.Combine(Path.GetTempPath(), $"sourcesharp-bsp-{Guid.NewGuid():N}") });
        Directory.CreateDirectory(storage.LevelsDirectory);
        var bsp = "VBSP fake level body"u8.ToArray();
        await File.WriteAllBytesAsync(storage.LevelFile("tf2_descent-2-L2", ".bsp"), bsp);
        var w = await Setup(levels: storage);
        await using var _ = w.H;
        await w.Pool.AddReady("L1", 1);
        await w.Pool.AddReady("L2", 2);
        await ArriveAtDepth1(w);
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bsp));
        Assert.Contains(NextLevels(w), c => c.EndsWith($"MapSha256 = {sha} }}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_prewarm_hops_nobody()
    {
        var w = await Setup();
        await using var _ = w.H;
        await w.Pool.AddReady("L1", 1);
        await w.Pool.AddReady("L2", 2);
        var level = await ArriveAtDepth1(w);
        var prewarmed = (await w.H.R(tx => tx.Instances.NonTerminal())).Single(i => i.Depth == 2);
        await w.Travel.OnInstanceLive(await w.H.W(tx => tx.Instances.Update(prewarmed.Id, i => i with { State = InstanceState.Live })), default);
        Assert.DoesNotContain(w.Commands.Sent, c => c.Contains("PrepareHop") && c.Contains(prewarmed.Id));
    }

    [Fact]
    public async Task A_later_descent_lands_on_the_prewarmed_level()
    {
        var w = await Setup();
        await using var _ = w.H;
        await w.Pool.AddReady("L1", 1);
        await w.Pool.AddReady("L2", 2);
        await ArriveAtDepth1(w);
        var prewarmed = (await w.H.R(tx => tx.Instances.NonTerminal())).Single(i => i.Depth == 2);
        var r = await w.Descend(2);
        Assert.Equal(prewarmed.Id, r.InstanceId);
        Assert.Equal(2, w.Life.Requests.Count); // no third instance
    }

    [Fact]
    public async Task The_last_depth_prewarms_nothing()
    {
        var w = await Setup();
        await using var _ = w.H;
        w.H.Options.MapPool.Depths = 1;
        await w.Pool.AddReady("L1", 1);
        var level = await ArriveAtDepth1(w);
        Assert.Single(w.Life.Requests);
        Assert.Empty(NextLevels(w));
    }

    [Fact]
    public async Task A_prewarm_is_not_started_at_the_cap()
    {
        var w = await Setup();
        await using var _ = w.H;
        w.H.Options.Instances.MaxLevelPods = 1; // depth 1 fills it
        await w.Pool.AddReady("L1", 1);
        await w.Pool.AddReady("L2", 2);
        await ArriveAtDepth1(w);
        Assert.Single(w.Life.Requests);
        Assert.Empty(NextLevels(w));
    }

    /// <summary>Ann's party is on depth 1 with depth 2 prewarmed; the cap (2) is full. Cy, solo on the hub, descends.</summary>
    static async Task<(World W, InstanceRecord Prewarm, string Cy)> FullWithAPrewarm()
    {
        var w = await Setup();
        w.Life.Max = 2;
        await w.Pool.AddReady("L1", 1);
        await w.Pool.AddReady("L2", 2);
        await w.Pool.AddReady("L1-b", 1);
        await ArriveAtDepth1(w);
        var prewarm = (await w.H.R(tx => tx.Instances.NonTerminal())).Single(i => i.Depth == 2);
        var cy = await w.H.Character("Cy", account: "767");
        await w.H.Lease(cy, w.H.Hub);
        return (w, prewarm, cy);
    }

    [Fact]
    public async Task At_the_cap_a_descent_drains_an_unentered_prewarm_and_waits()
    {
        var (w, prewarm, cy) = await FullWithAPrewarm();
        await using var _ = w.H;
        var r = await w.Travel.Go(w.H.Hub, cy, w.H.Token(cy), null, 1, TravelKind.Descend, default);
        Assert.Equal(P.TravelState.Waiting, r.State);
        Assert.Contains("yields", r.Reason);
        Assert.Equal([$"{prewarm.Id} prewarm_yield"], w.Life.Drained);
    }

    [Fact]
    public async Task The_waiting_descent_gets_its_level_once_the_slot_is_free()
    {
        var (w, prewarm, cy) = await FullWithAPrewarm();
        await using var _ = w.H;
        await w.Travel.Go(w.H.Hub, cy, w.H.Token(cy), null, 1, TravelKind.Descend, default);
        await w.Travel.RetryWaiting(default); // the tick: the yielded slot is free
        Assert.Equal(3, w.Life.Requests.Count); // depth 1, the prewarm, then Cy's level
        Assert.Equal("L1-b", (await w.H.R(tx => tx.LevelClaims.Get(cy, 1)))!.LevelHash);
    }

    [Fact]
    public async Task A_prewarm_someone_entered_does_not_yield()
    {
        var (w, prewarm, cy) = await FullWithAPrewarm();
        await using var _ = w.H;
        await w.H.W(async tx => { await tx.Audit.Write(SourceSharp.Host.Instances.InstanceManager.OccupiedAction, prewarm.Id, null, new { players = 1 }); return true; });
        var r = await w.Travel.Go(w.H.Hub, cy, w.H.Token(cy), null, 1, TravelKind.Descend, default);
        Assert.StartsWith("instance_cap: 2/2", r.Reason);
        Assert.Empty(w.Life.Drained);
    }

    [Fact]
    public async Task With_an_empty_pool_the_prewarm_waits_then_tells_the_map()
    {
        var w = await Setup();
        await using var _ = w.H;
        await w.Pool.AddReady("L1", 1);
        var level = await ArriveAtDepth1(w);
        Assert.Equal(2, w.Life.Requests.Count); // the depth-2 instance exists, waiting for a level
        Assert.Empty(NextLevels(w));
        await w.Pool.AddReady("L2", 2);
        await w.Travel.RetryWaiting(default);
        Assert.Equal([$"{level.Id} NextLevel {{ Depth = 2, Map = tf2_descent-2-L2, DownloadUrl = , MapSha256 =  }}"], NextLevels(w));
        var prewarmed = (await w.H.R(tx => tx.Instances.NonTerminal())).Single(i => i.Depth == 2);
        Assert.DoesNotContain(w.Commands.Sent, c => c.Contains("PrepareHop") && c.Contains(prewarmed.Id));
    }

    [Fact]
    public async Task Same_process_travel_prewarms_nothing()
    {
        var w = await Setup(new FakeConnections { Pods = [] });
        await using var _ = w.H;
        await w.Pool.AddReady("L1", 1);
        await w.Pool.AddReady("L2", 2);
        var r = await w.Descend(1);
        var level = await w.H.W(tx => tx.Instances.Update(r.InstanceId, i => i with { State = InstanceState.Live }));
        await w.Travel.OnInstanceLive(level, default);
        await w.Travel.RetryWaiting(default);
        Assert.Single(w.Life.Requests);
    }

    // ------------------------------------------------------------------ D-H16 death

    static IEnumerable<string> HopsFrom(World w, string instance) => w.Commands.Sent.Where(c => c.StartsWith(instance + " PrepareHop", StringComparison.Ordinal));

    [Fact]
    public async Task A_character_who_dies_is_sent_to_town_alone()
    {
        var w = await Setup();
        await using var _ = w.H;
        await w.H.Lease(w.Leader, w.H.Level);
        await w.H.Lease(w.Mate, w.H.Level);
        await w.Travel.RespawnInTown(w.H.Level, w.Leader, default);
        Assert.Equal([$"{w.H.Level.Id} PrepareHop {{ SteamId = 765, TargetInstanceId = {w.H.Hub.Id}, Map = , LocalConnection =  }}"], HopsFrom(w, w.H.Level.Id));
    }

    [Fact]
    public async Task A_retried_death_sends_no_second_hop()
    {
        var w = await Setup();
        await using var _ = w.H;
        await w.H.Lease(w.Leader, w.H.Level);
        await w.Travel.RespawnInTown(w.H.Level, w.Leader, default);
        await w.Travel.RespawnInTown(w.H.Level, w.Leader, default);
        Assert.Single(HopsFrom(w, w.H.Level.Id));
    }

    [Fact]
    public async Task A_death_reported_by_a_level_that_does_not_hold_the_lease_moves_nobody()
    {
        var w = await Setup();
        await using var _ = w.H; // the leader is leased on the hub
        await w.Travel.RespawnInTown(w.H.Level, w.Leader, default);
        Assert.Empty(HopsFrom(w, w.H.Level.Id));
    }

    static Task Died(World w, string ch, int depth, string level) => w.H.W(async tx =>
    {
        await tx.Audit.Write(TF2.Descent.Service.Ledger.DescentLedger.DeathAction, ch, null, new { instance = "lvl-x", depth, level, items = 0, australium = 0 });
        return true;
    });

    [Fact]
    public async Task Return_to_corpse_goes_to_the_level_they_died_on_whatever_depth_is_asked()
    {
        var w = await Setup();
        await using var _ = w.H;
        await w.Pool.AddReady("L3", 3);
        await Died(w, w.Leader, 3, "L3");
        var r = await w.Travel.Go(w.H.Hub, w.Leader, w.H.Token(w.Leader), null, 1, TravelKind.ReturnToCorpse, default);
        Assert.Equal(P.TravelState.Started, r.State);
        Assert.Equal(("L3", 3), ((await w.H.R(tx => tx.Instances.Get(r.InstanceId)))!.LevelHash, (await w.H.R(tx => tx.Instances.Get(r.InstanceId)))!.Depth));
        Assert.Equal("L3", (await w.H.R(tx => tx.LevelClaims.Get(w.Leader, 3)))!.LevelHash);
        Assert.Null(await w.H.R(tx => tx.LevelClaims.Get(w.Mate, 3))); // the character alone
    }

    [Fact]
    public async Task Return_to_corpse_joins_the_level_while_it_still_runs()
    {
        var w = await Setup();
        await using var _ = w.H;
        var running = await w.H.W(tx => tx.Instances.Update(w.H.Level.Id, i => i with { LevelHash = "L3", Depth = 3 }));
        await Died(w, w.Leader, 3, "L3");
        var r = await w.Travel.Go(w.H.Hub, w.Leader, w.H.Token(w.Leader), null, 3, TravelKind.ReturnToCorpse, default);
        Assert.Equal(running.Id, r.InstanceId);
        Assert.Empty(w.Life.Requests);
    }

    [Fact]
    public async Task Return_to_corpse_without_a_death_is_refused()
    {
        var w = await Setup();
        await using var _ = w.H;
        var e = await Assert.ThrowsAsync<HostRefusal>(() => w.Travel.Go(w.H.Hub, w.Leader, w.H.Token(w.Leader), null, 3, TravelKind.ReturnToCorpse, default));
        Assert.Equal("no_corpse_level", e.Reason);
    }
}
