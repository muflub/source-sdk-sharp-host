namespace SourceSharp.Host.Abstractions;

// Plan §11 (Phase H8): every mutation the admin pages can make. Each is audited with the
// admin actor (ServiceOptions.Admin.Actor, "admin@localhost", Q20) and before/after, and a
// target a game server holds a lease on is refused with a typed "kick and edit" result
// (Q21): the page then offers KickAndRelease, which kicks the player and force-releases
// the lease, and the edit can be retried. Host.Admin's AdminActions implements this over
// the stores and the services; what needs the lead's code goes through IAdminLedger,
// IAdminGateway, IAdminModules and IAdminBackups (AdminServices.cs).

public enum AdminOutcome { Done = 1, Leased, Refused }

/// <summary>The result every admin action returns: done, refused because a target is leased, or refused with a reason.</summary>
public sealed record AdminResult(AdminOutcome Outcome, string Message, string? Reason = null,
    string? CharacterId = null, string? InstanceId = null, object? Value = null)
{
    /// <summary>The words the page shows on a leased refusal, and the button it offers (Q21).</summary>
    public const string KickAndEdit = "leased — kick and edit";

    public bool Ok => Outcome == AdminOutcome.Done;

    public static AdminResult Done(string message, object? value = null) => new(AdminOutcome.Done, message, Value: value);

    public static AdminResult Leased(string characterId, string instanceId) =>
        new(AdminOutcome.Leased, $"character {characterId} is leased by instance {instanceId}: {KickAndEdit}", "leased", characterId, instanceId);

    public static AdminResult Refused(string reason, string message) => new(AdminOutcome.Refused, message, reason);
}

/// <summary>Where "move item" and "give item" put an item.</summary>
public sealed record AdminItemTarget(OwnerKind OwnerKind, string OwnerId, int Slot = -1);

public interface IAdminActions
{
    // ---- accounts
    Task<AdminResult> BanAccount(string steamId, string? note, CancellationToken ct = default);
    Task<AdminResult> UnbanAccount(string steamId, CancellationToken ct = default);
    /// <summary>Marks every character of the account deleted and bans it (the store keeps account rows).</summary>
    Task<AdminResult> DeleteAccount(string steamId, CancellationToken ct = default);

    // ---- characters
    /// <summary>The sheet as JSON through the rules codec: must round-trip and pass ValidateCheckpoint, else refused with the rule named.</summary>
    Task<AdminResult> EditCharacter(string characterId, long expectedVersion, string sheetJson, CancellationToken ct = default);
    Task<AdminResult> GrantAustralium(string characterId, long delta, string reason, CancellationToken ct = default);
    /// <summary>Force-releases the lease (§4.3 admin force-release). No kick.</summary>
    Task<AdminResult> ReleaseLease(string characterId, CancellationToken ct = default);
    /// <summary>"Kick and edit": kicks the player from the leasing instance, then force-releases the lease.</summary>
    Task<AdminResult> KickAndRelease(string characterId, CancellationToken ct = default);
    Task<AdminResult> SetFallen(string characterId, bool fallen, CancellationToken ct = default);
    Task<AdminResult> DeleteCharacter(string characterId, CancellationToken ct = default);

    // ---- items
    /// <summary>The instance as JSON through the rules codec; flagged admin_edited when it no longer replays.</summary>
    Task<AdminResult> EditItem(string itemId, long expectedVersion, string instanceJson, CancellationToken ct = default);
    Task<AdminResult> MoveItem(string itemId, AdminItemTarget to, CancellationToken ct = default);
    /// <summary>A mint (§5.2 "admin"), to a character or a stash.</summary>
    Task<AdminResult> GiveItem(AdminItemTarget to, string instanceJson, CancellationToken ct = default);
    Task<AdminResult> DestroyItem(string itemId, string reason, CancellationToken ct = default);

    // ---- stash
    Task<AdminResult> StashMove(string itemId, int slot, CancellationToken ct = default);
    /// <summary>Out of the stash into the Admin owner (kept, recoverable with MoveItem).</summary>
    Task<AdminResult> StashRemove(string itemId, CancellationToken ct = default);

