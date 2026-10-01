using System.Collections.Concurrent;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;

namespace SourceSharp.Host.Modules;

/// <summary>
/// How the host receives the mod's rules module (plan §1.2, D-H9): hash first, bytes on
/// request, content-addressed and deduplicated, verified against the announced hash and
/// contract version, recorded in <c>rules_modules</c>, and quarantined from the admin page.
/// The gRPC handlers call it for an authenticated instance; the admin calls the rest.
/// </summary>
public interface IRulesModuleRegistry
{
    /// <summary>
    /// The <c>Booting</c> handshake. Refused first when the contract version is unsupported,
    /// then when the hash is quarantined; Known when the bytes are here and approved, Pending
    /// when they wait for approval, Send when they must be uploaded. Refusals are audited.
    /// </summary>
    Task<RulesModuleAnswer> Announce(ModuleAnnouncement announcement, string instanceId, CancellationToken ct = default);

    /// <summary>
    /// Opens an upload of an announced hash. Throws <see cref="HostRefusal"/> (audited
    /// <c>module.refused</c>) when it was not announced or is quarantined.
    /// </summary>
    Task<IRulesModuleUpload> BeginUpload(string sha256, string instanceId, CancellationToken ct = default);

    /// <summary>Pending (or quarantined) → approved; audited <c>module.approved</c>.</summary>
    Task<RulesModuleRecord> Approve(string sha256, string actor, CancellationToken ct = default);

    /// <summary>
    /// Refuses every later announce, upload and load of the hash, unloads it, audits
    /// <c>module.quarantined</c>, and returns the non-terminal instances that run it: the
    /// caller drains them.
    /// </summary>
    Task<IReadOnlyList<string>> Quarantine(string sha256, string actor, CancellationToken ct = default);

    /// <summary>Removes the module's files when nothing refers to it (else <c>module_in_use</c>); audited <c>module.deleted</c>.</summary>
    Task Delete(string sha256, string actor, CancellationToken ct = default);

    Task<IReadOnlyList<RulesModuleRecord>> List(CancellationToken ct = default);
}

/// <summary>
/// One <c>UploadModule</c> stream. Chunks arrive per declared file (the main assembly and
/// each private dependency); each file is hashed as it arrives and refused on the first
/// mismatch. Nothing reaches the module folder before <see cref="Complete"/>, and an
/// abandoned upload leaves nothing behind when disposed.
/// </summary>
public interface IRulesModuleUpload : IAsyncDisposable
{
    string Sha256 { get; }

    /// <summary>Throws <see cref="HostRefusal"/> (audited, nothing stored) on an undeclared file, a wrong name, a hash mismatch or oversize.</summary>
    Task Write(string sha256, string fileName, ReadOnlyMemory<byte> data, bool last, CancellationToken ct = default);

    /// <summary>Moves the verified files into place atomically and records the module: Known (approved) or Pending.</summary>
    Task<RulesModuleAnswer> Complete(CancellationToken ct = default);
}

public sealed class RulesModuleRegistry : IRulesModuleRegistry
{
    /// <summary>A generous ceiling for one module's files together; an upload past it is refused.</summary>
    public const long DefaultMaxUploadBytes = 256L * 1024 * 1024;

    readonly IHostData _data;
    readonly Func<ModuleOptions> _options;
    readonly RulesModuleLoader _loader;
    readonly Func<Task<IReadOnlySet<string>>> _referencedHashes;
    readonly ConcurrentDictionary<string, ModuleAnnouncement> _announced = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, RulesModuleRecord> _records = new(StringComparer.Ordinal);

    public long MaxUploadBytes { get; init; } = DefaultMaxUploadBytes;

    public RulesModuleRegistry(IHostData data, Func<ModuleOptions> options, RulesModuleLoader loader,
        Func<Task<IReadOnlySet<string>>> referencedHashes)
    {
        _data = data;
        _options = options;
        _loader = loader;
        _referencedHashes = referencedHashes;
    }

