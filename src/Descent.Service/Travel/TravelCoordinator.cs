using System.Collections.Concurrent;
using Descent.Service.Api;
using Descent.Service.Gateway;
using Descent.Service.Ledger;
using SourceSharp.Host.Abstractions;
using P = SourceSharp.Host.Proto;

namespace Descent.Service.Travel;

public enum TravelKind { Descend, StairsDown, TownPortal, ReturnThroughPortal, ReturnToCorpse }

/// <summary>What TravelApi asks for a trip (the coordinator, or a recording planner in the SDK's facts).</summary>
public interface ITripPlanner
{
    Task<P.TravelResponse> Go(InstanceRecord caller, string characterId, byte[] token, string? partyId, int depth, TravelKind kind, string requestId, CancellationToken ct);
}

/// <summary>
/// Travel (plan §6.5, §10), with D-H12's sticky levels. A trip picks the target instance
/// (a live one already running the party's level, one created from the leader's claim for the
/// depth, or one created for a freshly handed-out level), records the claim for every member,
/// and hops each member when the target is live: the gateway route flips, the source instance
/// gets PrepareHop, and HopReady answers with Retry. With no ready level the trip waits (the
/// pool links one at priority 0) and a tick retries the hand-out.
/// </summary>
public sealed class TravelCoordinator(
    IHostData data,
    IInstanceLifecycle lifecycle,
    IMapPool pool,
    IInstanceCommands commands,
    IGatewayControl gateway,
    ServiceOptions options,
    ILogger<TravelCoordinator> log) : IHopListener, ITripPlanner
{
    sealed record Member(string CharacterId, string SteamId, string SourceInstance);
    sealed record Trip(string TargetInstance, IReadOnlyList<Member> Members);

    readonly ConcurrentDictionary<string, Trip> _awaitingLive = new();   // target instance → trip
    readonly ConcurrentDictionary<string, Member> _prepared = new();     // hop id → member
    readonly ConcurrentDictionary<string, (int Depth, IReadOnlyList<Member> Members)> _awaitingLevel = new(); // requested instance → trip

    public Task<P.TravelResponse> Go(InstanceRecord caller, string characterId, byte[] token, string? partyId, int depth, TravelKind kind, string requestId, CancellationToken ct) =>
        Go(caller, characterId, token, partyId, depth, kind, ct);

    public async Task<P.TravelResponse> Go(InstanceRecord caller, string characterId, byte[] token, string? partyId, int depth, TravelKind kind, CancellationToken ct)
    {
        // 1. Who travels, and where to (read + claims in one transaction).
        var plan = await data.WriteAsync(async (tx, _) =>
        {
            var (leader, _) = await DescentLedger.Leased(tx, caller, characterId, token);
            var party = partyId is { Length: > 0 } ? await tx.Parties.Get(partyId) : await tx.Parties.ForCharacter(characterId);
            var members = new List<Member>();
            foreach (var id in party?.Members ?? [characterId])
            {
                var lease = await tx.Leases.Get(id);
                if (lease?.InstanceId != caller.Id) continue; // only members standing here travel
                var ch = await tx.Characters.Get(id);
                if (ch is not null) members.Add(new Member(id, ch.Account, caller.Id));
            }
            if (kind == TravelKind.TownPortal)
            {
                var hub = (await tx.Instances.NonTerminal()).FirstOrDefault(i => i.Kind == InstanceKind.Hub && i.State == InstanceState.Live)
                          ?? throw HostRefusal.Exhausted("no_hub", "no live hub");
                return (Target: (InstanceRecord?)hub, Hash: (string?)null, Members: members);
            }
            if (depth < 1 || depth > options.MapPool.Depths)
                throw new HostRefusal(RefusalCode.InvalidArgument, "bad_depth", $"depth {depth}");
            if (kind is TravelKind.Descend or TravelKind.StairsDown && depth > leader.ReachedDepth + 1)
                throw HostRefusal.Precondition("depth_locked", $"{characterId} has reached depth {leader.ReachedDepth}");
            var claim = await tx.LevelClaims.Get(characterId, depth);
            if (claim is null) return (Target: null, Hash: null, Members: members);
            foreach (var m in members) await tx.LevelClaims.Set(m.CharacterId, depth, claim.LevelHash);
            var running = (await tx.Instances.NonTerminal()).FirstOrDefault(i => i.LevelHash == claim.LevelHash && i.State is not InstanceState.Draining);
            return (Target: running, Hash: claim.LevelHash, Members: members);
        }, ct);

        if (plan.Members.Count == 0) throw HostRefusal.Precondition("not_in_party", "nobody to move");

        // 2. The target instance.
        InstanceRecord target;
        if (plan.Target is { } live) target = live;
        else
        {
            var requested = await lifecycle.RequestLevel(depth, partyId, plan.Hash, null, ct);
            if (requested is LevelRequestResult.AtCapacity cap)
                return new P.TravelResponse { State = P.TravelState.Waiting, EtaMs = 30_000, Reason = $"instance_cap: {cap.Running}/{cap.Max} levels running" };
            target = ((LevelRequestResult.Accepted)requested).Instance;
            if (plan.Hash is null && !await TryHandOut(target.Id, depth, plan.Members, ct))
            {
                _awaitingLevel[target.Id] = (depth, plan.Members);
                return new P.TravelResponse { State = P.TravelState.Waiting, InstanceId = target.Id, EtaMs = 10_000, Reason = "pool_empty: linking a level" };
            }
        }

        // 3. Hop when it is live.
        _awaitingLive[target.Id] = new Trip(target.Id, plan.Members);
        if (target.State == InstanceState.Live) await OnInstanceLive(target, ct);
        return new P.TravelResponse { State = P.TravelState.Started, InstanceId = target.Id };
    }

    async Task<bool> TryHandOut(string instanceId, int depth, IReadOnlyList<Member> members, CancellationToken ct)
    {
        var level = await data.WriteAsync(async (tx, _) =>
        {
            var l = await pool.HandOutAsync(tx, depth, instanceId);
            if (l is null) return null;
            foreach (var m in members) await tx.LevelClaims.Set(m.CharacterId, depth, l.Hash);
            return l;
        }, ct);
        if (level is null) { await pool.RequestFallbackAsync(depth, ct); return false; }
        await lifecycle.AssignLevel(instanceId, level.Hash, level.PackId, ct);
        return true;
    }

    /// <summary>The waiting trips' retry, driven by a tick.</summary>
    public async Task RetryWaiting(CancellationToken ct)
    {
        foreach (var (instanceId, (depth, members)) in _awaitingLevel.ToArray())
        {
            if (!await TryHandOut(instanceId, depth, members, ct)) continue;
            _awaitingLevel.TryRemove(instanceId, out _);
            _awaitingLive[instanceId] = new Trip(instanceId, members);
        }
    }

    /// <summary>The target is live: flip each member's route and ask the source to prepare the hop.</summary>
    public async Task OnInstanceLive(InstanceRecord target, CancellationToken ct)
    {
        if (!_awaitingLive.TryRemove(target.Id, out var trip)) return;
        var sessions = await data.ReadAsync(async (tx, _) =>
        {
            var byAccount = new Dictionary<string, SessionRecord>();
            foreach (var m in trip.Members)
                if ((await tx.Sessions.ForSteamId(m.SteamId)).FirstOrDefault() is { } s) byAccount[m.SteamId] = s;
            return byAccount;
        }, ct);
        foreach (var m in trip.Members)
        {
            if (target.PodIp is not null && sessions.TryGetValue(m.SteamId, out var s))
                await gateway.SetRoute(s.ClientAddr, GatewayControlClient.Backend(target, options), m.SteamId, holdUntilReady: false, ct);
            _prepared[InstanceStreams.HopId(target.Id, m.SteamId)] = m;
            if (!await commands.Send(m.SourceInstance, new InstanceCommand.PrepareHop(m.SteamId, target.Id), ct))
                log.LogWarning("PrepareHop for {SteamId} could not reach {Source}", m.SteamId, m.SourceInstance);
        }
    }

    /// <summary>The source checkpointed and released: tell the client to retry, which lands it on the new route.</summary>
    public async Task HopReady(InstanceRecord caller, string hopId, string steamId, CancellationToken ct)
    {
        if (!_prepared.TryRemove(hopId, out var m)) { log.LogWarning("HopReady for unknown hop {Hop}", hopId); return; }
        await commands.Send(m.SourceInstance, new InstanceCommand.Retry(steamId), ct);
    }

    public int Waiting => _awaitingLevel.Count + _awaitingLive.Count;
}

