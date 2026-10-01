using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SourceSharp.Host.Abstractions;

namespace SourceSharp.Host.Instances;

/// <summary>
/// The instance manager (plan §7.3–7.5). Every transition is persisted with its audit row
/// in one transaction BEFORE its side effect, so a restart resumes from the row; the
/// terminal transitions' side effects (hooks, pod delete) are marked done with a second
/// audit row ("instance.&lt;state&gt;.done") and re-run on start when that mark is missing.
///
/// Everything runs under one lock: the watch, the tick, and the API's calls. Hooks and
/// commands are called under it, so they must not call back into IInstanceLifecycle.
/// </summary>
public sealed class InstanceManager : BackgroundService, IInstanceLifecycle
{
    readonly IHostData _data;
    readonly IInstanceHost _host;
    readonly IInstanceCommands _commands;
    readonly IInstanceHooks _hooks;
    readonly ModImageGuard? _modGuard;
    readonly IOptionsMonitor<ServiceOptions> _options;
    readonly TimeProvider _clock;
    readonly ILogger _log;
    readonly SemaphoreSlim _lock = new(1, 1);

    // What the manager knows that is not a column, rebuilt on start: pods from the pod list; the
    // boot deadline from the "instance.creating" audit row; empty-since from the latest
    // "instance.empty" / "instance.occupied" audit edge; the reconnect window is fresh by design.
    // Hub failure history (backoff, stop-after-five) is not persisted: a restart starts it over.
    readonly Dictionary<string, PodStatus> _pods = [];
    readonly Dictionary<string, DateTimeOffset> _bootDeadline = [];
    readonly Dictionary<string, DateTimeOffset> _reconnectDeadline = [];
    readonly Dictionary<string, DateTimeOffset> _emptySince = [];
    readonly List<DateTimeOffset> _hubFailures = [];
    int _hubConsecutiveFailures;
    DateTimeOffset _hubNextAttempt = DateTimeOffset.MinValue;

    public const string Actor = "instance-manager";

    public InstanceManager(IHostData data, IInstanceHost host, IInstanceCommands commands, IInstanceHooks hooks,
        IOptionsMonitor<ServiceOptions> options, TimeProvider clock, ModImageGuard? modGuard = null, ILogger<InstanceManager>? log = null)
    {
        _data = data; _host = host; _commands = commands; _hooks = hooks; _options = options; _clock = clock; _modGuard = modGuard;
        _log = (ILogger?)log ?? NullLogger.Instance;
    }

    InstanceOptions O => _options.CurrentValue.Instances;
    ModOptions Mod => _options.CurrentValue.Mod;
    DateTimeOffset Now => _clock.GetUtcNow();

    /// <summary>Hub re-creation stopped after too many failures (audited as hub.recreate_stopped).</summary>
    public bool HubsStopped { get; private set; }

    /// <summary>When the next hub may be created (the backoff), for the admin and the facts.</summary>
    public DateTimeOffset HubNextAttempt => _hubNextAttempt;