    public RulesModuleLoader Loader => _loader;
    string Root => Path.GetFullPath(_options().Path);
    internal string DirectoryOf(string sha256) => Path.Combine(Root, sha256);

    public async Task<RulesModuleAnswer> Announce(ModuleAnnouncement a, string instanceId, CancellationToken ct = default)
    {
        var sha = ModuleNames.Normalize(a.Sha256);

        // The contract first: before anything else happens (§1.2).
        if (!HostContract.Supports(a.ContractVersion))
            return await Refuse(sha, instanceId, "contract_unsupported",
                $"module {a.Assembly} {a.Version} was built against Host.Contracts {a.ContractVersion}; this host is on {HostContract.Version}", ct);

        if (Invalid(a, sha) is { } problem)
            return await Refuse(sha, instanceId, "bad_announcement", problem, ct);

        var row = await Find(sha, ct);
        if (row?.State == ModuleState.Quarantined)
            return await Refuse(sha, instanceId, "quarantined", $"module {sha} is quarantined", ct);

        if (row is not null && ModuleNames.DepsJson(a.Deps) != ModuleNames.DepsJson(ModuleNames.ParseDeps(row.DepsJson)))
            return await Refuse(sha, instanceId, "deps_mismatch", $"module {sha} was recorded with other dependencies", ct);

        if (row is not null && OnDisk(row))
            return row.State == ModuleState.Pending ? RulesModuleAnswer.Pending : RulesModuleAnswer.Known;

        _announced[sha] = a with { Sha256 = sha };
        return RulesModuleAnswer.Send;
    }

    static string? Invalid(ModuleAnnouncement a, string sha)
    {
        if (!ModuleNames.IsSha(sha)) return $"sha256 '{a.Sha256}' is not 64 hex digits";
        if (!ModuleNames.IsAssemblyName(a.Assembly ?? "")) return $"assembly name '{a.Assembly}' is not a simple name";
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { a.Assembly! };
        foreach (var d in a.Deps)
        {
            if (!ModuleNames.IsAssemblyName(d.Assembly ?? "")) return $"dependency name '{d.Assembly}' is not a simple name";
            if (!ModuleNames.IsSha(ModuleNames.Normalize(d.Sha256))) return $"dependency {d.Assembly}: sha256 is not 64 hex digits";
            if (string.Equals(d.Assembly, ModuleNames.ContractsAssembly, StringComparison.OrdinalIgnoreCase))
                return $"{ModuleNames.ContractsAssembly} is never uploaded: the host's own copy is the one that binds";
            if (!names.Add(d.Assembly!)) return $"dependency {d.Assembly} is declared twice";
        }
        return null;
    }

    bool OnDisk(RulesModuleRecord row) =>
        ModuleNames.Files(row.Assembly, row.Sha256, ModuleNames.ParseDeps(row.DepsJson))
            .All(f => File.Exists(Path.Combine(DirectoryOf(row.Sha256), f.Name)));

    public async Task<IRulesModuleUpload> BeginUpload(string sha256, string instanceId, CancellationToken ct = default)
    {
        var sha = ModuleNames.Normalize(sha256);
        if ((await Find(sha, ct))?.State == ModuleState.Quarantined)
            throw await RefusalAsync(sha, instanceId, HostRefusal.Denied("quarantined", $"module {sha} is quarantined: its uploads are refused"), ct);
        if (!_announced.TryGetValue(sha, out var a))
            throw await RefusalAsync(sha, instanceId, HostRefusal.Precondition("not_announced", $"module {sha} was not announced in Booting"), ct);
        Directory.CreateDirectory(Root);
        return new RulesModuleUpload(this, a, instanceId, Path.Combine(Root, $".upload-{Guid.NewGuid():N}"), MaxUploadBytes);
    }

