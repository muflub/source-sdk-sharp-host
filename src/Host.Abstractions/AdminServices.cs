namespace SourceSharp.Host.Abstractions;

// The admin UI's seams onto the lead's code (plan §11). Host.Admin calls these and
// writes the audit row itself; an implementation does the work and never audits it a
// second time. Every one is optional in DI: a page shows "not wired" and an action is
// refused with reason "unavailable" until the composition root registers it. Fakes for
// facts live in Host.Testing (AdminFakeLedger, AdminFakeGateway, AdminFakeModules,
// AdminFakeBackups, and AdminFakeLifecycle for the instance manager).

/// <summary>One reconcile finding (§5.4), as the Dashboard lists it.</summary>
public sealed record AdminFinding(string Kind, string Target, string Detail);

public sealed record AdminReconcileReport(DateTimeOffset At, IReadOnlyList<AdminFinding> Findings, bool FixedCachedSums);

/// <summary>
/// The ledger policies the admin needs (Descent's rules, §5; plan Q2): they live in
/// Descent.Service. Methods taking a transaction run inside the admin's own write, so the
/// change, the lease check and the audit row commit together or not at all.
/// </summary>
public interface IAdminLedger
{
    /// <summary>The owner id of an account's stash (softcore and hardcore are separate, D4).</summary>
    string StashOwner(string account, bool hardcore);

    /// <summary>The inverse of <see cref="StashOwner"/>: which account and ladder a stash owner id is; null when it is not one.</summary>
    (string Account, bool Hardcore)? ParseStashOwner(string ownerId);

    /// <summary>How many stash slots the account has for that ladder (the rules' StashSlots over its living characters).</summary>
    Task<int> StashCapacity(IReadTx tx, string account, bool hardcore);

    /// <summary>§5.4, on demand. Nothing is fixed but the cached Australium sum, and only when asked.</summary>
    Task<IReadOnlyList<AdminFinding>> Reconcile(IWriteTx tx, bool fixCachedSums);

    /// <summary>The last reconcile (nightly or on demand), for the Dashboard; null before the first.</summary>
    AdminReconcileReport? LastReconcile { get; }

    /// <summary>Cancels an open or locked trade and returns its escrow to the offerers. Returns the items returned.</summary>
    Task<int> CancelTrade(IWriteTx tx, string tradeId);

    /// <summary>Lost &amp; Found entry → the owner's stash, fee not charged; refused when the stash is full.</summary>
    Task<ItemRecord> ReleaseToStash(IWriteTx tx, string entryId);

    /// <summary>Sets fallen (hardcore death, D7) on the row and in the sheet, which the host cannot edit generically.</summary>
    Task<CharacterRecord> SetFallen(IWriteTx tx, string characterId, bool fallen);
}

/// <summary>One route in the gateway's table (§8.2).</summary>
public sealed record AdminRoute(string SessionId, string ClientAddr, string? SteamId, string Backend, long BytesIn, long BytesOut, DateTimeOffset Since);

/// <summary>What the Gateway page shows beyond the sessions table: the gateway's own view.</summary>
public sealed record AdminGatewaySnapshot(bool Reachable, string? Error, IReadOnlyList<AdminRoute> Routes, int AdmissionQueue,
    string? A2S, IReadOnlyList<string> BannedAddresses, int PinPoolUsed, int PinPoolSize)
{
    public static AdminGatewaySnapshot Unreachable(string error) => new(false, error, [], 0, null, [], 0, 0);
}

/// <summary>The gateway control (§8.2), as the admin drives it.</summary>
public interface IAdminGateway
{
    Task<AdminGatewaySnapshot> Snapshot(CancellationToken ct = default);
    Task CloseSession(string sessionId, string reason, CancellationToken ct = default);
    Task ForceToHub(string sessionId, CancellationToken ct = default);
    Task BanAddress(string address, string? note, CancellationToken ct = default);
    Task ReleasePin(string steamId, CancellationToken ct = default);
}

/// <summary>The rules-module registry (D-H9, §1.2): state changes that must also reach the loaded modules.</summary>
public interface IAdminModules
{
    Task Approve(string sha256, CancellationToken ct = default);
    /// <summary>Refuses the module's uploads and loads from now on. The admin drains its instances itself.</summary>
    Task Quarantine(string sha256, CancellationToken ct = default);
    /// <summary>Deletes the row and its files; throws HostRefusal module_in_use while an instance or level references it.</summary>
    Task Delete(string sha256, CancellationToken ct = default);
}

public sealed record AdminBackup(string Name, long Bytes, DateTimeOffset At, string Kind);

/// <summary>Backups (Q14): <c>VACUUM INTO</c> hourly, 24 hourly + 7 daily, on the service's volume.</summary>
public interface IAdminBackups
{
    Task<IReadOnlyList<AdminBackup>> List(CancellationToken ct = default);
    Task<AdminBackup> RunNow(CancellationToken ct = default);
    /// <summary>The backup's bytes, for download; null when there is no such backup.</summary>
    Stream? Open(string name);
    /// <summary>Copies the backup to a file beside the live database (never over it); returns that path.</summary>
    Task<string> RestoreToFile(string name, CancellationToken ct = default);
    /// <summary>The live database's size on disk, for the Dashboard.</summary>
    long DatabaseBytes { get; }
}

/// <summary>The admin UI's own options (bound from the "AdminUi" section by AddHostAdmin).</summary>
public sealed class AdminUiOptions
{
    /// <summary>Rows per page on list pages.</summary>
    public int PageSize { get; set; } = 50;
    /// <summary>The periodic refresh of the live pages (Dashboard, Gateway, Instances, Map pool).</summary>
    public TimeSpan Refresh { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>Lines of the pod log the Instances page tails.</summary>
    public int LogTail { get; set; } = 200;
    /// <summary>The pod's devapi web UI port the proxy forwards to; 0 = not configured (the proxy answers 501).</summary>
    public int DevApiPort { get; set; }
}
