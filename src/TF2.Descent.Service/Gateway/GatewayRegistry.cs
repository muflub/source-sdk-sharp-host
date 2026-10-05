using TF2.Descent.Service.Api;
using TF2.Descent.Service.Ledger;
using SourceSharp.Host.Abstractions;
using G = SourceSharp.Host.Proto.Gateway;
using P = SourceSharp.Host.Proto;

namespace TF2.Descent.Service.Gateway;

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
    IInstanceCommands commands,
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
            await tx.SteamAuth.Delete(e.SessionId); // plan_steam_auth S-10: the verdict goes with its session
            if (s?.SteamId is not { } steamId || s.InstanceId is not { } instanceId) return true;
            // Only the player's last session on that instance gives the lease up: a taken-over or
            // carried session closing late (a gateway rollout, a client back from a new port) must
            // not release the lease its successor is playing on.
            if (s.State == "closed"
                || (await tx.Sessions.ForSteamId(steamId)).Any(o => o.Id != s.Id && o.State == "open" && o.InstanceId == instanceId))
                return true;
            // §4.3: an identified session's close releases the lease at once; the reserve stays with
            // the level until the level ends (§5.2a).
            foreach (var ch in await tx.Characters.List(steamId))
                if (await tx.Leases.Get(ch.Id) is { } lease && lease.InstanceId == instanceId)
                {
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

    /// <summary>How long after its logon a claimed ticket may be readmitted once (plan_steam_auth S-11): the live option, else 6 h.</summary>
    static TimeSpan ClaimedTrust(Setting? live) =>
        GatewayControlClient.ParseOptions(live) is { ClaimedTrustMs: > 0 } o ? TimeSpan.FromMilliseconds(o.ClaimedTrustMs) : TimeSpan.FromHours(6);

    public async Task<G.IdentifyResponse> Identify(G.IdentifyRequest req, CancellationToken ct)
    {
        var (allowed, reason, takeOver, backend, reauth) = await data.WriteAsync(async (tx, _) =>
        {
            var now = clock.GetUtcNow();
            // plan_steam_auth S-14: the table is authoritative even when the gateway's copy lags.
            if ((await tx.Bans.List(now)).Any(b => b.Kind == "steamid" && b.Value == req.Steamid))
                return (false, "banned", new List<SessionRecord>(), "", false);

            var reauth = false;
            if (req.TicketClaimed)
            {
                // S-11: readmit once a ticket this host validated before, under an earlier logon, recently.
                var trust = ClaimedTrust(await tx.Settings.Get(GatewayControlClient.OptionsKey));
                var row = (await tx.SteamAuth.ForSteamId(req.Steamid)).FirstOrDefault(r =>
                    r.TicketSha256 == req.TicketSha256 && r.Epoch != req.Epoch && r.State == "valid" && now - r.ValidatedAt <= trust);
                if (row is null) return (false, "please reconnect", new List<SessionRecord>(), "", false);
                // Once: a row lives only while its session is open, and this Identify takes that session over
                // below, deleting the row with it; a second replay of the ticket finds nothing.
                reauth = true;
            }

            List<SessionRecord> silent;
            if (req.SteamValidated || req.TicketClaimed)
            {
                // S-17: the gateway, which sees every packet, already decided presence; what the table still
                // holds for this SteamID is stale and is taken over.
                silent = (await tx.Sessions.ForSteamId(req.Steamid)).Where(x => x.Id != req.SessionId && x.State == "open").ToList();
            }
            else
            {
                var (ok, presence) = await Presence(tx, req.Steamid, req.SessionId);
                if (!ok) return (false, "single_presence: another live session holds this SteamID", new List<SessionRecord>(), "", false);
                silent = presence;
            }
            foreach (var x in silent)
            {
                await tx.Sessions.Close(x.Id, "taken_over");
                await tx.SteamAuth.Delete(x.Id);
            }
            // A returning player goes where a character of the account is leased; a new arrival to the hub.
            string target = "";
            foreach (var ch in await tx.Characters.List(req.Steamid))
                if (await tx.Leases.Get(ch.Id) is { } lease && await tx.Instances.Get(lease.InstanceId) is { PodIp: not null } inst)
                { target = BackendOf(inst); break; }
            var existing = await tx.Sessions.Get(req.SessionId);
            await tx.Sessions.Upsert(new SessionRecord(req.SessionId, req.ClientAddr, req.Steamid, existing?.InstanceId, existing?.Backend ?? target,
                existing?.Peer ?? "", "open", existing?.Opened ?? now, now, now));
            if (req.SteamValidated || reauth)
                await tx.SteamAuth.Upsert(new SteamAuthRecord(req.SessionId, req.Steamid, req.TicketSha256, req.GsSteamid, req.Epoch,
                    reauth ? "reauthing" : "valid", now));
            return (true, "", silent, target, reauth);
        }, ct);
        var response = new G.IdentifyResponse { Allowed = allowed, Backend = backend, TableVersion = (ulong)control.TableVersion, Reauth = reauth };
        if (!allowed) response.Reason = reason;
        response.CloseSessions.AddRange(takeOver.Select(s => s.ClientAddr));
        return response;
    }

    /// <summary>S-12: Steam withdrew a session's auth, or a ban now covers it. The gateway stopped its upstream;
    /// the instance kicks the player so the slot frees and the client is told why.</summary>
    public async Task AuthRevoked(G.AuthRevokedEvent e, CancellationToken ct)
    {
        var session = await data.ReadAsync((tx, _) => tx.Sessions.Get(e.SessionId), ct);
        var instance = session?.InstanceId;
        if (instance is null && e.Backend.Length > 0)
            instance = (await data.ReadAsync(async (tx, _) => await ByBackend(tx, e.Backend), ct))?.Id;
        if (instance is null || e.Steamid.Length == 0)
        {
            log.LogWarning("revoked session {Session} ({SteamId}) has no instance to kick it from", e.SessionId, e.Steamid);
            return;
        }
        if (!await commands.Send(instance, new InstanceCommand.Kick(e.Steamid, e.Reason), ct))
            log.LogWarning("Kick of {SteamId} could not reach {Instance}", e.Steamid, instance);
    }

    /// <summary>S-11/S-20: a carried session needs a fresh ticket; the instance asks its client through the game.</summary>
    public async Task ReauthNeeded(G.ReauthNeededEvent e, CancellationToken ct)
    {
        var (session, instance) = await data.ReadAsync(async (tx, _) =>
        {
            var s = await tx.Sessions.Get(e.SessionId);
            var i = s?.InstanceId ?? (e.Backend.Length > 0 ? (await ByBackend(tx, e.Backend))?.Id : null);
            return (s, i);
        }, ct);
        if (instance is null)
        {
            log.LogWarning("session {Session} needs a fresh ticket but has no instance to ask", e.SessionId);
            return;
        }
        var peer = e.Peer.Length > 0 ? e.Peer : session?.Peer ?? "";
        var deadline = clock.GetUtcNow() + ReauthWindow;
        if (!await commands.Send(instance, new InstanceCommand.Reauth(e.Steamid, e.GsSteamid, e.Nonce, deadline, peer), ct))
            log.LogWarning("Reauth for {SteamId} could not reach {Instance}", e.Steamid, instance);
    }

    /// <summary>What the game is told it has; the gateway's own ReauthTimeout decides (it asks again or kicks).</summary>
    static readonly TimeSpan ReauthWindow = TimeSpan.FromSeconds(30);

    /// <summary>S-20: the client's fresh ticket, for the session behind (caller, peer), checked by the gateway against its nonce.</summary>
    public async Task<P.SubmitTicketResponse> SubmitTicket(InstanceRecord caller, string peer, string nonce, byte[] ticket, CancellationToken ct)
    {
        if (caller.PodIp is null) return new P.SubmitTicketResponse { Outcome = "Unavailable", Reason = "no gateway in front of this instance" };
        var s = await data.ReadAsync((tx, _) => tx.Sessions.ByBackendPeer(BackendOf(caller), peer), ct);
        if (s is not { State: "open", SteamId: { } steamId })
            return new P.SubmitTicketResponse { Outcome = "Stale", Reason = $"no open session for {peer}" };
        var r = await control.Reauth(s.Id, nonce, ticket, ct);
        if (r.Outcome == "Ok")
            await data.WriteAsync(async (tx, _) =>
            {
                await tx.SteamAuth.Upsert(new SteamAuthRecord(s.Id, steamId, r.TicketSha256, r.GsSteamid, r.Epoch, "valid", clock.GetUtcNow()));
                return true;
            }, ct);
        return new P.SubmitTicketResponse { Outcome = r.Outcome, Reason = r.Reason };
    }

    /// <summary>S-15: the gateway's temp ban, stored so SyncTable restores it after either side restarts.</summary>
    public Task TempBanned(G.TempBannedEvent e, CancellationToken ct) =>
        data.WriteAsync(async (tx, _) =>
        {
            await tx.Bans.Set(new BanRecord(e.Ban.Kind, e.Ban.Value,
                e.Ban.UntilUnixMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(e.Ban.UntilUnixMs) : null,
                "auto", e.Ban.Reason, clock.GetUtcNow()));
            await tx.Audit.Write("ban.auto", $"{e.Ban.Kind}:{e.Ban.Value}", null, new { e.Ban.UntilUnixMs, e.Ban.Reason, e.Strikes }, "gateway");
            return true;
        }, ct);

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
            // plan_steam_auth S-7: the session's SteamID (the gateway validated it) is the player; a game claiming
            // another one for the peer is refused, never believed.
            if (s.SteamId is { } known && known != steamId)
                return new JoinOutcome(new P.PlayerJoinedResponse { Allowed = false, SessionId = s.Id, ClientAddr = s.ClientAddr,
                    Reason = $"steamid_mismatch: this peer is {known}" }, [], null);
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

    /// <summary>
    /// S-13: a player who left for good releases their ticket at once, so a quick rejoin finds it free. A hop
    /// is not a leave: travel moved the session's route to the target before the source said PlayerLeft, so
    /// the session is no longer on the caller's backend and nothing is released.
    /// </summary>
    public async Task PlayerLeft(InstanceRecord caller, string peer, string steamId, CancellationToken ct)
    {
        if (caller.PodIp is null) return;
        var s = await data.ReadAsync((tx, _) => tx.Sessions.ByBackendPeer(BackendOf(caller), peer), ct);
        if (s is not { State: "open" }) return;
        await control.EndAuth(s.Id, ct);
    }

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