    /// <summary>The verified files of <paramref name="upload"/> into place, then the row and the audit.</summary>
    internal async Task<RulesModuleAnswer> Commit(ModuleAnnouncement a, string instanceId, string tempDir, long bytes, CancellationToken ct)
    {
        var sha = a.Sha256;
        if ((await Find(sha, ct))?.State == ModuleState.Quarantined)
        {
            Directory.Delete(tempDir, recursive: true);
            throw await RefusalAsync(sha, instanceId, HostRefusal.Denied("quarantined", $"module {sha} is quarantined: its uploads are refused"), ct);
        }

        var final = DirectoryOf(sha);
        if (Directory.Exists(final))
        {
            // Content-addressed: whoever got here first wrote the same verified bytes, unless a
            // file is missing (a partial delete); then the new copy replaces it.
            if (ModuleNames.Files(a.Assembly, sha, a.Deps).All(f => File.Exists(Path.Combine(final, f.Name))))
                Directory.Delete(tempDir, recursive: true);
            else
            {
                Directory.Delete(final, recursive: true);
                Directory.Move(tempDir, final);
            }
        }
        else
        {
            try { Directory.Move(tempDir, final); }
            catch (IOException) when (Directory.Exists(final)) { Directory.Delete(tempDir, recursive: true); }
        }

        var requireApproval = _options().RequireApproval;
        var row = await _data.WriteAsync(async (tx, _) =>
        {
            var existing = await tx.Modules.Get(sha);
            var r = existing ?? await tx.Modules.Add(new RulesModuleRecord(
                sha, a.Assembly, a.Version, a.ContractVersion, ModuleNames.DepsJson(a.Deps),
                tx.Now, instanceId, requireApproval ? ModuleState.Pending : ModuleState.Approved, bytes));
            await tx.Audit.Write("module.accepted", sha, null,
                new { instance = instanceId, assembly = a.Assembly, version = a.Version, contract = a.ContractVersion, bytes, state = r.State.ToString(), reupload = existing is not null });
            return r;
        }, ct);
        // The announcement stays: every pod that announced before this commit was told Send and
        // may still be uploading; its copy lands on the verified files above (one entry per hash).
        _records[sha] = row;
        return row.State switch
        {
            ModuleState.Pending => RulesModuleAnswer.Pending,
            ModuleState.Approved => RulesModuleAnswer.Known,
            _ => RulesModuleAnswer.Refused("quarantined", $"module {sha} is quarantined"),
        };
    }

    public async Task<RulesModuleRecord> Approve(string sha256, string actor, CancellationToken ct = default)
    {
        var sha = ModuleNames.Normalize(sha256);
        var row = await _data.WriteAsync(async (tx, _) =>
        {
            var before = await tx.Modules.Get(sha) ?? throw HostRefusal.NotFound("unknown_module", sha);
            var after = await tx.Modules.SetState(sha, ModuleState.Approved);
            await tx.Audit.Write("module.approved", sha, new { state = before.State.ToString() }, new { state = after.State.ToString() }, actor);
            return after;
        }, ct);
        _records[sha] = row;
        return row;
    }

    public async Task<IReadOnlyList<string>> Quarantine(string sha256, string actor, CancellationToken ct = default)
    {
        var sha = ModuleNames.Normalize(sha256);
        var (row, drain) = await _data.WriteAsync(async (tx, _) =>
        {
            var before = await tx.Modules.Get(sha) ?? throw HostRefusal.NotFound("unknown_module", sha);
            var after = await tx.Modules.SetState(sha, ModuleState.Quarantined);
            IReadOnlyList<string> ids = (await tx.Instances.NonTerminal())
                .Where(i => string.Equals(i.RulesSha256, sha, StringComparison.OrdinalIgnoreCase))
                .Select(i => i.Id).Order(StringComparer.Ordinal).ToList();
            await tx.Audit.Write("module.quarantined", sha, new { state = before.State.ToString() }, new { state = after.State.ToString(), drain = ids }, actor);
            return (after, ids);
        }, ct);
        _records[sha] = row;
        _announced.TryRemove(sha, out _);
        _loader.Unload(sha);
        return drain;
    }