    // ================================================================== hosting

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Start(ct);
        var watch = Task.Run(async () =>
        {
            await foreach (var e in _host.Watch(ct))
            {
                try { await Apply(e, ct); }
                catch (Exception ex) when (!ct.IsCancellationRequested) { _log.LogError(ex, "pod event {Pod} failed", e.Pod.Name); }
            }
        }, ct);
        while (!ct.IsCancellationRequested)
        {
            try { await Tick(ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { _log.LogError(ex, "instance tick failed"); }
            try { await Task.Delay(O.TickInterval, _clock, ct); }
            catch (OperationCanceledException) { break; }
        }
        try { await watch; } catch (OperationCanceledException) { }
    }

    /// <summary>
    /// §7.4 adoption, then the unfinished terminal side effects, then orphans, then hubs.
    /// For each non-terminal row with a pod: same name AND uid → kept (live pending the stream
    /// reconnect); no pod or another uid → crashed. A labelled pod no live row owns → deleted.
    /// </summary>
    public async Task Start(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var pods = await _host.List(GamePodLabels.Selector, ct);
            _pods.Clear();
            foreach (var p in pods) _pods[p.Name] = p;

            var rows = await _data.ReadAsync((tx, _) => tx.Instances.NonTerminal(), ct);
            foreach (var row in rows) await Adopt(row, ct);

            await ResumeUnfinished(ct);

            var owned = (await _data.ReadAsync((tx, _) => tx.Instances.NonTerminal(), ct))
                .Where(r => r.PodName is not null && r.PodUid is not null)
                .Select(r => (r.PodName!, r.PodUid!)).ToHashSet();
            foreach (var pod in pods.Where(p => !owned.Contains((p.Name, p.Uid))))
            {
                await _data.WriteAsync(async (tx, _) =>
                {
                    await tx.Audit.Write("instance.orphan_deleted", pod.Name, null, new { pod.Name, pod.Uid }, Actor);
                    return true;
                }, ct);
                await _host.Delete(pod.Name, pod.Uid, O.ReapGrace, ct);
                _pods.Remove(pod.Name);
            }

            await EnsureHubs(ct);
        }
        finally { _lock.Release(); }
    }

    async Task Adopt(InstanceRecord row, CancellationToken ct)
    {
        if (row.State is InstanceState.Requested or InstanceState.LevelReady) return; // no pod yet: creation proceeds on the tick

        var pod = row.PodName is { } name && _pods.TryGetValue(name, out var p) ? p : null;
        var same = pod is not null && row.PodUid is not null && pod.Uid == row.PodUid;
        if (!same)
        {
            var reason = pod is null ? "adoption: no pod" : $"adoption: pod {pod.Name} has uid {pod.Uid}, row has {row.PodUid ?? "none"}";
            await EndWithoutReap(row, InstanceState.Crashed, reason, null, ct);
            return;
        }
        if (pod!.Phase is PodPhase.Failed or PodPhase.Succeeded || pod.Restarts > 0)
        {
            if (row.State == InstanceState.Draining) await Reap(row, "adoption: pod exited while draining", failure: false, ct);
            else await EndWithoutReap(row, InstanceState.Crashed, $"adoption: pod {pod.Phase}", pod.ExitCode, ct);
            return;
        }

        await _data.WriteAsync(async (tx, _) =>
        {
            await tx.Audit.Write("instance.adopted", row.Id, null, new { state = row.State.ToString(), pod = pod.Name, uid = pod.Uid }, Actor);
            return true;
        }, ct);
        if (row.State is InstanceState.Creating or InstanceState.Booting)
        {
            // The boot clock started at the persisted "instance.creating" row; a restart does not
            // reset it, but never leaves the pod less than the reconnect window to reach us again.
            var creating = await LastAudit(row.Id, InstanceTransitions.Action(InstanceState.Creating), ct);
            var deadline = (creating?.At ?? Now) + O.BootTimeout;
            var floor = Now + O.AdoptReconnect;
            _bootDeadline[row.Id] = deadline > floor ? deadline : floor;
        }
        else
        {
            _reconnectDeadline[row.Id] = Now + O.AdoptReconnect;
            if (row.State == InstanceState.Live && row.Kind == InstanceKind.Level && row.Players == 0)
            {
                // Empty-since is the latest "instance.empty" edge, unless an "instance.occupied" edge followed it.
                var empty = await LastAudit(row.Id, EmptyAction, ct);
                var occupied = await LastAudit(row.Id, OccupiedAction, ct);
                _emptySince[row.Id] = empty is not null && (occupied is null || empty.Id > occupied.Id)
                    ? empty.At
                    : row.LiveAt ?? Now;
            }
        }
    }

    public const string EmptyAction = "instance.empty";
    public const string OccupiedAction = "instance.occupied";