    // ---- parties and trades
    Task<AdminResult> DisbandParty(string partyId, CancellationToken ct = default);
    Task<AdminResult> KickFromParty(string partyId, string characterId, CancellationToken ct = default);
    Task<AdminResult> CancelTrade(string tradeId, CancellationToken ct = default);

    // ---- Lost & Found
    Task<AdminResult> WaiveFee(string entryId, CancellationToken ct = default);
    Task<AdminResult> ReleaseToStash(string entryId, CancellationToken ct = default);
    Task<AdminResult> DeleteLostAndFound(string entryId, CancellationToken ct = default);

    // ---- instances
    /// <summary>Lets the manager (re)create the hub(s) on its next tick (IInstanceLifecycle.ResumeHubs).</summary>
    Task<AdminResult> CreateHub(CancellationToken ct = default);
    Task<AdminResult> DrainInstance(string instanceId, string reason, CancellationToken ct = default);
    Task<AdminResult> KickPlayer(string instanceId, string steamId, string reason, CancellationToken ct = default);
    /// <summary>Deletes the pod by name and uid: the manager sees it gone and treats the instance as crashed.</summary>
    Task<AdminResult> DeletePod(string instanceId, CancellationToken ct = default);
    Task<AdminResult> Exec(string instanceId, string command, CancellationToken ct = default);

    // ---- gateway
    Task<AdminResult> CloseSession(string sessionId, CancellationToken ct = default);
    Task<AdminResult> ForceToHub(string sessionId, CancellationToken ct = default);
    Task<AdminResult> BanAddress(string address, string? note, CancellationToken ct = default);
    Task<AdminResult> ReleasePin(string steamId, CancellationToken ct = default);

    // ---- map pool
    /// <summary>Retires the depth's ready levels (never a handed-out one) so the pool links fresh ones.</summary>
    Task<AdminResult> RegenerateDepth(int depth, CancellationToken ct = default);
    Task<AdminResult> RetireLevel(string hash, CancellationToken ct = default);
    /// <summary>Retires the level and deletes its files; refused while handed out.</summary>
    Task<AdminResult> DeleteLevel(string hash, CancellationToken ct = default);
    Task<AdminResult> RequeueJob(long jobId, CancellationToken ct = default);
    /// <summary>K for one depth (runtime setting <c>MapPool.PerDepth.&lt;depth&gt;</c>).</summary>
    Task<AdminResult> SetPerDepth(int depth, int k, CancellationToken ct = default);

    // ---- room packs (§9.4)
    Task<AdminResult> UploadPack(string mod, string library, Stream content, string? libraryJson, string? note, CancellationToken ct = default);
    Task<AdminResult> ActivatePack(string mod, IReadOnlyCollection<int> depths, long packId, bool pinned, OldLevelsPolicy policy, CancellationToken ct = default);
    Task<AdminResult> PinDepths(string mod, IReadOnlyCollection<int> depths, bool pinned, CancellationToken ct = default);
    Task<AdminResult> RollBackPack(string mod, IReadOnlyCollection<int> depths, long olderPackId, OldLevelsPolicy policy, CancellationToken ct = default);
    Task<AdminResult> RetirePack(long packId, CancellationToken ct = default);
    Task<AdminResult> RebakePack(string mod, string library, CancellationToken ct = default);
    Task<AdminResult> CollectPacks(string mod, CancellationToken ct = default);

    // ---- rules modules (D-H9)
    Task<AdminResult> ApproveModule(string sha256, CancellationToken ct = default);
    /// <summary>Quarantines the module and drains the instances running it.</summary>
    Task<AdminResult> QuarantineModule(string sha256, CancellationToken ct = default);
    Task<AdminResult> DeleteModule(string sha256, CancellationToken ct = default);

    // ---- operations
    Task<AdminResult> RunReconcile(bool fixCachedSums, CancellationToken ct = default);
    Task<AdminResult> RunBackup(CancellationToken ct = default);
    /// <summary>Copies a backup to a file beside the live database (never over it), for a manual restore.</summary>
    Task<AdminResult> RestoreBackupToFile(string name, CancellationToken ct = default);
    /// <summary>A runtime setting: the value must parse as JSON; the key must be one the Config page marks runtime.</summary>
    Task<AdminResult> SetSetting(string key, string valueJson, CancellationToken ct = default);
}
