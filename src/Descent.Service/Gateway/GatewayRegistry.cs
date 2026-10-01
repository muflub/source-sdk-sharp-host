using Descent.Service.Api;
using Descent.Service.Ledger;
using SourceSharp.Host.Abstractions;
using G = SourceSharp.Host.Proto.Gateway;
using P = SourceSharp.Host.Proto;

namespace Descent.Service.Gateway;

/// <summary>The gateway's health as the Dashboard shows it (§8.2: three missed pings → unreachable).</summary>
public sealed class GatewayState
{
    public DateTimeOffset? LastPing { get; set; }
    public string? GatewayId { get; set; }
    public int Sessions { get; set; }
    public G.GatewayStats? LastStats { get; set; }
    public bool Reachable(DateTimeOffset now) => LastPing is { } t && now - t < TimeSpan.FromSeconds(6);
}

/// <summary>
/// The session registry (plan §8.2: the service owns it, the gateway rebuilds from it) and the
/// identity rules of 7e: a session is bound to a SteamID by the hub's PlayerJoined or the
/// gateway's Identify; single presence keeps the oldest live session and takes over a silent
/// one; a closed identified session releases its character's lease at once (§4.3).
/// </summary>
public sealed class GatewayRegistry(
    IHostData data,
    IGatewayControl control,
    ServiceOptions options,
    TimeProvider clock,
    ILogger<GatewayRegistry> log) : IPlayerSessions, IInstanceRouting
{
    /// <summary>A session silent this long may be taken over by a new one for the same SteamID.</summary>
    public static readonly TimeSpan SilentAfter = TimeSpan.FromSeconds(15);

    /// <summary>How long a join the game reported before the gateway's session event waits for that event.</summary>
    public static readonly TimeSpan PendingJoinLimit = TimeSpan.FromSeconds(30);

    readonly System.Collections.Concurrent.ConcurrentDictionary<(string Backend, string Peer), (string SteamId, string InstanceId, DateTimeOffset At)> _pendingJoins = new();

    async Task<InstanceRecord?> ByBackend(IReadTx tx, string backend) =>
        (await tx.Instances.NonTerminal()).FirstOrDefault(i => i.PodIp is not null && GatewayControlClient.Backend(i, options) == backend);

    string BackendOf(InstanceRecord i) => GatewayControlClient.Backend(i, options);

    // ------------------------------------------------------------------ events from the gateway

    public async Task SessionOpened(G.SessionEvent e, CancellationToken ct)
    {
        var instance = await data.WriteAsync(async (tx, _) =>
        {
            var now = clock.GetUtcNow();
            var instance = await ByBackend(tx, e.Backend);
            var existing = await tx.Sessions.Get(e.SessionId);
            await tx.Sessions.Upsert(new SessionRecord(e.SessionId, e.ClientAddr, e.Steamid.Length > 0 ? e.Steamid : existing?.SteamId,
                instance?.Id, e.Backend, e.Peer, "open", existing?.Opened ?? now, now, existing?.IdentifiedAt));
            return instance;
        }, ct);
        // The game reported this peer's join before the gateway reported the session: bind it now.
        if (_pendingJoins.TryRemove((e.Backend, e.Peer), out var pending) && clock.GetUtcNow() - pending.At <= PendingJoinLimit
            && instance?.Id == pending.InstanceId)
        {
            var outcome = await Bind(instance, e.Backend, e.Peer, pending.SteamId, ct);
            if (outcome.Session is { } s && !outcome.Response.Allowed)
            {
                log.LogWarning("late join of {SteamId} on {Instance} refused: {Reason}", pending.SteamId, instance.Id, outcome.Response.Reason);
                await control.CloseSession(s.ClientAddr, outcome.Response.Reason, ct);
            }
        }
    }

    public Task SessionMoved(G.SessionEvent e, CancellationToken ct) => SessionOpened(e, ct);

    public async Task SessionClosed(G.SessionEvent e, CancellationToken ct)
    {
        await data.WriteAsync(async (tx, _) =>
        {
            var s = await tx.Sessions.Get(e.SessionId);
            await tx.Sessions.Close(e.SessionId, e.Reason.Length > 0 ? e.Reason : "closed");
            if (s?.SteamId is not { } steamId || s.InstanceId is not { } instanceId) return true;
            // §4.3: an identified session's close releases the lease at once; the reserve goes with it.
            foreach (var ch in await tx.Characters.List(steamId))
                if (await tx.Leases.Get(ch.Id) is { } lease && lease.InstanceId == instanceId)
                {
                    await DescentLedger.SweepReserve(tx, ch.Id, instanceId, "reserve_released");
                    await tx.Leases.Release(ch.Id, null);
                    await tx.Audit.Write("lease.release", ch.Id, null, new { instance = instanceId, reason = "session_closed" });
                }
            return true;
        }, ct);
    }

    /// <summary>Single presence (7e) for <paramref name="steamId"/>, excluding <paramref name="sessionId"/>: refuse, or name the silent sessions to close.</summary>
    async Task<(bool Allowed, List<SessionRecord> TakeOver)> Presence(IReadTx tx, string steamId, string sessionId)
    {
        var now = clock.GetUtcNow();
        var others = (await tx.Sessions.ForSteamId(steamId)).Where(s => s.Id != sessionId).ToList();
        if (others.Any(s => now - s.LastSeen < SilentAfter)) return (false, []);
        return (true, others);
    }

    public async Task<G.IdentifyResponse> Identify(G.IdentifyRequest req, CancellationToken ct)
    {
        var (allowed, takeOver, backend) = await data.WriteAsync(async (tx, _) =>
        {
            var (ok, silent) = await Presence(tx, req.Steamid, req.SessionId);
            if (!ok) return (false, new List<SessionRecord>(), "");
            foreach (var s in silent) await tx.Sessions.Close(s.Id, "taken_over");
            // A returning player goes where a character of the account is leased; a new arrival to the hub.
            string target = "";
            foreach (var ch in await tx.Characters.List(req.Steamid))
                if (await tx.Leases.Get(ch.Id) is { } lease && await tx.Instances.Get(lease.InstanceId) is { PodIp: not null } inst)
                { target = BackendOf(inst); break; }
            var existing = await tx.Sessions.Get(req.SessionId);
            var now = clock.GetUtcNow();
            await tx.Sessions.Upsert(new SessionRecord(req.SessionId, req.ClientAddr, req.Steamid, existing?.InstanceId, existing?.Backend ?? target,
                existing?.Peer ?? "", "open", existing?.Opened ?? now, now, now));
            return (true, silent, target);
        }, ct);
        var response = new G.IdentifyResponse { Allowed = allowed, Backend = backend, TableVersion = (ulong)control.TableVersion };
        if (!allowed) response.Reason = "single_presence: another live session holds this SteamID";
        response.CloseSessions.AddRange(takeOver.Select(s => s.ClientAddr));
        return response;
    }

    // ------------------------------------------------------------------ the game servers' side (IPlayerSessions)

    public async Task<P.PlayerJoinedResponse> PlayerJoined(InstanceRecord caller, string peer, string steamId, CancellationToken ct)
    {
        if (caller.PodIp is null)
            return new P.PlayerJoinedResponse { Allowed = true, ClientAddr = peer, Reason = "no gateway in front of this instance" };
        var backend = BackendOf(caller);
        var outcome = await Bind(caller, backend, peer, steamId, ct);
        if (outcome.Session is null)
        {
            var now = clock.GetUtcNow();
            foreach (var (key, stale) in _pendingJoins)
                if (now - stale.At > PendingJoinLimit) _pendingJoins.TryRemove(key, out _);
            _pendingJoins[(backend, peer)] = (steamId, caller.Id, now); // bound when the gateway's event arrives
        }
        return outcome.Response;
    }

    /// <summary>Binds the gateway's session for (backend, peer) to the SteamID (7e single presence), and routes it.</summary>
    async Task<JoinOutcome> Bind(InstanceRecord caller, string backend, string peer, string steamId, CancellationToken ct)
    {
        var outcome = await data.WriteAsync(async (tx, _) =>
        {
            var s = await tx.Sessions.ByBackendPeer(backend, peer);
            if (s is null) return new JoinOutcome(new P.PlayerJoinedResponse { Allowed = true, ClientAddr = peer, Reason = "peer not relayed by the gateway yet" }, [], null);
            var (ok, silent) = await Presence(tx, steamId, s.Id);
            if (!ok)
                return new JoinOutcome(new P.PlayerJoinedResponse { Allowed = false, SessionId = s.Id, ClientAddr = s.ClientAddr, Reason = "single_presence" }, [], s);
            foreach (var old in silent) await tx.Sessions.Close(old.Id, "taken_over");
            var now = clock.GetUtcNow();
            var bound = await tx.Sessions.Upsert(s with { SteamId = steamId, InstanceId = caller.Id, IdentifiedAt = s.IdentifiedAt ?? now, LastSeen = now });
            return new JoinOutcome(new P.PlayerJoinedResponse { Allowed = true, SessionId = bound.Id, ClientAddr = bound.ClientAddr }, silent, bound);
        }, ct);
        foreach (var old in outcome.Close) await control.CloseSession(old.ClientAddr, "taken_over", ct);
        if (outcome.Session is { } session && outcome.Response.Allowed)
            await control.SetRoute(session.ClientAddr, backend, steamId, holdUntilReady: false, ct);
        return outcome;
    }

    sealed record JoinOutcome(P.PlayerJoinedResponse Response, List<SessionRecord> Close, SessionRecord? Session);

    public Task<P.ResolvePeerResponse> ResolvePeer(InstanceRecord caller, string peer, CancellationToken ct) =>
        data.ReadAsync(async (tx, _) =>
        {
            var s = caller.PodIp is null ? null : await tx.Sessions.ByBackendPeer(BackendOf(caller), peer);
            return s is null
                ? new P.ResolvePeerResponse { Found = false }
                : new P.ResolvePeerResponse { Found = true, SessionId = s.Id, ClientAddr = s.ClientAddr, Steamid = s.SteamId ?? "" };
        }, ct);

    public Task PlayerLeft(InstanceRecord caller, string peer, string steamId, CancellationToken ct) => Task.CompletedTask;

    // ------------------------------------------------------------------ instances coming and going (IInstanceRouting)

    public async Task InstanceLive(InstanceRecord instance, CancellationToken ct)
    {
        if (instance.PodIp is null) return;
        var backend = BackendOf(instance);
        await control.BackendReady(backend, true, ct);
        if (instance.Kind == InstanceKind.Hub) await control.SetDefault(backend, ct);
    }

    /// <summary>Sessions on a departing instance fall back to the hub route (§7.3).</summary>
    public async Task InstanceGone(InstanceRecord instance, CancellationToken ct)
    {
        if (instance.PodIp is null) return;
        var backend = BackendOf(instance);
        var (sessions, hub) = await data.ReadAsync(async (tx, _) =>
        {
            var hubRow = (await tx.Instances.NonTerminal()).FirstOrDefault(i => i.Kind == InstanceKind.Hub && i.Id != instance.Id && i.State == InstanceState.Live && i.PodIp is not null);
            return (await tx.Sessions.ForBackend(backend), hubRow);
        }, ct);
        await control.BackendReady(backend, false, ct);
        if (hub is null) { log.LogWarning("instance {Instance} gone with no live hub to fall back to", instance.Id); return; }
        foreach (var s in sessions)
            await control.SetRoute(s.ClientAddr, BackendOf(hub), s.SteamId, holdUntilReady: false, ct);
    }
}