    public async Task Delete(string sha256, string actor, CancellationToken ct = default)
    {
        var sha = ModuleNames.Normalize(sha256);
        if ((await _referencedHashes()).Any(h => ModuleNames.Normalize(h) == sha))
            throw HostRefusal.Precondition("module_in_use", $"module {sha} is still referred to by an instance, a level or an assignment");
        var row = await Find(sha, ct) ?? throw HostRefusal.NotFound("unknown_module", sha);

        _loader.Unload(sha);
        var dir = DirectoryOf(sha);
        if (Directory.Exists(dir))
        {
            // Out of the content-addressed name first, so no reader ever sees half a module.
            var doomed = Path.Combine(Root, $".delete-{Guid.NewGuid():N}");
            Directory.Move(dir, doomed);
            Directory.Delete(doomed, recursive: true);
        }
        // IRulesModuleStore has no Remove: the row stays as history, and a later announce of
        // the hash answers Send because its files are gone.
        await _data.WriteAsync(async (tx, _) =>
        {
            await tx.Audit.Write("module.deleted", sha, new { state = row.State.ToString(), bytes = row.Bytes }, null, actor);
            return true;
        }, ct);
    }

    public Task<IReadOnlyList<RulesModuleRecord>> List(CancellationToken ct = default) =>
        _data.ReadAsync((tx, _) => tx.Modules.List(), ct);

    /// <summary>Removes the temp folders an interrupted upload or delete left behind (service start).</summary>
    public int CleanStaleUploads()
    {
        if (!Directory.Exists(Root)) return 0;
        var n = 0;
        foreach (var d in Directory.EnumerateDirectories(Root))
        {
            var name = Path.GetFileName(d);
            if (!name.StartsWith(".upload-", StringComparison.Ordinal) && !name.StartsWith(".delete-", StringComparison.Ordinal)) continue;
            Directory.Delete(d, recursive: true);
            n++;
        }
        return n;
    }

    async Task<RulesModuleRecord?> Find(string sha, CancellationToken ct)
    {
        if (_records.TryGetValue(sha, out var r)) return r;
        var row = await _data.ReadAsync((tx, _) => tx.Modules.Get(sha), ct);
        if (row is not null) _records[sha] = row;
        return row;
    }

    /// <summary>The row, for the synchronous <see cref="IRulesProvider.For"/>: cached; a miss reads the store.</summary>
    internal RulesModuleRecord? Find(string sha) =>
        _records.TryGetValue(sha, out var r) ? r : Find(sha, CancellationToken.None).GetAwaiter().GetResult();

    internal IReadOnlyList<ModuleFile> FilesOf(RulesModuleRecord row) =>
        ModuleNames.Files(row.Assembly, row.Sha256, ModuleNames.ParseDeps(row.DepsJson));

    async Task<RulesModuleAnswer> Refuse(string sha, string instanceId, string reason, string message, CancellationToken ct)
    {
        await AuditRefusal(sha, instanceId, reason, message, ct);
        return RulesModuleAnswer.Refused(reason, message);
    }

    internal async Task<HostRefusal> RefusalAsync(string sha, string instanceId, HostRefusal refusal, CancellationToken ct)
    {
        await AuditRefusal(sha, instanceId, refusal.Reason, refusal.Message, ct);
        return refusal;
    }

    /// <summary>In a transaction of its own: a refusal's audit must survive the refusal (a HostRefusal rolls its transaction back).</summary>
    Task AuditRefusal(string sha, string instanceId, string reason, string message, CancellationToken ct) =>
        _data.WriteAsync(async (tx, _) =>
        {
            await tx.Audit.Write("module.refused", sha, null, new { instance = instanceId, reason, message });
            return true;
        }, ct);
}