    async Task<AuditEntry?> LastAudit(string id, string action, CancellationToken ct) =>
        (await _data.ReadAsync((tx, _) => tx.Audit.List(new AuditQuery(Action: action, Target: id, Take: 1)), ct)).FirstOrDefault();

    /// <summary>Starts the empty clock and records the edge, so a restarted service resumes the same grace.</summary>
    async Task MarkEmpty(string id, CancellationToken ct)
    {
        if (_emptySince.ContainsKey(id)) return;
        _emptySince[id] = Now;
        await Audit(EmptyAction, id, null, Actor, ct);
    }

    async Task MarkOccupied(string id, int players, CancellationToken ct)
    {
        if (!_emptySince.Remove(id)) return;
        await Audit(OccupiedAction, id, new { players }, Actor, ct);
    }

    /// <summary>Terminal rows whose side effects were not marked done (the service died mid-way): run them again.</summary>
    async Task ResumeUnfinished(CancellationToken ct)
    {
        foreach (var state in new[] { InstanceState.Reaped, InstanceState.Crashed, InstanceState.Failed })
        {
            var rows = await _data.ReadAsync((tx, _) => tx.Instances.List(state: state, take: 200), ct);
            foreach (var row in rows)
            {
                var action = InstanceTransitions.Action(state);
                var (started, done) = await _data.ReadAsync(async (tx, _) =>
                    (await tx.Audit.Count(new AuditQuery(Action: action, Target: row.Id)),
                     await tx.Audit.Count(new AuditQuery(Action: action + ".done", Target: row.Id))), ct);
                if (started == 0 || done > 0) continue;
                _log.LogWarning("instance {Id}: resuming {State} side effects", row.Id, state);
                if (state == InstanceState.Reaped) await ReapEffects(row, ct);
                else await EndEffects(row, ct);
            }
        }
    }

    // ================================================================== the tick

    volatile bool _stopping;

    /// <summary>
    /// The service is shutting down: its Connect streams have ended, so heartbeats stop
    /// arriving here while the pods keep running for the next service to adopt. From now on
    /// the tick judges nothing (no suspect, no reap, no boot timeout).
    /// </summary>
    public void Stopping() => _stopping = true;

