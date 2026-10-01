using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;

namespace SourceSharp.Host.MapPool;

/// <summary>
/// The pool worker (plan §9.3, TF2 plan 7c): keeps K ready levels per depth of the pack version
/// assigned to that depth, orders the work by priority, links with bounded concurrency, retries
/// a failed link with a fresh seed, hands out a level once ever, and collects old files.
/// </summary>
/// <remarks>
/// Priorities (lower first): 0 fallback for an empty depth someone asked for (and a depth whose
/// old levels were retired at once), 1 depth N+1 for every party on N, 2 depths the church
/// offers, 3 the rest, shallowest first (queued in depth order). A bake outranks every link
/// (the job store orders it first).
/// </remarks>
public sealed class MapPoolWorker(
    IHostData data,
    IRulesProvider rules,
    IPoolDemand demand,
    PackCatalog catalog,
    LinkDriver driver,
    ServiceOptions options,
    MapPoolLayoutOptions layout,
    TimeProvider time,
    PoolSignal signal,
    IBakeLauncher? baker = null,
    ILogger<MapPoolWorker>? log = null) : BackgroundService, IMapPool
{
    readonly ILogger _log = (ILogger?)log ?? NullLogger.Instance;
    string Mod => options.Mod.Name;
    MapPoolOptions Pool => options.MapPool;

    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan FailedRetention = TimeSpan.FromDays(7);

    volatile PoolStatus _status = PoolStatus.Starting;
    public PoolStatus Status => _status;

    // ---- travel and reap -------------------------------------------------------------

    public async Task<PoolRequestResult> RequestLevelAsync(int depth, string instanceId, CancellationToken ct = default)
    {
        var level = await data.WriteAsync((tx, _) => HandOutAsync(tx, depth, instanceId), ct);
        if (level is not null)
        {
            signal.Pulse();
            return new PoolRequestResult(level, null);
        }
        await RequestFallbackAsync(depth, ct);
        var why = rules.Current is null ? PoolStatus.WaitingForRulesMessage
            : await data.ReadAsync((tx, _) => tx.Library.Assignment(Mod, depth), ct) is null ? $"no pack is assigned to depth {depth}"
            : $"depth {depth} has no ready level; one is being linked";
        return new PoolRequestResult(null, why);
    }

    public async Task<LevelRecord?> HandOutAsync(IWriteTx tx, int depth, string instanceId)
    {
        var a = await tx.Library.Assignment(Mod, depth);
        if (a is null) return null;
        if (await tx.Levels.HandOut(depth, a.PackId, instanceId) is { } level) return level;
        // Drain (§9.4): the old version's ready levels hand out until the new version's exist.
        var old = (await tx.Levels.List(depth: depth, state: LevelState.Ready)).Select(l => l.PackId).Where(p => p != a.PackId).Distinct();
        foreach (var packId in old)
            if (await tx.Levels.HandOut(depth, packId, instanceId) is { } drained) return drained;
        return null;
    }

    public async Task RequestFallbackAsync(int depth, CancellationToken ct = default)
    {
        await data.WriteAsync(async (tx, _) =>
        {
            var a = await tx.Library.Assignment(Mod, depth);
            if (a is null) return false;
            var live = await LiveLinkJobs(tx);
            // Raise a queued job of this depth to 0, or queue one.
            var queued = live.FirstOrDefault(j => j.State == MapJobState.Queued && KeyOf(j.Key) is { } k && k.Depth == depth && k.PackId == a.PackId);
            await tx.MapJobs.Enqueue(MapJobKind.Link, queued?.Key ?? FreeKey(live, depth, a.PackId), 0);
            return true;
        }, ct);
        signal.Pulse();
    }

    public async Task RetireLevelAsync(string hash, CancellationToken ct = default) =>
        await data.WriteAsync(async (tx, _) =>
        {
            var l = await tx.Levels.Get(hash);
            if (l is null || l.State != LevelState.HandedOut) return false;
            await tx.Levels.SetState(hash, LevelState.Retired);
            return true;
        }, ct);

    // ---- planning ------------------------------------------------------------------------

    public sealed record LinkKey(int Depth, long PackId, int Slot);

    public static string LinkJobKey(string mod, int depth, long packId, int slot) => $"link:{mod}:{depth}:{packId}:{slot}";

    LinkKey? KeyOf(string key)
    {
        var p = key.Split(':');
        return p.Length == 5 && p[0] == "link" && p[1] == Mod && int.TryParse(p[2], out var d) && long.TryParse(p[3], out var pk) && int.TryParse(p[4], out var s)
            ? new LinkKey(d, pk, s) : null;
    }

    string FreeKey(IReadOnlyList<MapJob> live, int depth, long packId)
    {
        var used = live.Select(j => KeyOf(j.Key)).OfType<LinkKey>().Where(k => k.Depth == depth && k.PackId == packId).Select(k => k.Slot).ToHashSet();
        var slot = 0;
        while (used.Contains(slot)) slot++;
        return LinkJobKey(Mod, depth, packId, slot);
    }

    static async Task<IReadOnlyList<MapJob>> LiveLinkJobs(IReadTx tx) =>
        [.. (await tx.MapJobs.List(MapJobState.Queued, 10_000)).Concat(await tx.MapJobs.List(MapJobState.Running, 10_000)).Where(j => j.Kind == MapJobKind.Link)];

    /// <summary>
    /// One planning pass: status, the old-levels drain, and a link job per missing level.
    /// Does nothing but say so until the hub's rules module is known.
    /// </summary>
    public async Task PlanAsync(CancellationToken ct = default)
    {
        if (rules.Current is null)
        {
            _status = new PoolStatus(true, PoolStatus.WaitingForRulesMessage, [], time.GetUtcNow());
            return;
        }
        var parties = demand.PartyDepths().ToHashSet();
        var selectable = demand.SelectableDepths().ToHashSet();
        var depths = await data.WriteAsync(async (tx, _) =>
        {
            var live = (await LiveLinkJobs(tx)).ToList();
            var assignments = (await tx.Library.Assignments(Mod)).ToDictionary(a => a.Depth);
            var wanted = new List<(int Priority, int Depth, long PackId, int Count)>();
            var status = new List<DepthStatus>();
            for (var depth = 1; depth <= Pool.Depths; depth++)
            {
                var k = Pool.PerDepthFor(depth);
                if (!assignments.TryGetValue(depth, out var a))
                {
                    status.Add(new DepthStatus(depth, null, null, k, 0, 0, 0, "no pack assigned"));
                    continue;
                }
                var pack = await tx.Library.GetPack(a.PackId);
                var ready = await tx.Levels.Count(depth, a.PackId, LevelState.Ready);
                var inflight = live.Count(j => KeyOf(j.Key) is { } key && key.Depth == depth && key.PackId == a.PackId);
                var old = (await tx.Levels.List(depth: depth, state: LevelState.Ready)).Where(l => l.PackId != a.PackId).OrderBy(l => l.Created).ToList();

                // Drain: the old version's levels retire as the new version's meet K.
                var retire = Math.Min(old.Count, Math.Max(0, ready + old.Count - k));
                foreach (var l in old.Take(retire)) await tx.Levels.SetState(l.Hash, LevelState.Retired, "drained: the assigned version's levels meet K");

                // Jobs for a version no longer assigned are dropped when they run; count only this version's.
                var deficit = k - ready - inflight;
                var priority = ready == 0 && a.OldLevels == OldLevelsPolicy.Retire ? 0
                    : parties.Contains(depth - 1) ? 1 : selectable.Contains(depth) ? 2 : 3;
                if (deficit > 0) wanted.Add((priority, depth, a.PackId, deficit));
                status.Add(new DepthStatus(depth, a.PackId, pack?.Version, k, ready, inflight + Math.Max(0, deficit), old.Count - retire, null));
            }
            foreach (var (priority, depth, packId, count) in wanted.OrderBy(w => w.Priority).ThenBy(w => w.Depth))
                for (var i = 0; i < count; i++)
                    live.Add(await tx.MapJobs.Enqueue(MapJobKind.Link, FreeKey(live, depth, packId), priority));
            return status;
        }, ct);
        _status = new PoolStatus(false, "linking", depths, time.GetUtcNow());
        await WatchLibrariesAsync(ct);
    }

    /// <summary>A bake when a library's source changed: its version (rooms.vmf + library.json) has no pack yet.</summary>
    async Task WatchLibrariesAsync(CancellationToken ct)
    {
        if (baker is null || !Directory.Exists(Pool.LibraryPath)) return;
        foreach (var dir in Directory.GetDirectories(Pool.LibraryPath).Order(StringComparer.Ordinal))
        {
            var source = new LibrarySource(Mod, Path.GetFileName(dir), dir);
            if (!File.Exists(source.Vmf)) continue;
            var version = MapIdentity.LibraryVersionOf(source);
            await data.WriteAsync(async (tx, _) =>
            {
                var known = (await tx.Library.Packs(Mod, source.Library)).Any(p => p.LibraryVersion == version);
                var failed = (await tx.MapJobs.List(MapJobState.Failed, 200)).Any(j => j.Key == BakeKey(source.Library, version));
                if (!known && !failed) await tx.MapJobs.Enqueue(MapJobKind.Bake, BakeKey(source.Library, version), 0);
                return true;
            }, ct);
        }
    }

    public string BakeKey(string library, string version) => $"bake:{Mod}:{library}:{version}";

    // ---- running jobs ----------------------------------------------------------------------

    /// <summary>Takes queued jobs and runs them, at most <c>ConcurrentLinks</c> links at once, until none is queued.</summary>
    public async Task RunPendingAsync(CancellationToken ct = default)
    {
        var running = new List<Task>();
        while (true)
        {
            while (running.Count < Pool.ConcurrentLinks)
            {
                var job = await data.WriteAsync((tx, _) => tx.MapJobs.TakeNext(), ct);
                if (job is null) break;
                running.Add(job.Kind == MapJobKind.Bake ? RunBakeAsync(job, ct) : RunLinkAsync(job, ct));
            }
            if (running.Count == 0) return;
            var done = await Task.WhenAny(running);
            running.Remove(done);
            await done;
        }
    }

    /// <summary>The seed of a link attempt: fresh per attempt, so a retry lays out another level.</summary>
    public static ulong SeedFor(long jobId, int attempt) =>
        BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes($"link-seed|{jobId}|{attempt}")));

    async Task RunLinkAsync(MapJob job, CancellationToken ct)
    {
        var key = KeyOf(job.Key);
        if (key is null) { await Finish(job, "not a link key", fail: true, requeue: false, ct); return; }
        var (pack, assignment) = await data.ReadAsync(async (tx, _) =>
            (await tx.Library.GetPack(key.PackId), await tx.Library.Assignment(Mod, key.Depth)), ct);
        if (pack is null || pack.State != PackState.Ready || assignment?.PackId != pack.Id)
        {
            await Finish(job, $"dropped: depth {key.Depth} no longer links from pack {key.PackId}", fail: false, requeue: false, ct);
            return;
        }
        var module = rules.Current;
        if (module is null) { await Finish(job, PoolStatus.WaitingForRulesMessage, fail: true, requeue: true, ct); return; }

        var seed = SeedFor(job.Id, job.Attempts);
        var rooms = catalog.RoomsOf(pack);
        var hash = driver.HashOf(pack, key.Depth, seed, layout.Difficulty);
        try
        {
            var plan = layout.ToolLayout ? null : module.GenerateLayout(new LayoutKey(pack.Library, key.Depth, layout.Difficulty, seed, rooms));
            var stored = await driver.LinkAsync(pack, catalog.PathOf(pack), key.Depth, seed, plan, rooms, ct);
            await data.WriteAsync(async (tx, _) =>
            {
                await tx.Levels.Add(new LevelRecord(stored.Hash, pack.Id, key.Depth, pack.Library, seed, layout.Difficulty, LevelState.Ready,
                    tx.Now, null, stored.Bytes, null, rules.CurrentSha256));
                await tx.MapJobs.Complete(job.Id, $"{stored.MapName} ({stored.Bytes} bytes, {stored.LinkTime.TotalMilliseconds:F0} ms)\n{stored.Log}");
                return true;
            }, ct);
            signal.Pulse();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var why = e is MapCompileFailure f ? f.Message + "\n" + f.Log : e.ToString();
            _log.LogWarning("link {Key} attempt {Attempt} (seed {Seed}) failed: {Why}", job.Key, job.Attempts, seed, e.Message);
            await data.WriteAsync(async (tx, _) =>
            {
                if (await tx.Levels.Get(hash) is null)
                    await tx.Levels.Add(new LevelRecord(hash, pack.Id, key.Depth, pack.Library, seed, layout.Difficulty, LevelState.Failed,
                        tx.Now, null, 0, why, rules.CurrentSha256));
                await tx.MapJobs.Fail(job.Id, $"attempt {job.Attempts} seed {seed}: {why}", requeue: job.Attempts < Pool.LinkAttempts);
                return true;
            }, ct);
        }
    }

    async Task RunBakeAsync(MapJob job, CancellationToken ct)
    {
        var parts = job.Key.Split(':');
        if (baker is null || parts.Length < 3) { await Finish(job, "no bake launcher", fail: true, requeue: false, ct); return; }
        try
        {
            var log = await baker.BakeAsync(parts[1], parts[2], ct);
            await Finish(job, log, fail: false, requeue: false, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await Finish(job, e.Message, fail: true, requeue: false, ct);
        }
    }

    Task Finish(MapJob job, string log, bool fail, bool requeue, CancellationToken ct) =>
        data.WriteAsync(async (tx, _) => fail ? await tx.MapJobs.Fail(job.Id, log, requeue) : await tx.MapJobs.Complete(job.Id, log), ct);

    // ---- GC ----------------------------------------------------------------------------------

    /// <summary>
    /// Retired levels beyond the newest <c>KeepRetiredPerDepth</c> per depth lose their files;
    /// failed levels older than 7 days too; then unreferenced retired pack files. Returns the
    /// map names whose files were deleted.
    /// </summary>
    public async Task<IReadOnlyList<string>> CollectAsync(CancellationToken ct = default)
    {
        var (retired, failed) = await data.ReadAsync(async (tx, _) =>
            (await All(tx, LevelState.Retired), await All(tx, LevelState.Failed)), ct);
        var deleted = new List<string>();
        foreach (var group in retired.GroupBy(l => l.Depth))
            foreach (var l in group.OrderByDescending(l => l.Created).ThenByDescending(l => l.Hash).Skip(Pool.KeepRetiredPerDepth))
                if (catalog.Storage.DeleteLevel(MapIdentity.MapName(Mod, l.Depth, l.Hash)) > 0) deleted.Add(MapIdentity.MapName(Mod, l.Depth, l.Hash));
        var cutoff = time.GetUtcNow() - FailedRetention;
        foreach (var l in failed.Where(l => l.Created < cutoff))
            if (catalog.Storage.DeleteLevel(MapIdentity.MapName(Mod, l.Depth, l.Hash)) > 0) deleted.Add(MapIdentity.MapName(Mod, l.Depth, l.Hash));
        if (deleted.Count > 0)
            await data.WriteAsync(async (tx, _) => { await tx.Audit.Write("level.gc", Mod, null, new { deleted }, "mappool"); return true; }, ct);
        await catalog.CollectAsync(Mod, ct);
        return deleted;
    }

    static async Task<List<LevelRecord>> All(IReadTx tx, LevelState state)
    {
        var all = new List<LevelRecord>();
        for (var skip = 0; ; skip += 500)
        {
            var page = await tx.Levels.List(state: state, skip: skip, take: 500);
            all.AddRange(page);
            if (page.Count < 500) return all;
        }
    }

    // ---- the service loop -----------------------------------------------------------------

    /// <summary>A job left running by a previous process never finishes: queue it again (start of the service loop).</summary>
    public Task<int> RequeueOrphansAsync(CancellationToken ct = default) =>
        data.WriteAsync(async (tx, _) =>
        {
            var orphans = await tx.MapJobs.List(MapJobState.Running, 10_000);
            foreach (var j in orphans) await tx.MapJobs.Fail(j.Id, "the service restarted while this job ran", requeue: true);
            return orphans.Count;
        }, ct);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var lastGc = DateTimeOffset.MinValue;
        await RequeueOrphansAsync(ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PlanAsync(ct);
                if (rules.Current is not null) await RunPendingAsync(ct);
                if (time.GetUtcNow() - lastGc > TimeSpan.FromMinutes(10)) { await CollectAsync(ct); lastGc = time.GetUtcNow(); }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogError(e, "map pool pass failed");
            }
            await signal.WaitAsync(TickInterval, time, ct);
        }
    }
}
