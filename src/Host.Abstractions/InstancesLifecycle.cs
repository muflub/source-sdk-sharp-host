namespace SourceSharp.Host.Abstractions;

// Plan §7.3–7.5: the instance manager's edges. IInstanceLifecycle is what the game API
// (InstanceService) and the admin call into the manager; IInstanceCommands is the
// down-stream the manager pushes commands through (the lead implements it over the
// Connect stream); IInstanceHooks is what the ledger / travel code does on transitions.

/// <summary>The result of asking for a level instance.</summary>
public abstract record LevelRequestResult
{
    /// <summary>A row was created; it is created as a pod on the manager's next tick.</summary>
    public sealed record Accepted(InstanceRecord Instance) : LevelRequestResult;

    /// <summary>MaxLevelPods non-terminal level instances exist: ask again later (§7.5, the visible wait).</summary>
    public sealed record AtCapacity(int Running, int Max) : LevelRequestResult;
}

/// <summary>What MapReady did: Live, still waiting for pod Ready, or refused (port mismatch → failed).</summary>
public enum MapReadyOutcome { Live = 1, AwaitingPodReady, PortMismatch }

public sealed record MapReadyResult(MapReadyOutcome Outcome, InstanceRecord Instance);

public interface IInstanceLifecycle
{
    /// <summary>
    /// A level instance for (depth, party). With a level hash the row starts level_ready, else
    /// requested until <see cref="AssignLevel"/>. Refused with a typed result at MaxLevelPods.
    /// </summary>
    Task<LevelRequestResult> RequestLevel(int depth, string? partyId, string? levelHash, long? packVersion, CancellationToken ct = default);

    /// <summary>requested → level_ready: the pool handed this instance a level.</summary>
    Task<InstanceRecord> AssignLevel(string instanceId, string levelHash, long packVersion, CancellationToken ct = default);

    /// <summary>
    /// The pod's credentials, checked against the row: SHA-256 hex of the token, compared in
    /// constant time. Null for an unknown, terminal or mismatched instance.
    /// </summary>
    Task<InstanceRecord?> Authenticate(string instanceId, string token, CancellationToken ct = default);

    /// <summary>The game's Booting call: records the rules module it runs and the mod image digest.</summary>
    Task<InstanceRecord> OnBooting(string instanceId, string rulesSha256, string? modImageDigest, CancellationToken ct = default);

    /// <summary>The port the socket reports. Compared with the expected port; a mismatch fails the instance (audited).</summary>
    Task<MapReadyResult> OnMapReady(string instanceId, int port, CancellationToken ct = default);

    /// <summary>The Connect stream opened (or reopened after a service restart: adoption's reconnect).</summary>
    Task OnStreamOpened(string instanceId, CancellationToken ct = default);

    Task OnHeartbeat(string instanceId, int players, CancellationToken ct = default);

    /// <summary>The Connect stream closed: outside draining this is a crash.</summary>
    Task OnStreamClosed(string instanceId, CancellationToken ct = default);

    /// <summary>live → draining (admin Drain, hub replacement): refuses new routes, reaped when empty.</summary>
    Task<InstanceRecord> Drain(string instanceId, string reason, string actor, CancellationToken ct = default);

    /// <summary>
    /// Local same-process travel (D-H13): the instance's game is becoming another instance. It goes
    /// draining with no Drain command (the game keeps running), heartbeats and timeouts stop
    /// counting, and it is reaped, not crashed, when its stream closes.
    /// </summary>
    Task Leave(string instanceId, string reason, CancellationToken ct = default);

    Task Kick(string instanceId, string steamId, string reason, string actor, CancellationToken ct = default);

    /// <summary>The admin console (Q22): sent as an Exec command, audited.</summary>
    Task Exec(string instanceId, string command, string actor, CancellationToken ct = default);

    /// <summary>After hub re-creation stopped (five failures in five minutes), let it try again.</summary>
    Task ResumeHubs(string actor, CancellationToken ct = default);
}

/// <summary>A command pushed down an instance's Connect stream. Not proto types: the lead maps them.</summary>
public abstract record InstanceCommand
{
    public sealed record Drain(string Reason) : InstanceCommand;
    public sealed record Shutdown(string Reason) : InstanceCommand;
    public sealed record Kick(string SteamId, string Reason) : InstanceCommand;
    public sealed record Retry(string SteamId) : InstanceCommand;
    /// <summary><paramref name="LocalConnection"/>: only for a same-process hop in local mode (the target's connection record).</summary>
    public sealed record PrepareHop(string SteamId, string TargetInstanceId, string Map = "", string LocalConnection = "") : InstanceCommand;
    public sealed record Say(string Text) : InstanceCommand;
    /// <summary>D-H14: the party's next level down (its map, and where a client downloads it).</summary>
    /// <summary><paramref name="MapSha256"/>: lower-case hex of the unpacked .bsp's SHA-256, "" when unknown.</summary>
    public sealed record NextLevel(int Depth, string Map, string DownloadUrl, string MapSha256 = "") : InstanceCommand;
    public sealed record Exec(string Command) : InstanceCommand;
    /// <summary>The service ended a lease (expiry, admin, crash): the SDK raises LeaseLost.</summary>
    public sealed record LeaseRevoked(string CharacterId, string Reason) : InstanceCommand;
}

public interface IInstanceCommands
{
    /// <summary>Queues the command on the instance's stream. False when the instance has no open stream.</summary>
    Task<bool> Send(string instanceId, InstanceCommand command, CancellationToken ct = default);
}

/// <summary>
/// What the service does on the manager's transitions. Each is called after the transition
/// is persisted and must be idempotent: after a service restart the manager re-runs the
/// side effects of a terminal transition whose completion was not recorded.
/// </summary>
public interface IInstanceHooks
{
    /// <summary>The instance's pod exists and its game is starting (Booting).</summary>
    Task OnBooting(InstanceRecord instance, CancellationToken ct);

    /// <summary>The instance went live (MapReady and pod Ready): routes may point at it.</summary>
    Task OnLive(InstanceRecord instance, CancellationToken ct);

    /// <summary>Reap, before the pod is deleted: stop routing to it, copy its log.</summary>
    Task OnReaping(InstanceRecord instance, CancellationToken ct);

    /// <summary>Reap, after the pod delete: §5.3 sweep, lease release, hub fallback for attached sessions.</summary>
    Task OnReaped(InstanceRecord instance, CancellationToken ct);

    /// <summary>
    /// The instance ended without a reap: crashed (pod gone or failed, stream closed, heartbeats
    /// lost past the reap count is a reap, not this) or failed (boot timeout, port mismatch,
    /// refused mod image). The pod is already deleted. Sweep, release leases, hub fallback.
    /// </summary>
    Task OnCrashed(InstanceRecord instance, CancellationToken ct);

    /// <summary>Whether a corpse lies on this instance: an empty level then waits CorpseGrace instead of EmptyGrace (Q10).</summary>
    Task<bool> HasCorpses(InstanceRecord instance, CancellationToken ct);

    /// <summary>
    /// Whether something else keeps this empty level open (D-H14: a prewarm whose party's level
    /// still runs). Held, it is never judged empty; its empty grace starts when the hold ends.
    /// Reads only: called under the manager's lock.
    /// </summary>
    Task<bool> IsHeldOpen(InstanceRecord instance, CancellationToken ct);
}
