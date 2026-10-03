using System.Collections.Concurrent;
using System.Text.Json;
using TF2.Descent.Service.Api;
using TF2.Descent.Service.Gateway;
using TF2.Descent.Service.Ledger;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Instances;
using SourceSharp.Host.MapPool;
using P = SourceSharp.Host.Proto;

namespace TF2.Descent.Service.Travel;

public enum TravelKind { Descend, StairsDown, TownPortal, ReturnThroughPortal, ReturnToCorpse }

/// <summary>What TravelApi asks for a trip (the coordinator, or a recording planner in the SDK's facts).</summary>
public interface ITripPlanner
{
    Task<P.TravelResponse> Go(InstanceRecord caller, string characterId, byte[] token, string? partyId, int depth, TravelKind kind, string requestId, CancellationToken ct);

    /// <summary>D-H16: from the hub, the caller alone, to <paramref name="anchorOwner"/>'s teleporter (the caller or a party member).</summary>
    Task<P.TravelResponse> ToAnchor(InstanceRecord caller, string characterId, byte[] token, string anchorOwner, string requestId, CancellationToken ct);
}

/// <summary>
/// Local same-process travel (D-H13), on when <see cref="Connections"/> is set: the requesting
/// game serves the target, so the hop is sent as soon as the target has a pod (its game could
/// never go live first: it is this process), with the target's map and connection record.
/// </summary>
public sealed record SameProcessTravel(ILocalConnections? Connections)
{
    public bool On => Connections is not null;
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
    ILogger<TravelCoordinator> log,
    SameProcessTravel? sameProcess = null,
    LevelStorage? levels = null) : IHopListener, ITripPlanner
{
    readonly SameProcessTravel _same = sameProcess ?? new SameProcessTravel(null);
    readonly ConcurrentDictionary<string, bool> _sameProcessHops = new(); // hop ids sent as same-process
    // D-H14: levels whose party just arrived, waiting for the tick to prewarm the next one down
    // (OnInstanceLive runs under the instance manager's lock, which RequestLevel takes).
    readonly ConcurrentQueue<(InstanceRecord Current, IReadOnlyList<Member> Members)> _prewarm = new();
    // Prewarmed instances still waiting for the pool to hand out a level → (depth, members, the level to tell).
    readonly ConcurrentDictionary<string, (int Depth, IReadOnlyList<Member> Members, string Current)> _prewarmAwaiting = new();

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
                var hubs = (await tx.Instances.NonTerminal()).Where(i => i.Kind == InstanceKind.Hub).ToList();
                // Same-process: the hub this game left has been replaced by one with no game yet; this game becomes it.
                var hub = hubs.FirstOrDefault(i => i.State == InstanceState.Live)
                          ?? (_same.On ? hubs.FirstOrDefault(i => i.State == InstanceState.Booting && i.Id != caller.Id) : null)
                          ?? throw HostRefusal.Exhausted("no_hub", "no live hub");
                return (Target: (InstanceRecord?)hub, Hash: (string?)null, Members: members);
            }
            if (kind == TravelKind.ReturnToCorpse)
            {
                // D-H16: back to the level they died on, whatever depth was asked; the character alone.
                var death = (await tx.Audit.List(new AuditQuery(Action: DescentLedger.DeathAction, Target: characterId, Take: 1))).FirstOrDefault();
                using var doc = death?.AfterJson is { } json ? JsonDocument.Parse(json) : null;
                if (doc is null || !doc.RootElement.TryGetProperty("level", out var lv) || lv.GetString() is not { } died
                    || !doc.RootElement.TryGetProperty("depth", out var dv))
                    throw HostRefusal.Precondition("no_corpse_level", $"{characterId} has no level it died on");
                depth = dv.GetInt32();
                await tx.LevelClaims.Set(characterId, depth, died);
                var at = (await tx.Instances.NonTerminal()).FirstOrDefault(i => i.LevelCopy == died && i.State is not InstanceState.Draining);
                return (Target: at, Hash: (string?)died, Members: members.Where(m => m.CharacterId == characterId).ToList());
            }
            if (depth < 1 || depth > options.MapPool.Depths)
                throw new HostRefusal(RefusalCode.InvalidArgument, "bad_depth", $"depth {depth}");
            if (kind is TravelKind.Descend or TravelKind.StairsDown && depth > leader.ReachedDepth + 1)
                throw HostRefusal.Precondition("depth_locked", $"{characterId} has reached depth {leader.ReachedDepth}");
            var claim = await tx.LevelClaims.Get(characterId, depth);
            if (claim is null) return (Target: null, Hash: null, Members: members);
            foreach (var m in members) await tx.LevelClaims.Set(m.CharacterId, depth, claim.LevelId);
            var running = (await tx.Instances.NonTerminal()).FirstOrDefault(i => i.LevelCopy == claim.LevelId && i.State is not InstanceState.Draining);
            return (Target: running, Hash: claim.LevelId, Members: members);
        }, ct);

        if (plan.Members.Count == 0) throw HostRefusal.Precondition("not_in_party", "nobody to move");
        return await Arrive(plan.Target, depth, partyId, plan.Hash, plan.Members, ct);
    }

    public async Task<P.TravelResponse> ToAnchor(InstanceRecord caller, string characterId, byte[] token, string anchorOwner, string requestId, CancellationToken ct)
    {
        var plan = await data.WriteAsync(async (tx, _) =>
        {
            DescentLedger.RequireHub(caller);
            var (ch, _) = await DescentLedger.Leased(tx, caller, characterId, token);
            var party = await tx.Parties.ForCharacter(characterId);
            if (anchorOwner != characterId && party?.Members.Contains(anchorOwner) != true)
                throw HostRefusal.Denied("not_in_party", $"{anchorOwner} is not in {characterId}'s party");
            var anchor = await tx.Anchors.Get(anchorOwner) ?? throw HostRefusal.NotFound("no_anchor", $"{anchorOwner} has placed no teleporter");
            // Through the teleporter, its level copy becomes the caller's level for that depth (D-H12).
            await tx.LevelClaims.Set(characterId, anchor.Depth, anchor.LevelId);
            await tx.Audit.Write("travel.to_anchor", characterId, null, new { owner = anchorOwner, level = anchor.LevelId, depth = anchor.Depth });
            var at = (await tx.Instances.NonTerminal()).FirstOrDefault(i => i.LevelCopy == anchor.LevelId && i.State is not InstanceState.Draining);
            return (Target: at, anchor.Depth, Hash: anchor.LevelId, Party: party?.Id, Member: new Member(characterId, ch.Account, caller.Id));
        }, ct);
        return await Arrive(plan.Target, plan.Depth, plan.Party, plan.Hash, [plan.Member], ct);
    }

    /// <summary>2 and 3 of a trip: the target instance (running, or requested for the level copy, or for a hand-out), then the hops.</summary>
    async Task<P.TravelResponse> Arrive(InstanceRecord? running, int depth, string? partyId, string? hash, IReadOnlyList<Member> members, CancellationToken ct)
    {
        var plan = (Target: running, Hash: hash, Members: members);

        // 2. The target instance.
        InstanceRecord target;
        if (plan.Target is { } live) target = live;
        else
        {
            var requested = await lifecycle.RequestLevel(depth, partyId, plan.Hash, null, ct);
            if (requested is LevelRequestResult.AtCapacity cap)
            {
                // D-H14: a prewarm yields its slot to a level needed now; the trip waits for the room.
                if (await YieldAPrewarm(ct) is { } yielded)
                {
                    _awaitingSlot[Guid.NewGuid().ToString("N")] = new SlotWait(depth, partyId, plan.Hash, plan.Members);
                    return new P.TravelResponse { State = P.TravelState.Waiting, EtaMs = 5_000, Reason = $"instance_cap: prewarm {yielded} yields its slot" };
                }
                return new P.TravelResponse { State = P.TravelState.Waiting, EtaMs = 30_000, Reason = $"instance_cap: {cap.Running}/{cap.Max} levels running" };
            }
            target = ((LevelRequestResult.Accepted)requested).Instance;
            if (plan.Hash is null && !await TryHandOut(target.Id, depth, plan.Members, ct))
            {
                _awaitingLevel[target.Id] = (depth, plan.Members);
                return new P.TravelResponse { State = P.TravelState.Waiting, InstanceId = target.Id, EtaMs = 10_000, Reason = "pool_empty: linking a level" };
            }
        }

        await HopWhenLive(target, plan.Members, ct);
        return new P.TravelResponse { State = P.TravelState.Started, InstanceId = target.Id };
    }

    // 3. Hop when it is live.
    async Task HopWhenLive(InstanceRecord target, IReadOnlyList<Member> members, CancellationToken ct)
    {
        // Two trips to one target (a party's town portal and a death's respawn) travel together.
        _awaitingLive.AddOrUpdate(target.Id, new Trip(target.Id, members), (_, t) => t with { Members = [.. t.Members, .. members] });
        if (target.State == InstanceState.Live) await OnInstanceLive(target, ct);
        else if (_same.On) await OnInstanceBooting(target, ct);
    }

    readonly ConcurrentDictionary<string, bool> _respawning = new(); // characters on their way to town after a death

    /// <summary>
    /// D-H16: a character who died on <paramref name="level"/> respawns in town: that one character
    /// is sent to the hub as a town portal for one (PrepareHop to their level, HopReady, Retry).
    /// The rest of the party stays. Idempotent: a retried RecordDeath starts no second trip.
    /// </summary>
    public async Task RespawnInTown(InstanceRecord level, string characterId, CancellationToken ct)
    {
        if (level.Kind != InstanceKind.Level) return;
        var plan = await data.ReadAsync(async (tx, _) =>
        {
            if ((await tx.Leases.Get(characterId))?.InstanceId != level.Id) return (Member: (Member?)null, Hub: (InstanceRecord?)null);
            var ch = await tx.Characters.Get(characterId);
            var hubs = (await tx.Instances.NonTerminal()).Where(i => i.Kind == InstanceKind.Hub).ToList();
            var hub = hubs.FirstOrDefault(i => i.State == InstanceState.Live)
                      ?? (_same.On ? hubs.FirstOrDefault(i => i.State == InstanceState.Booting) : null);
            return (Member: ch is null ? null : new Member(characterId, ch.Account, level.Id), Hub: hub);
        }, ct);
        if (plan.Member is not { } member || plan.Hub is not { } hub)
        {
            log.LogWarning("{Character} died on {Level} but cannot respawn in town: {Why}", characterId, level.Id, plan.Member is null ? "not leased there" : "no hub");
            return;
        }
        if (!_respawning.TryAdd(characterId, true)) return;
        await HopWhenLive(hub, [member], ct);
    }

    sealed record SlotWait(int Depth, string? PartyId, string? Hash, IReadOnlyList<Member> Members);
    readonly ConcurrentDictionary<string, SlotWait> _awaitingSlot = new(); // trips waiting for a yielded prewarm's slot

    /// <summary>
    /// D-H14 (amended): at the cap, the oldest prewarm nobody has entered is drained to make room
    /// (it is reaped once empty and draining, and its slot frees). Null when there is none.
    /// </summary>
    async Task<string?> YieldAPrewarm(CancellationToken ct)
    {
        var prewarm = await data.ReadAsync(async (tx, _) =>
        {
            foreach (var i in (await tx.Instances.NonTerminal()).Where(i => i.Kind == InstanceKind.Level && i.State != InstanceState.Draining && i.Players == 0).OrderBy(i => i.Created))
                if (await tx.Audit.Count(new AuditQuery(Action: PrewarmedAction, Target: i.Id)) > 0
                    && await tx.Audit.Count(new AuditQuery(Action: InstanceManager.OccupiedAction, Target: i.Id)) == 0)
                    return i;
            return null;
        }, ct);
        if (prewarm is null) return null;
        try { await lifecycle.Drain(prewarm.Id, "prewarm_yield", "travel", ct); }
        catch (HostRefusal e) { log.LogInformation("prewarm {Id} could not yield: {Reason}", prewarm.Id, e.Reason); return null; }
        return prewarm.Id;
    }

    /// <summary>The trips a yielded prewarm made room for: request their level again, then go on as a descent does.</summary>
    async Task RetryAwaitingSlots(CancellationToken ct)
    {
        foreach (var (key, w) in _awaitingSlot.ToArray())
        {
            if (await lifecycle.RequestLevel(w.Depth, w.PartyId, w.Hash, null, ct) is not LevelRequestResult.Accepted accepted) continue;
            _awaitingSlot.TryRemove(key, out _);
            var target = accepted.Instance;
            if (w.Hash is null && !await TryHandOut(target.Id, w.Depth, w.Members, ct)) { _awaitingLevel[target.Id] = (w.Depth, w.Members); continue; }
            await HopWhenLive(target, w.Members, ct);
        }
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
        await RetryAwaitingSlots(ct);
        while (_prewarm.TryDequeue(out var p)) await PrewarmNext(p.Current, p.Members, ct);
        foreach (var (instanceId, (depth, members, current)) in _prewarmAwaiting.ToArray())
        {
            if (!await TryHandOut(instanceId, depth, members, ct)) continue;
            _prewarmAwaiting.TryRemove(instanceId, out _);
            await TellNextLevel(current, instanceId, ct);
        }
    }

    /// <summary>
    /// D-H14: the party just arrived on <paramref name="current"/> (depth d): claim and boot its
    /// d+1 level now, then tell <paramref name="current"/> that level's map so the clients can
    /// fetch it while they play. Never hops anyone: a later descent finds the claim and the live
    /// instance. Off in local same-process mode (one game, nothing to warm); skipped at the last
    /// free level slot, so a real descent always has one.
    /// </summary>
    async Task PrewarmNext(InstanceRecord current, IReadOnlyList<Member> members, CancellationToken ct)
    {
        if (_same.On || current.Kind != InstanceKind.Level || members.Count == 0) return;
        var depth = current.Depth + 1;
        if (depth > options.MapPool.Depths) return;
        var plan = await data.WriteAsync(async (tx, _) =>
        {
            var leader = members[0];
            var party = await tx.Parties.ForCharacter(leader.CharacterId);
            var claim = await tx.LevelClaims.Get(leader.CharacterId, depth);
            if (claim is null) return (Hash: (string?)null, Running: (InstanceRecord?)null, Party: party?.Id);
            foreach (var m in members) await tx.LevelClaims.Set(m.CharacterId, depth, claim.LevelId);
            var running = (await tx.Instances.NonTerminal()).FirstOrDefault(i => i.LevelCopy == claim.LevelId && i.State is not InstanceState.Draining);
            return (Hash: claim.LevelId, Running: running, Party: party?.Id);
        }, ct);
        if (plan.Running is { } warm) { await LinkPrewarm(warm.Id, current, ct); await TellNextLevel(current.Id, warm.Id, ct); return; }

        var levels = await data.ReadAsync((tx, _) => tx.Instances.CountLevels(
            InstanceState.Requested, InstanceState.LevelReady, InstanceState.Creating, InstanceState.Booting,
            InstanceState.Live, InstanceState.Suspect, InstanceState.Draining), ct);
        if (levels >= options.Instances.MaxLevelPods) // D-H14: not started at the cap (it would only yield again)
        {
            log.LogInformation("no prewarm of depth {Depth} for {Level}: {Running}/{Max} level slots in use", depth, current.Id, levels, options.Instances.MaxLevelPods);
            return;
        }
        if (await lifecycle.RequestLevel(depth, plan.Party, plan.Hash, null, ct) is not LevelRequestResult.Accepted accepted) return;
        var target = accepted.Instance;
        await LinkPrewarm(target.Id, current, ct);
        if (plan.Hash is null && !await TryHandOut(target.Id, depth, members, ct))
        {
            _prewarmAwaiting[target.Id] = (depth, members, current.Id);
            return;
        }
        await TellNextLevel(current.Id, target.Id, ct);
    }

    public const string PrewarmedAction = "instance.prewarmed";

    readonly ConcurrentDictionary<string, string> _bspSha = new(); // map name → hex; a level's files never change

    /// <summary>SHA-256 of the stored, unpacked .bsp (hex), computed once per map; "" when the file is not here.</summary>
    async Task<string> BspSha256(string map, CancellationToken ct)
    {
        if (_bspSha.TryGetValue(map, out var known)) return known;
        if (levels?.LevelFile(map, ".bsp") is not { } path || !File.Exists(path)) return "";
        await using var f = File.OpenRead(path);
        var hex = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(f, ct));
        _bspSha[map] = hex;
        return hex;
    }

    /// <summary>
    /// D-H14 (amended): the prewarm is held open while <paramref name="parent"/> runs. Audited, so
    /// a restarted service reads the link back (InstanceHooks.IsHeldOpen); the newest link wins.
    /// </summary>
    Task LinkPrewarm(string prewarmId, InstanceRecord parent, CancellationToken ct) =>
        data.WriteAsync(async (tx, _) =>
        {
            await tx.Audit.Write(PrewarmedAction, prewarmId, null, new { parent = parent.Id, depth = parent.Depth + 1 }, "travel");
            return true;
        }, ct);

    /// <summary>D-H14: tell <paramref name="currentId"/> the map of <paramref name="nextId"/> and where clients download it.</summary>
    async Task TellNextLevel(string currentId, string nextId, CancellationToken ct)
    {
        var next = await data.ReadAsync((tx, _) => tx.Instances.Get(nextId), ct);
        if (next?.LevelHash is not { } hash) return;
        var map = MapIdentity.MapName(options.Mod.Name, next.Depth, hash);
        if (!await commands.Send(currentId, new InstanceCommand.NextLevel(next.Depth, map, options.Instances.FastDlUrl, await BspSha256(map, ct)), ct))
            log.LogInformation("NextLevel {Map} could not reach {Level}", map, currentId);
    }

    /// <summary>The target is live: flip each member's route and ask the source to prepare the hop.</summary>
    public async Task OnInstanceLive(InstanceRecord target, CancellationToken ct)
    {
        if (!_awaitingLive.TryRemove(target.Id, out var trip)) return;
        if (target.Kind == InstanceKind.Level) _prewarm.Enqueue((target, trip.Members)); // D-H14, on the next tick
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

    /// <summary>
    /// Same-process travel: the target has a pod, so its connection record exists. Ask the source
    /// to prepare the hop with the target's map and record; its game changes map and becomes the target.
    /// </summary>
    public async Task OnInstanceBooting(InstanceRecord target, CancellationToken ct)
    {
        if (_same.Connections?.For(target.Id) is not { } json) return;
        if (!_awaitingLive.TryRemove(target.Id, out var trip)) return;
        var map = JsonSerializer.Deserialize<LocalConnection>(json, LocalInstanceHost.Json)?.Map ?? "";
        foreach (var m in trip.Members)
        {
            var hopId = InstanceStreams.HopId(target.Id, m.SteamId);
            _prepared[hopId] = m;
            _sameProcessHops[hopId] = true;
            if (!await commands.Send(m.SourceInstance, new InstanceCommand.PrepareHop(m.SteamId, target.Id, map, json), ct))
                log.LogWarning("same-process PrepareHop for {SteamId} could not reach {Source}", m.SteamId, m.SourceInstance);
        }
    }

    /// <summary>
    /// The source checkpointed and released: tell the client to retry, which lands it on the new
    /// route. Same-process, there is no new route: the source's game is leaving its instance.
    /// </summary>
    public async Task HopReady(InstanceRecord caller, string hopId, string steamId, CancellationToken ct)
    {
        if (!_prepared.TryRemove(hopId, out var m)) { log.LogWarning("HopReady for unknown hop {Hop}", hopId); return; }
        _respawning.TryRemove(m.CharacterId, out _);
        if (_sameProcessHops.TryRemove(hopId, out _))
        {
            await lifecycle.Leave(m.SourceInstance, "same_process_hop", ct);
            return;
        }
        await commands.Send(m.SourceInstance, new InstanceCommand.Retry(steamId), ct);
    }

    public int Waiting => _awaitingLevel.Count + _awaitingLive.Count;
}