    public async Task Tick(CancellationToken ct = default)
    {
        if (_stopping) return;
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureHubs(ct);
            var rows = await _data.ReadAsync((tx, _) => tx.Instances.NonTerminal(), ct);
            foreach (var row in rows)
            {
                try { await TickOne(row, ct); }
                catch (Exception e) when (!ct.IsCancellationRequested) { _log.LogError(e, "instance {Id} tick failed", row.Id); }
            }
        }
        finally { _lock.Release(); }
    }

    async Task TickOne(InstanceRecord row, CancellationToken ct)
    {
        var now = Now;
        switch (row.State)
        {
            case InstanceState.Requested when row.Kind == InstanceKind.Hub:
            case InstanceState.LevelReady:
                await CreatePod(row, ct);
                return;

            case InstanceState.Creating or InstanceState.Booting:
                if (_bootDeadline.TryGetValue(row.Id, out var deadline) && now >= deadline)
                    await EndWithoutReap(row, InstanceState.Failed, $"boot_timeout: no MapReady within {O.BootTimeout.TotalSeconds:0} s", null, ct);
                return;

            case InstanceState.Live or InstanceState.Suspect or InstanceState.Draining:
                if (_reconnectDeadline.TryGetValue(row.Id, out var reconnect))
                {
                    if (now < reconnect) return;
                    _reconnectDeadline.Remove(row.Id);
                    await EndWithoutReap(row, InstanceState.Crashed, "adoption: stream not reconnected", null, ct);
                    return;
                }
                var last = row.LastHeartbeat ?? row.LiveAt ?? row.DrainAt ?? now;
                var missed = (int)((now - last) / O.HeartbeatInterval);
                if (missed >= O.ReapAfterMissed)
                {
                    await Reap(row, $"heartbeats_lost: {missed} missed", failure: true, ct);
                    return;
                }
                if (row.State == InstanceState.Live && missed >= O.SuspectAfterMissed)
                {
                    await Transition(row.Id, InstanceState.Suspect, r => r, new { missed }, Actor, ct);
                    return;
                }
                if (row.State == InstanceState.Draining && row.Players == 0)
                {
                    await Reap(row, row.Reason ?? "drained", failure: false, ct);
                    return;
                }
                if (row.State == InstanceState.Live && row.Kind == InstanceKind.Level && row.Players == 0)
                    await CheckEmpty(row, now, ct);
                return;
        }
    }

    async Task CheckEmpty(InstanceRecord row, DateTimeOffset now, CancellationToken ct)
    {
        if (!_emptySince.TryGetValue(row.Id, out var since)) { await MarkEmpty(row.Id, ct); return; }
        var empty = now - since;
        if (empty < O.EmptyGrace) return;
        var grace = await _hooks.HasCorpses(row, ct) ? O.CorpseGrace : O.EmptyGrace;
        if (empty < grace) return;
        await DrainCore(row, grace == O.CorpseGrace ? "empty_with_corpse" : "empty", Actor, ct);
    }

    async Task EnsureHubs(CancellationToken ct)
    {
        if (HubsStopped || Now < _hubNextAttempt) return;
        var active = await _data.ReadAsync(async (tx, _) =>
            (await tx.Instances.NonTerminal()).Count(r => r.Kind == InstanceKind.Hub && r.State != InstanceState.Draining), ct);
        if (active >= O.Hubs) return;
        var row = await _data.WriteAsync(async (tx, _) =>
        {
            var r = await tx.Instances.Add(NewRow(InstanceKind.Hub, InstanceState.Requested, 0, null, null));
            await tx.Audit.Write(InstanceTransitions.Action(InstanceState.Requested), r.Id, null, new { kind = "hub" }, Actor);
            return r;
        }, ct);
        await CreatePod(row, ct);
    }

    InstanceRecord NewRow(InstanceKind kind, InstanceState state, int depth, string? party, string? hash) =>
        new(Ulid.New(_clock), kind, state, depth, party, hash, null, null, null, 0, "", null, null, null,
            BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)), Now, null, null, null, null, null, null, null, 0, O.GamePort);

    async Task CreatePod(InstanceRecord row, CancellationToken ct)
    {
        var o = O;
        if (o.VerifyModLabel && !o.SkipModInit && _modGuard is not null
            && await _modGuard.Refusal(o.ModImage, Mod.Name, ct) is { } refusal)
        {
            await Audit("instance.mod_image_refused", row.Id, new { image = o.ModImage.ToString(), mod = Mod.Name, refusal }, Actor, ct);
            await EndWithoutReap(row, InstanceState.Failed, refusal, null, ct);
            return;
        }

        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var name = GamePodBuilder.PodName(row.Kind, row.Id);
        row = await Transition(row.Id, InstanceState.Creating, r => r with
        {
            PodName = name,
            TokenHash = TokenHash(token),
            ModImage = o.SkipModInit ? null : o.ModImage.ToString(),
            ExpectedPort = o.GamePort,
        }, new { pod = name }, Actor, ct);
        _bootDeadline[row.Id] = Now + o.BootTimeout;

        PodRef pod;
        try { pod = await _host.Create(GamePodBuilder.Build(o, Mod, row, token), ct); }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            _log.LogError(e, "instance {Id}: pod create failed", row.Id);
            await EndWithoutReap(row, InstanceState.Failed, $"create_failed: {e.Message}", null, ct);
            return;
        }
        await Transition(row.Id, InstanceState.Booting, r => r with { PodUid = pod.Uid }, new { pod = pod.Name, uid = pod.Uid }, Actor, ct);
    }

    // ================================================================== pod events

    public async Task Apply(PodEvent e, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try { await ApplyCore(e, ct); }
        finally { _lock.Release(); }
    }

    async Task ApplyCore(PodEvent e, CancellationToken ct)
    {
        var pod = e.Pod;
        if (e.Type == PodEventType.Deleted) _pods.Remove(pod.Name);
        else _pods[pod.Name] = pod;

        if (!pod.Labels.TryGetValue(GamePodLabels.Instance, out var id)) return;
        var row = await _data.ReadAsync((tx, _) => tx.Instances.Get(id), ct);
        if (row is null || row.Terminal || row.PodName != pod.Name || row.PodUid != pod.Uid) return; // not this row's pod

        if (pod.Ip is not null && pod.Ip != row.PodIp)
            row = await _data.WriteAsync((tx, _) => tx.Instances.Update(id, r => r with { PodIp = pod.Ip }), ct);

        var gone = e.Type == PodEventType.Deleted || pod.Phase is PodPhase.Failed or PodPhase.Succeeded;
        if (gone || pod.Restarts > 0)
        {
            var why = e.Type == PodEventType.Deleted ? "pod_deleted" : pod.Restarts > 0 && !gone ? "container_restarted" : $"pod_{pod.Phase.ToString().ToLowerInvariant()}";
            if (row.State == InstanceState.Draining) await Reap(row, $"{why} while draining", failure: false, ct);
            else await EndWithoutReap(row, InstanceState.Crashed, why, pod.ExitCode, ct);
            return;
        }
        if (row.State == InstanceState.Booting && pod.Ready && row.Port > 0)
            await GoLive(row, ct);
    }

    bool PodReady(InstanceRecord row) =>
        row.PodName is { } n && _pods.TryGetValue(n, out var p) && p.Uid == row.PodUid && p.Ready;

    async Task GoLive(InstanceRecord row, CancellationToken ct)
    {
        row = await Transition(row.Id, InstanceState.Live, r => r with { LiveAt = Now, LastHeartbeat = Now }, new { port = row.Port }, Actor, ct);
        _bootDeadline.Remove(row.Id);
        if (row.Kind == InstanceKind.Level && row.Players == 0) await MarkEmpty(row.Id, ct);
        if (row.Kind == InstanceKind.Hub) _hubConsecutiveFailures = 0;
        await _hooks.OnLive(row, ct);
    }

    // ================================================================== terminal transitions

    /// <summary>reaped: persisted, then OnReaping → Delete(pod, uid, ReapGrace) → OnReaped, then marked done.</summary>
    async Task Reap(InstanceRecord row, string reason, bool failure, CancellationToken ct)
    {
        row = await Transition(row.Id, InstanceState.Reaped, r => r with { ReapedAt = Now, Reason = reason }, new { reason }, Actor, ct);
        Forget(row.Id);
        await ReapEffects(row, ct);
        if (row.Kind == InstanceKind.Hub && failure) await HubFailed(row, ct);
    }

    async Task ReapEffects(InstanceRecord row, CancellationToken ct)
    {
        await _hooks.OnReaping(row, ct);
        if (row.PodName is not null && row.PodUid is not null)
            await _host.Delete(row.PodName, row.PodUid, O.ReapGrace, ct);
        await _hooks.OnReaped(row, ct);
        await Audit(InstanceTransitions.Action(InstanceState.Reaped) + ".done", row.Id, null, Actor, ct);
    }

    /// <summary>crashed / failed: persisted, the pod deleted (uid-checked), OnCrashed, marked done; a hub counts a failure.</summary>
    async Task EndWithoutReap(InstanceRecord row, InstanceState to, string reason, int? exitCode, CancellationToken ct)
    {
        row = await Transition(row.Id, to, r => r with { Reason = reason, ExitCode = exitCode ?? r.ExitCode, ReapedAt = Now }, new { reason, exitCode }, Actor, ct);
        Forget(row.Id);
        _log.LogWarning("instance {Id} {State}: {Reason}", row.Id, to, reason);
        await EndEffects(row, ct);
        if (row.Kind == InstanceKind.Hub) await HubFailed(row, ct);
    }

    async Task EndEffects(InstanceRecord row, CancellationToken ct)
    {
        if (row.PodName is not null && row.PodUid is not null)
            await _host.Delete(row.PodName, row.PodUid, O.ReapGrace, ct);
        await _hooks.OnCrashed(row, ct);
        await Audit(InstanceTransitions.Action(row.State) + ".done", row.Id, null, Actor, ct);
    }

    void Forget(string id)
    {
        _bootDeadline.Remove(id);
        _reconnectDeadline.Remove(id);
        _emptySince.Remove(id);
    }

    /// <summary>§7.3: hub re-creation backs off 1 s … 30 s; five failures in five minutes stop it (audited).</summary>
    async Task HubFailed(InstanceRecord row, CancellationToken ct)
    {
        var now = Now;
        _hubFailures.Add(now);
        _hubFailures.RemoveAll(t => now - t >= O.HubFailureWindow);
        _hubConsecutiveFailures++;
        if (_hubFailures.Count >= O.HubFailureLimit)
        {
            HubsStopped = true;
            _log.LogCritical("hub re-creation stopped: {Count} failures within {Window}", _hubFailures.Count, O.HubFailureWindow);
            await Audit("hub.recreate_stopped", row.Id, new { failures = _hubFailures.Count, window = O.HubFailureWindow.ToString() }, Actor, ct);
            return;
        }
        _hubNextAttempt = now + HubBackoff(_hubConsecutiveFailures);
    }

    public TimeSpan HubBackoff(int consecutiveFailures)
    {
        var o = O;
        var ticks = o.HubBackoffMin.Ticks * Math.Pow(2, Math.Max(0, consecutiveFailures - 1));
        return ticks >= o.HubBackoffMax.Ticks ? o.HubBackoffMax : TimeSpan.FromTicks((long)ticks);
    }

    // ================================================================== persistence

    async Task<InstanceRecord> Transition(string id, InstanceState to, Func<InstanceRecord, InstanceRecord> change, object? detail, string actor, CancellationToken ct) =>
        await _data.WriteAsync(async (tx, _) =>
        {
            var before = await tx.Instances.Get(id) ?? throw HostRefusal.NotFound("unknown_instance", id);
            if (!InstanceTransitions.IsAllowed(before.State, to)) throw new InvalidInstanceTransition(id, before.State, to);
            var after = await tx.Instances.Update(id, r => change(r) with { State = to });
            await tx.Audit.Write(InstanceTransitions.Action(to), id, new { state = before.State.ToString() }, new { state = to.ToString(), detail }, actor);
            return after;
        }, ct);

    Task Audit(string action, string target, object? after, string actor, CancellationToken ct) =>
        _data.WriteAsync(async (tx, _) => { await tx.Audit.Write(action, target, null, after, actor); return true; }, ct);

    async Task<InstanceRecord> Row(string id, CancellationToken ct) =>
        await _data.ReadAsync((tx, _) => tx.Instances.Get(id), ct) ?? throw HostRefusal.NotFound("unknown_instance", id);

    public static string TokenHash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    // ================================================================== IInstanceLifecycle

    public async Task<LevelRequestResult> RequestLevel(int depth, string? partyId, string? levelHash, long? packVersion, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var max = O.MaxLevelPods;
            return await _data.WriteAsync<LevelRequestResult>(async (tx, _) =>
            {
                var running = await tx.Instances.CountLevels(
                    InstanceState.Requested, InstanceState.LevelReady, InstanceState.Creating, InstanceState.Booting,
                    InstanceState.Live, InstanceState.Suspect, InstanceState.Draining);
                if (running >= max) return new LevelRequestResult.AtCapacity(running, max);
                var state = levelHash is null ? InstanceState.Requested : InstanceState.LevelReady;
                var row = await tx.Instances.Add(NewRow(InstanceKind.Level, state, depth, partyId, levelHash));
                await tx.Audit.Write(InstanceTransitions.Action(InstanceState.Requested), row.Id, null, new { depth, partyId, levelHash, packVersion }, Actor);
                if (state == InstanceState.LevelReady)
                    await tx.Audit.Write(InstanceTransitions.Action(InstanceState.LevelReady), row.Id, new { state = "Requested" }, new { state = "LevelReady", detail = new { levelHash, packVersion } }, Actor);
                return new LevelRequestResult.Accepted(row);
            }, ct);
        }
        finally { _lock.Release(); }
    }

    public async Task<InstanceRecord> AssignLevel(string instanceId, string levelHash, long packVersion, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var row = await Row(instanceId, ct);
            if (row.State != InstanceState.Requested || row.Kind != InstanceKind.Level)
                throw HostRefusal.Precondition("not_requested", $"instance {instanceId} is {row.State}");
            return await Transition(instanceId, InstanceState.LevelReady, r => r with { LevelHash = levelHash }, new { levelHash, packVersion }, Actor, ct);
        }
        finally { _lock.Release(); }
    }

    public async Task<InstanceRecord?> Authenticate(string instanceId, string token, CancellationToken ct = default)
    {
        var row = await _data.ReadAsync((tx, _) => tx.Instances.Get(instanceId), ct);
        if (row is null || row.Terminal || row.TokenHash.Length == 0) return null;
        var presented = Encoding.ASCII.GetBytes(TokenHash(token));
        var stored = Encoding.ASCII.GetBytes(row.TokenHash);
        return CryptographicOperations.FixedTimeEquals(presented, stored) ? row : null;
    }

    public async Task<InstanceRecord> OnBooting(string instanceId, string rulesSha256, string? modImageDigest, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var row = await Row(instanceId, ct);
            if (row.State != InstanceState.Booting)
                throw HostRefusal.Precondition("not_booting", $"instance {instanceId} is {row.State}");
            return await _data.WriteAsync(async (tx, _) =>
            {
                var r = await tx.Instances.Update(instanceId, x => x with { RulesSha256 = rulesSha256, ModImageDigest = modImageDigest, Booted = Now });
                await tx.Audit.Write("instance.boot_reported", instanceId, null, new { rulesSha256, modImageDigest }, Actor);
                return r;
            }, ct);
        }
        finally { _lock.Release(); }
    }

    public async Task<MapReadyResult> OnMapReady(string instanceId, int port, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var row = await Row(instanceId, ct);
            if (row.State is InstanceState.Live or InstanceState.Suspect && row.Port == port)
                return new MapReadyResult(MapReadyOutcome.Live, row); // a retried call
            if (row.State != InstanceState.Booting)
                throw HostRefusal.Precondition("not_booting", $"instance {instanceId} is {row.State}");

            if (port != row.ExpectedPort)
            {
                // The sidecar reaches the engine on the expected port only: never accept another one silently.
                _log.LogError("instance {Id}: MapReady on port {Port}, expected {Expected}", instanceId, port, row.ExpectedPort);
                await _data.WriteAsync(async (tx, _) =>
                {
                    await tx.Instances.Update(instanceId, r => r with { Port = port });
                    await tx.Audit.Write("instance.port_mismatch", instanceId, null, new { expected = row.ExpectedPort, reported = port }, Actor);
                    return true;
                }, ct);
                await EndWithoutReap(await Row(instanceId, ct), InstanceState.Failed, $"port_mismatch: expected {row.ExpectedPort}, reported {port}", null, ct);
                return new MapReadyResult(MapReadyOutcome.PortMismatch, await Row(instanceId, ct));
            }

            row = await _data.WriteAsync(async (tx, _) =>
            {
                var r = await tx.Instances.Update(instanceId, x => x with { Port = port });
                await tx.Audit.Write("instance.map_ready", instanceId, null, new { port }, Actor);
                return r;
            }, ct);
            if (!PodReady(row)) return new MapReadyResult(MapReadyOutcome.AwaitingPodReady, row);
            await GoLive(row, ct);
            return new MapReadyResult(MapReadyOutcome.Live, await Row(instanceId, ct));
        }
        finally { _lock.Release(); }
    }

    public async Task OnStreamOpened(string instanceId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var row = await Row(instanceId, ct);
            if (row.Terminal) throw HostRefusal.Precondition("instance_ended", $"instance {instanceId} is {row.State}");
            if (_reconnectDeadline.Remove(instanceId))
            {
                await _data.WriteAsync(async (tx, _) =>
                {
                    await tx.Instances.Update(instanceId, r => r with { LastHeartbeat = Now });
                    await tx.Audit.Write("instance.reconnected", instanceId, null, new { state = row.State.ToString() }, Actor);
                    return true;
                }, ct);
            }
        }
        finally { _lock.Release(); }
    }

    public async Task OnHeartbeat(string instanceId, int players, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var row = await Row(instanceId, ct);
            if (row.Terminal) throw HostRefusal.Precondition("instance_ended", $"instance {instanceId} is {row.State}");
            _reconnectDeadline.Remove(instanceId);
            if (players > 0) await MarkOccupied(instanceId, players, ct);
            else if (row.State == InstanceState.Live && row.Kind == InstanceKind.Level) await MarkEmpty(instanceId, ct);

            if (row.State == InstanceState.Suspect)
                await Transition(instanceId, InstanceState.Live, r => r with { LastHeartbeat = Now, Players = players }, new { recovered = true }, Actor, ct);
            else
                await _data.WriteAsync((tx, _) => tx.Instances.Update(instanceId, r => r with { LastHeartbeat = Now, Players = players }), ct);
        }
        finally { _lock.Release(); }
    }

    public async Task OnStreamClosed(string instanceId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var row = await Row(instanceId, ct);
            if (row.Terminal || row.State is InstanceState.Draining or InstanceState.Requested or InstanceState.LevelReady) return;
            await EndWithoutReap(row, InstanceState.Crashed, "stream_closed", null, ct);
        }
        finally { _lock.Release(); }
    }

    public async Task<InstanceRecord> Drain(string instanceId, string reason, string actor, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var row = await Row(instanceId, ct);
            if (row.State == InstanceState.Draining) return row;
            if (row.Terminal) throw HostRefusal.Precondition("instance_ended", $"instance {instanceId} is {row.State}");
            return await DrainCore(row, reason, actor, ct);
        }
        finally { _lock.Release(); }
    }

    async Task<InstanceRecord> DrainCore(InstanceRecord row, string reason, string actor, CancellationToken ct)
    {
        row = await Transition(row.Id, InstanceState.Draining, r => r with { DrainAt = Now, Reason = reason }, new { reason }, actor, ct);
        _emptySince.Remove(row.Id);
        _bootDeadline.Remove(row.Id);
        await _commands.Send(row.Id, new InstanceCommand.Drain(reason), ct);
        return row;
    }

    public async Task Kick(string instanceId, string steamId, string reason, string actor, CancellationToken ct = default)
    {
        await Audit("instance.kick", instanceId, new { steamId, reason }, actor, ct);
        if (!await _commands.Send(instanceId, new InstanceCommand.Kick(steamId, reason), ct))
            throw HostRefusal.Precondition("no_stream", $"instance {instanceId} has no open stream");
    }

    public async Task Exec(string instanceId, string command, string actor, CancellationToken ct = default)
    {
        await Audit("instance.exec", instanceId, new { command }, actor, ct);
        if (!await _commands.Send(instanceId, new InstanceCommand.Exec(command), ct))
            throw HostRefusal.Precondition("no_stream", $"instance {instanceId} has no open stream");
    }

    public async Task ResumeHubs(string actor, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            HubsStopped = false;
            _hubFailures.Clear();
            _hubConsecutiveFailures = 0;
            _hubNextAttempt = DateTimeOffset.MinValue;
            await Audit("hub.recreate_resumed", "hub", null, actor, ct);
        }
        finally { _lock.Release(); }
    }
}