/// <summary>The trips waiting for a level retry on a tick; D-H12 sessions end after the idle time.</summary>
public sealed class TravelWorker(TravelCoordinator travel, IHostData data, IMapPool pool, ServiceOptions options, TimeProvider clock, ILogger<TravelWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
        var n = 0;
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await travel.RetryWaiting(stoppingToken);
                if (++n % 60 == 0) await EndIdleSessions(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "travel tick failed"); }
        }
    }

    /// <summary>
    /// D-H12: a character holding a lease keeps its claims fresh; one without a lease for
    /// Travel.SessionIdle loses them, and a level no claim and no running instance refers to
    /// is retired.
    /// </summary>
    public async Task<int> EndIdleSessions(CancellationToken ct)
    {
        var retire = await data.WriteAsync(async (tx, _) =>
        {
            var free = new List<string>();
            var now = tx.Now;
            foreach (var ch in await tx.LevelClaims.CharactersWithClaims())
            {
                if (await tx.Leases.Get(ch) is not null) { await tx.LevelClaims.Touch(ch); continue; }
                var claims = await tx.LevelClaims.ForCharacter(ch);
                if (claims.Count == 0 || now - claims.Max(c => c.LastUsed) < options.Travel.SessionIdle) continue;
                var hashes = await tx.LevelClaims.Drop(ch);
                await tx.Audit.Write("session.end", ch, null, new { levels = hashes });
                var running = (await tx.Instances.NonTerminal()).Select(i => i.LevelHash).ToHashSet();
                foreach (var h in hashes)
                    if (await tx.LevelClaims.ClaimsOn(h) == 0 && !running.Contains(h)) free.Add(h);
            }
            return free;
        }, ct);
        foreach (var hash in retire) await pool.RetireLevelAsync(hash, ct);
        return retire.Count;
    }
}
