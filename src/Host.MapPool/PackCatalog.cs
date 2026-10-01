using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;

namespace SourceSharp.Host.MapPool;

/// <summary>The outcome of an upload: the row (Ready or Rejected) and the validation behind it.</summary>
public sealed record PackUploadResult(PackRecord Pack, PackInspection Inspection)
{
    public bool Accepted => Pack.State == PackState.Ready;
}

/// <summary>Where each depth links from, one row per depth, as the admin's matrix shows it.</summary>
public sealed record AssignmentMatrix(IReadOnlyList<PackAssignment> Rows);

/// <summary>Wakes the pool worker when something it plans from changed (an assignment, a new pack, a hand-out).</summary>
public sealed class PoolSignal
{
    readonly SemaphoreSlim _s = new(0, 1);
    public void Pulse() { try { _s.Release(); } catch (SemaphoreFullException) { } }
    public async Task WaitAsync(TimeSpan timeout, TimeProvider time, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = Task.Delay(timeout, time, cts.Token);
        var signal = _s.WaitAsync(cts.Token);
        await Task.WhenAny(delay, signal);
        await cts.CancelAsync();
        ct.ThrowIfCancellationRequested();
    }
}

/// <summary>
/// Room packs at run time (plan §9.4): uploads and bakes become versions through one
/// validation; the admin assigns versions to depths (activate, pin, roll back) and retires
/// them; every change is audited with the before/after matrix. A <c>ready</c> version never
/// changes on disk, and a <c>handed_out</c> level is never touched.
/// </summary>
/// <remarks>
/// Rejected uploads are kept as rows (the report stays) with negative versions: the packs
/// table's (mod, library, version) index is unique, and "a valid upload becomes version N+1"
/// counts ready versions only.
/// </remarks>
public sealed partial class PackCatalog(IHostData data, IPackValidator validator, LevelStorage storage, ServiceOptions options,
    PoolSignal? signal = null)
{
    public const string SourceUpload = "upload", SourceBake = "bake";
    readonly SemaphoreSlim _uploads = new(1, 1);

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex Key();

    public LevelStorage Storage => storage;

    public string PathOf(PackRecord p) => storage.PackFile(p.Mod, p.Library, p.Version);
    string ManifestOf(PackRecord p) => Path.ChangeExtension(PathOf(p), ".library.json");
    string RoomsFileOf(PackRecord p) => Path.ChangeExtension(PathOf(p), ".rooms.json");

    /// <summary>The rooms of a version as its library.json described them at upload (for <see cref="LayoutKey"/>).</summary>
    public IReadOnlyList<RoomInfo> RoomsOf(PackRecord p) =>
        File.Exists(RoomsFileOf(p)) ? JsonSerializer.Deserialize<List<RoomInfo>>(File.ReadAllText(RoomsFileOf(p))) ?? [] : [];

    /// <summary>
    /// Streams a pack to <c>packs/&lt;mod&gt;/&lt;library&gt;/&lt;version&gt;.roompack.uploading</c>, validates it,
    /// and keeps it as the next version or rejects it (file deleted, report kept).
    /// </summary>
    /// <param name="libraryJson">The game's metadata; null takes the previous ready version's.</param>
    public async Task<PackUploadResult> UploadAsync(string mod, string library, Stream content, string? libraryJson, string? note,
        string source, string actor, CancellationToken ct = default)
    {
        if (!Key().IsMatch(mod) || !Key().IsMatch(library))
            throw new HostRefusal(RefusalCode.InvalidArgument, "bad_key", $"mod '{mod}' and library '{library}' must be lower-case keys");

        await _uploads.WaitAsync(ct);
        try
        {
            var existing = await data.ReadAsync((tx, _) => tx.Library.Packs(mod, library), ct);
            var version = existing.Where(p => p.Version > 0).Select(p => p.Version).DefaultIfEmpty(0).Max() + 1;
            var final = storage.PackFile(mod, library, version);
            var uploading = final + ".uploading";
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);

            long bytes;
            string sha;
            try
            {
                (bytes, sha) = await CopyLimitedAsync(content, uploading, options.Admin.MaxUploadBytes, ct);
            }
            catch
            {
                File.Delete(uploading);
                throw;
            }

            var previous = existing.Where(p => p.State is PackState.Ready or PackState.Retired && p.Version > 0).MaxBy(p => p.Version);
            var json = libraryJson ?? (previous is not null && File.Exists(ManifestOf(previous)) ? await File.ReadAllTextAsync(ManifestOf(previous), ct) : null);

            PackInspection inspection;
            try
            {
                inspection = await validator.ValidateAsync(new PackCandidate(mod, library, uploading, json), ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                inspection = new PackInspection([$"validation failed: {e.Message}"], null, null, [], JsonSerializer.Serialize(new { error = e.Message }), TimeSpan.Zero);
            }

            if (!inspection.Ok)
            {
                File.Delete(uploading);
                var rejectedVersion = -(existing.Count(p => p.Version < 0) + 1);
                var row = await data.WriteAsync(async (tx, _) =>
                {
                    var r = await tx.Library.AddPack(new PackRecord(0, mod, library, rejectedVersion, inspection.PackId ?? "", inspection.LibraryVersion ?? "",
                        source, actor, tx.Now, bytes, sha, inspection.ReportJson, note, PackState.Rejected));
                    await tx.Audit.Write("pack.rejected", $"{mod}/{library}", null, new { r.Id, source, problems = inspection.Problems }, actor);
                    return r;
                }, ct);
                return new PackUploadResult(row, inspection);
            }

            File.Move(uploading, final, overwrite: false);
            if (json is not null) await File.WriteAllTextAsync(ManifestOf(PackRecordPath(mod, library, version)), json, ct);
            await File.WriteAllTextAsync(RoomsFileOf(PackRecordPath(mod, library, version)), JsonSerializer.Serialize(inspection.Rooms), ct);
            var ready = await data.WriteAsync(async (tx, _) =>
            {
                var r = await tx.Library.AddPack(new PackRecord(0, mod, library, version, inspection.PackId ?? "", inspection.LibraryVersion ?? "",
                    source, actor, tx.Now, bytes, sha, inspection.ReportJson, note, PackState.Ready));
                await tx.Audit.Write("pack.added", $"{mod}/{library}/{version}", null,
                    new { r.Id, source, r.Sha256, r.Bytes, trialLinkMs = (long)inspection.TrialLink.TotalMilliseconds }, actor);
                if (source == SourceBake && options.MapPool.AutoActivateBakes)
                    await AutoActivate(tx, r, actor);
                return r;
            }, ct);
            signal?.Pulse();
            return new PackUploadResult(ready, inspection);
        }
        finally
        {
            _uploads.Release();
        }
    }

    /// <summary>A path-only stand-in: the files beside a version are named before its row exists.</summary>
    static PackRecord PackRecordPath(string mod, string library, int version) =>
        new(0, mod, library, version, "", "", "", "", DateTimeOffset.MinValue, 0, "", null, null, PackState.Validating);

    static async Task<(long Bytes, string Sha)> CopyLimitedAsync(Stream content, string path, long limit, CancellationToken ct)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var output = File.Create(path);
        var buffer = new byte[81920];
        long total = 0;
        int n;
        while ((n = await content.ReadAsync(buffer, ct)) > 0)
        {
            total += n;
            if (total > limit)
                throw HostRefusal.Exhausted("too_large", $"the pack is over Admin.MaxUploadBytes ({limit} bytes)");
            sha.AppendData(buffer, 0, n);
            await output.WriteAsync(buffer.AsMemory(0, n), ct);
        }
        return (total, Convert.ToHexStringLower(sha.GetHashAndReset()));
    }

    /// <summary>AutoActivateBakes: a bake's new version moves every unpinned depth that links from this library, and every unassigned depth.</summary>
    async Task AutoActivate(IWriteTx tx, PackRecord pack, string actor)
    {
        var before = await tx.Library.Assignments(pack.Mod);
        var libraryOf = new Dictionary<long, string>();
        foreach (var a in before)
            libraryOf[a.PackId] = (await tx.Library.GetPack(a.PackId))?.Library ?? "";
        var depths = Enumerable.Range(1, options.MapPool.Depths)
            .Where(d => before.FirstOrDefault(a => a.Depth == d) is not { } a || (!a.Pinned && libraryOf[a.PackId] == pack.Library))
            .ToList();
        if (depths.Count == 0) return;
        foreach (var d in depths)
            await tx.Library.Assign(new PackAssignment(pack.Mod, d, pack.Id, false, actor, tx.Now, OldLevelsPolicy.Drain));
        await tx.Audit.Write("pack.auto_activate", $"{pack.Mod}/{pack.Library}/{pack.Version}", Matrix(before), Matrix(await tx.Library.Assignments(pack.Mod)), actor);
    }

    static object Matrix(IReadOnlyList<PackAssignment> rows) =>
        rows.Select(a => new { a.Depth, a.PackId, a.Pinned, a.OldLevels }).ToList();

    public Task<AssignmentMatrix> MatrixAsync(string mod, CancellationToken ct = default) =>
        data.ReadAsync(async (tx, _) => new AssignmentMatrix(await tx.Library.Assignments(mod)), ct);

    /// <summary>
    /// Activate a ready version on <paramref name="depths"/>: new links use it. Pinned (the
    /// default) means a later upload or bake does not move it. The old-levels policy applies
    /// to ready levels of other versions at those depths; handed-out levels are never touched.
    /// </summary>
    public async Task<AssignmentMatrix> ActivateAsync(string mod, IReadOnlyCollection<int> depths, long packId, string actor,
        bool pinned = true, OldLevelsPolicy policy = OldLevelsPolicy.Drain, string action = "pack.activate", CancellationToken ct = default)
    {
        var result = await data.WriteAsync(async (tx, _) =>
        {
            var pack = await tx.Library.GetPack(packId) ?? throw HostRefusal.NotFound("no_pack", packId.ToString());
            if (pack.Mod != mod) throw HostRefusal.Precondition("wrong_mod", $"pack {packId} is for mod '{pack.Mod}'");
            if (pack.State != PackState.Ready) throw HostRefusal.Precondition("pack_not_ready", $"pack {packId} is {pack.State}");
            CheckDepths(depths);
            var before = await tx.Library.Assignments(mod);
            if (action == "pack.rollback")
                foreach (var d in depths)
                {
                    var current = before.FirstOrDefault(a => a.Depth == d) ?? throw HostRefusal.Precondition("nothing_assigned", $"depth {d} has no version to roll back from");
                    var cur = await tx.Library.GetPack(current.PackId);
                    if (cur is null || cur.Library != pack.Library || pack.Version >= cur.Version)
                        throw HostRefusal.Precondition("not_older", $"version {pack.Version} is not older than depth {d}'s version {cur?.Version} of '{pack.Library}'");
                }
            foreach (var d in depths)
            {
                await tx.Library.Assign(new PackAssignment(mod, d, pack.Id, pinned, actor, tx.Now, policy));
                if (policy == OldLevelsPolicy.Retire)
                    foreach (var l in await tx.Levels.List(depth: d, state: LevelState.Ready))
                        if (l.PackId != pack.Id) await tx.Levels.SetState(l.Hash, LevelState.Retired, $"retired by {action} of pack {pack.Id}");
            }
            var after = await tx.Library.Assignments(mod);
            await tx.Audit.Write(action, $"{mod}/{pack.Library}/{pack.Version}", Matrix(before), Matrix(after), actor);
            return new AssignmentMatrix(after);
        }, ct);
        signal?.Pulse();
        return result;
    }

    /// <summary>Roll back: activate an older ready version of the depth's current library.</summary>
    public Task<AssignmentMatrix> RollBackAsync(string mod, IReadOnlyCollection<int> depths, long olderPackId, string actor,
        OldLevelsPolicy policy = OldLevelsPolicy.Drain, CancellationToken ct = default) =>
        ActivateAsync(mod, depths, olderPackId, actor, pinned: true, policy, "pack.rollback", ct);

    /// <summary>Pin or unpin the depths' current assignment without changing the version.</summary>
    public async Task<AssignmentMatrix> PinAsync(string mod, IReadOnlyCollection<int> depths, bool pinned, string actor, CancellationToken ct = default) =>
        await data.WriteAsync(async (tx, _) =>
        {
            CheckDepths(depths);
            var before = await tx.Library.Assignments(mod);
            foreach (var d in depths)
            {
                var a = before.FirstOrDefault(x => x.Depth == d) ?? throw HostRefusal.Precondition("nothing_assigned", $"depth {d} has no version");
                await tx.Library.Assign(a with { Pinned = pinned, AssignedBy = actor, AssignedAt = tx.Now });
            }
            var after = await tx.Library.Assignments(mod);
            await tx.Audit.Write(pinned ? "pack.pin" : "pack.unpin", mod, Matrix(before), Matrix(after), actor);
            return new AssignmentMatrix(after);
        }, ct);

    /// <summary>Retire a version: refused while any depth is assigned to it; its ready levels retire with it.</summary>
    public async Task<PackRecord> RetireAsync(long packId, string actor, CancellationToken ct = default)
    {
        var r = await data.WriteAsync(async (tx, _) =>
        {
            var pack = await tx.Library.GetPack(packId) ?? throw HostRefusal.NotFound("no_pack", packId.ToString());
            var using_ = (await tx.Library.Assignments(pack.Mod)).Where(a => a.PackId == packId).Select(a => a.Depth).ToList();
            if (using_.Count > 0)
                throw HostRefusal.Precondition("pack_assigned", $"pack {packId} is assigned to depth(s) {string.Join(", ", using_)}; assign another version first");
            var retired = await tx.Library.SetPackState(packId, PackState.Retired);
            foreach (var l in await tx.Levels.List(state: LevelState.Ready, packId: packId))
                await tx.Levels.SetState(l.Hash, LevelState.Retired, $"pack {packId} retired");
            await tx.Audit.Write("pack.retire", $"{pack.Mod}/{pack.Library}/{pack.Version}", new { pack.State }, new { retired.State }, actor);
            return retired;
        }, ct);
        signal?.Pulse();
        return r;
    }

    /// <summary>
    /// GC of pack files: a retired version's file goes only when no ready or handed-out level
    /// references it and no assignment names it. Returns the versions whose files were deleted.
    /// </summary>
    public async Task<IReadOnlyList<long>> CollectAsync(string mod, CancellationToken ct = default)
    {
        var (packs, assigned, referenced) = await data.ReadAsync(async (tx, _) =>
        {
            var p = await tx.Library.Packs(mod);
            var a = (await tx.Library.Assignments(mod)).Select(x => x.PackId).ToHashSet();
            var refd = new HashSet<long>();
            foreach (var s in new[] { LevelState.Ready, LevelState.HandedOut, LevelState.Generating })
                for (var skip = 0; ; skip += 500)
                {
                    var page = await tx.Levels.List(state: s, skip: skip, take: 500);
                    foreach (var l in page) refd.Add(l.PackId);
                    if (page.Count < 500) break;
                }
            return (p, a, refd);
        }, ct);
        var deleted = new List<long>();
        foreach (var p in packs.Where(p => p.State == PackState.Retired && !assigned.Contains(p.Id) && !referenced.Contains(p.Id)))
        {
            var f = PathOf(p);
            if (!File.Exists(f)) continue;
            File.Delete(f);
            deleted.Add(p.Id);
        }
        if (deleted.Count > 0)
            await data.WriteAsync(async (tx, _) => { await tx.Audit.Write("pack.gc", mod, null, new { deleted }, "mappool"); return true; }, ct);
        return deleted;
    }

    void CheckDepths(IReadOnlyCollection<int> depths)
    {
        if (depths.Count == 0 || depths.Any(d => d < 1 || d > options.MapPool.Depths))
            throw new HostRefusal(RefusalCode.InvalidArgument, "bad_depth", $"depths must be 1..{options.MapPool.Depths}");
    }
}
