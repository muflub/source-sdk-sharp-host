namespace SourceSharp.Host.Abstractions;

/// <summary>
/// What the pool needs to know about players to order its work (plan §9.3). The composition
/// root implements it from the parties and instances; the pool never reads them itself.
/// </summary>
public interface IPoolDemand
{
    /// <summary>The depths parties are on now: the pool links depth N+1 for each at priority 1.</summary>
    IReadOnlyCollection<int> PartyDepths();

    /// <summary>The depths the church offers (TF2 plan: waypoints): priority 2.</summary>
    IReadOnlyCollection<int> SelectableDepths();
}

/// <summary>No parties, nothing selectable: every deficit is priority 3, shallowest first.</summary>
public sealed class NoPoolDemand : IPoolDemand
{
    public IReadOnlyCollection<int> PartyDepths() => [];
    public IReadOnlyCollection<int> SelectableDepths() => [];
}

/// <summary>
/// Starts a bake (plan §9.2): in production a Kubernetes Job from the service image with the
/// content volume mounted, running <c>Descent.Service bake</c>; the Job POSTs its pack back.
/// Returns when the Job has ended; throws on failure.
/// </summary>
public interface IBakeLauncher
{
    Task<string> BakeAsync(string mod, string library, CancellationToken ct);
}

/// <summary>A travel's request for a level: the level (handed out, once, ever) or "waiting".</summary>
public sealed record PoolRequestResult(LevelRecord? Level, string? Waiting)
{
    public bool Ready => Level is not null;
}

/// <summary>One depth as the Dashboard shows it.</summary>
public sealed record DepthStatus(int Depth, long? PackId, int? PackVersion, int Target, int Ready, int InFlight, int Draining, string? Note);

/// <summary>The pool's state for the Dashboard and /healthz (plan §9.3, §11).</summary>
public sealed record PoolStatus(bool WaitingForRules, string Message, IReadOnlyList<DepthStatus> Depths, DateTimeOffset? LastTick)
{
    public const string WaitingForRulesMessage = "waiting for the hub's rules module";
    public static readonly PoolStatus Starting = new(true, WaitingForRulesMessage, [], null);
}

/// <summary>The pool as travel and the reaper use it (plan §9.3).</summary>
public interface IMapPool
{
    PoolStatus Status { get; }

    /// <summary>
    /// Hands out a ready level of <paramref name="depth"/> to <paramref name="instanceId"/>
    /// (once, ever), or queues a priority-0 link and says why it is waiting.
    /// </summary>
    Task<PoolRequestResult> RequestLevelAsync(int depth, string instanceId, CancellationToken ct = default);

    /// <summary>
    /// The hand-out alone, inside the caller's transaction, so the level moves
    /// <c>ready → handed_out</c> in the same transaction as the instance's <c>requested</c> row.
    /// Null when there is none; the caller then calls <see cref="RequestFallbackAsync"/>.
    /// </summary>
    Task<LevelRecord?> HandOutAsync(IWriteTx tx, int depth, string instanceId);

    /// <summary>Queues (or raises to) a priority-0 link for an empty depth.</summary>
    Task RequestFallbackAsync(int depth, CancellationToken ct = default);

    /// <summary>The reap of the instance that ran the level: <c>handed_out → retired</c>.</summary>
    Task RetireLevelAsync(string hash, CancellationToken ct = default);
}