/// <summary>The trips waiting for a level retry on a tick; D-H12 sessions end after the idle time.</summary>
public sealed class TravelWorker(TravelCoordinator travel, IHostData data, IMapPool pool, ServiceOptions options, TimeProvider clock, ILogger<TravelWorker> log, Anchors? anchors = null) : BackgroundService
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
        var (retire, unanchored) = await data.WriteAsync(async (tx, _) =>
        {
            var free = new List<string>();
            var lost = new List<string>();
            var now = tx.Now;
            foreach (var ch in await tx.LevelClaims.CharactersWithClaims())
            {
                if (await tx.Leases.Get(ch) is not null) { await tx.LevelClaims.Touch(ch); continue; }
                var claims = await tx.LevelClaims.ForCharacter(ch);
                if (claims.Count == 0 || now - claims.Max(c => c.LastUsed) < options.Travel.SessionIdle) continue;
                var ids = await tx.LevelClaims.Drop(ch);
                await tx.Audit.Write("session.end", ch, null, new { levels = ids });
                // D-H18: a level's files are shared by its copies; retired only when no copy is claimed or running.
                var running = (await tx.Instances.NonTerminal()).Select(i => i.LevelHash).ToHashSet();
                foreach (var h in ids.Select(LevelIds.HashOf).Distinct())
                    if (await tx.LevelClaims.ClaimsOn(h) == 0 && !running.Contains(h)) free.Add(h);
                // D-H16: a teleporter lives as long as its level copy is someone's level; D-H18: so
                // does a corpse waiting for a fork's first instance.
                foreach (var id in ids)
                    if (await tx.LevelClaims.ClaimsOnCopy(id) == 0)
                    {
                        await LevelForks.EndPending(tx, id);
                        foreach (var a in await tx.Anchors.OnLevel(id))
                        {
                            await tx.Anchors.Remove(a.CharacterId);
                            await tx.Audit.Write(Anchors.ClearedAction, a.CharacterId, new { level = a.LevelId, depth = a.Depth }, new { reason = "level_unclaimed" });
                            lost.Add(a.CharacterId);
                        }
                    }
            }
            return (free, lost);
        }, ct);
        foreach (var hash in retire) await pool.RetireLevelAsync(hash, ct);
        if (anchors is not null) foreach (var owner in unanchored) await anchors.Notify(owner, ct);
        return retire.Count;
    }
}
