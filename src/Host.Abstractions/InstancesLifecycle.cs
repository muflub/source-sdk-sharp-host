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
    public sealed record PrepareHop(string SteamId, string TargetInstanceId) : InstanceCommand;
    public sealed record Say(string Text) : InstanceCommand;
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
}
