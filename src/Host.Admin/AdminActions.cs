using System.Text;
using System.Text.Json;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;
using SourceSharp.Host.Data;
using SourceSharp.Host.MapPool;

namespace SourceSharp.Host.Admin;

/// <summary>The optional collaborators of <see cref="AdminActions"/>: each is null until the composition root registers it.</summary>
public sealed record AdminCollaborators(
    IInstanceLifecycle? Lifecycle = null,
    IInstanceHost? Pods = null,
    PackCatalog? Packs = null,
    MapPoolWorker? Pool = null,
    PoolSignal? PoolSignal = null,
    IAdminLedger? Ledger = null,
    IAdminGateway? Gateway = null,
    IAdminModules? Modules = null,
    IAdminBackups? Backups = null);

/// <summary>
/// Every admin mutation (plan §11) over the stores and the services. A store change, its lease
/// check and its audit row run in one write transaction as the admin actor: a refusal throws
/// out of the transaction, so nothing of it commits. Actions on another service (the instance
/// manager, the gateway, the module registry, backups) run outside the transaction and are
/// audited after they succeed; the instance manager audits Drain, Kick, Exec and ResumeHubs
/// itself with the admin actor, so those are not audited twice.
/// </summary>
public sealed class AdminActions(IHostData data, IRulesProvider rules, ServiceOptions options, AdminCollaborators? with = null) : IAdminActions
{
    readonly AdminCollaborators _c = with ?? new AdminCollaborators();
    public string Actor => options.Admin.Actor;

    /// <summary>The settings the Config page may edit at run time (prefix match for the per-depth keys).</summary>
    public static readonly IReadOnlyList<string> RuntimeSettings =
    [
        "Instances.ModImage", "Instances.MaxLevelPods", "Instances.WarmSpares", "Instances.EmptyGrace", "Instances.CorpseGrace",
        "Instances.Hubs", "MapPool.DefaultPerDepth", "MapPool.PerDepth.", "MapPool.AutoActivateBakes", "Reserve.Sizes", "Reserve.LowWater",
    ];

    public static bool IsRuntimeSetting(string key) =>
        RuntimeSettings.Any(k => k.EndsWith('.') ? key.StartsWith(k, StringComparison.Ordinal) && key.Length > k.Length : key == k);

    // ------------------------------------------------------------------ plumbing

    sealed class LeasedException(string characterId, string instanceId) : Exception
    {
        public string CharacterId { get; } = characterId;
        public string InstanceId { get; } = instanceId;
    }

    Task<T> Write<T>(Func<IWriteTx, Task<T>> work, CancellationToken ct) =>
        data is HostData hd ? hd.WriteAsActorAsync(Actor, (tx, _) => work(tx), ct) : data.WriteAsync((tx, _) => work(tx), ct);

    /// <summary>One transaction: the work returns the result; a lease or a refusal rolls it all back.</summary>
    async Task<AdminResult> Tx(Func<IWriteTx, Task<AdminResult>> work, CancellationToken ct)
    {
        try { return await Write(work, ct); }
        catch (LeasedException l) { return AdminResult.Leased(l.CharacterId, l.InstanceId); }
        catch (HostRefusal r) { return AdminResult.Refused(r.Reason, r.Message); }
        catch (InvalidDataException e) { return AdminResult.Refused("bad_json", e.Message); }
        catch (JsonException e) { return AdminResult.Refused("bad_json", e.Message); }
    }

    /// <summary>An action on another service, audited after it succeeds.</summary>
    async Task<AdminResult> External(string action, string target, object? before, Func<Task<object?>> work, string message, bool audit = true)
    {
        object? after;
        try { after = await work(); }
        catch (HostRefusal r) { return AdminResult.Refused(r.Reason, r.Message); }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException or TimeoutException)
        {
            return AdminResult.Refused("unreachable", $"{action} failed: {e.Message}");
        }
        if (audit) await Write(async tx => { await tx.Audit.Write(action, target, before, after, Actor); return true; }, default);
        return AdminResult.Done(message, after);
    }

    static AdminResult Unavailable(string what) => AdminResult.Refused("unavailable", $"{what} is not wired into this service");

    static async Task Audit(IWriteTx tx, string action, string target, object? before, object? after, string actor) =>
        await tx.Audit.Write(action, target, before, after, actor);

    static async Task Unleased(IReadTx tx, string characterId)
    {
        if (await tx.Leases.Get(characterId) is { } l) throw new LeasedException(characterId, l.InstanceId);
    }

    /// <summary>The characters whose data an item is: its owner, the corpse's, a stash's ladder, a trade's parties.</summary>
    async Task<IReadOnlyList<string>> HoldersOf(IReadTx tx, ItemRecord item)
    {
        switch (item.OwnerKind)
        {
            case OwnerKind.Character or OwnerKind.Corpse or OwnerKind.Reserve or OwnerKind.LostAndFound:
                return [item.OwnerId];
            case OwnerKind.World:
                return item.ForCharacter is { } f ? [f] : [];
            case OwnerKind.TradeEscrow:
                return await tx.Trades.Get(item.OwnerId) is { } t ? [t.A, t.B] : [];
            case OwnerKind.Stash:
                return await StashHolders(tx, item.OwnerId);
            default:
                return [];
        }
    }

    async Task<IReadOnlyList<string>> StashHolders(IReadTx tx, string stashOwner)
    {
        if (_c.Ledger?.ParseStashOwner(stashOwner) is not { } s) return [];
        return (await tx.Characters.List(s.Account)).Where(c => c.Hardcore == s.Hardcore).Select(c => c.Id).ToList();
    }

    async Task UnleasedAll(IReadTx tx, IEnumerable<string> characterIds)
    {
        foreach (var id in characterIds) await Unleased(tx, id);
    }

    /// <summary>The characters a target owner id stands for (a character, or a stash's ladder).</summary>
    async Task<IReadOnlyList<string>> HoldersOfTarget(IReadTx tx, AdminItemTarget to) => to.OwnerKind switch
    {
        OwnerKind.Character or OwnerKind.Corpse or OwnerKind.Reserve or OwnerKind.LostAndFound => [to.OwnerId],
        OwnerKind.Stash => await StashHolders(tx, to.OwnerId),
        _ => [],
    };

    static async Task<CharacterRecord> Character(IReadTx tx, string id) =>
        await tx.Characters.Get(id) ?? throw HostRefusal.NotFound("no_character", id);

    static async Task<ItemRecord> Item(IReadTx tx, string id) =>
        await tx.Items.Get(id) is { State: ItemState.Live } i ? i : throw HostRefusal.NotFound("no_item", $"no live item {id}");

    IGameRules Rules => rules.Current ?? throw HostRefusal.Precondition("no_rules", PoolStatus.WaitingForRulesMessage);

    static object Row(CharacterRecord c) => new { c.Name, c.ClassName, c.Level, c.Xp, c.ReachedDepth, c.Fallen, c.Australium, c.Deleted, c.Version };
    static object Row(ItemRecord i) => new { i.OwnerKind, i.OwnerId, i.Slot, i.State, i.AdminEdited, i.Version, Instance = Encoding.UTF8.GetString(i.Instance) };

    // ------------------------------------------------------------------ accounts

    public Task<AdminResult> BanAccount(string steamId, string? note, CancellationToken ct = default) => Tx(async tx =>
    {
        var a = await tx.Characters.GetAccount(steamId) ?? throw HostRefusal.NotFound("no_account", steamId);
        await tx.Characters.SetBanned(steamId, true, note ?? a.Note);
        await Audit(tx, "account.ban", steamId, new { a.Banned, a.Note }, new { Banned = true, Note = note ?? a.Note }, Actor);
        return AdminResult.Done($"banned {steamId}");
    }, ct);

    public Task<AdminResult> UnbanAccount(string steamId, CancellationToken ct = default) => Tx(async tx =>
    {
        var a = await tx.Characters.GetAccount(steamId) ?? throw HostRefusal.NotFound("no_account", steamId);
        await tx.Characters.SetBanned(steamId, false, a.Note);
        await Audit(tx, "account.unban", steamId, new { a.Banned }, new { Banned = false }, Actor);
        return AdminResult.Done($"unbanned {steamId}");
    }, ct);

    public Task<AdminResult> DeleteAccount(string steamId, CancellationToken ct = default) => Tx(async tx =>
    {
        var a = await tx.Characters.GetAccount(steamId) ?? throw HostRefusal.NotFound("no_account", steamId);
        var chars = await tx.Characters.List(steamId);
        await UnleasedAll(tx, chars.Select(c => c.Id));
        foreach (var c in chars) await tx.Characters.MarkDeleted(c.Id);
        await tx.Characters.SetBanned(steamId, true, "deleted by admin");
        await Audit(tx, "account.delete", steamId, new { a.Banned, Characters = chars.Select(c => c.Id) }, new { Banned = true, Deleted = chars.Count }, Actor);
        return AdminResult.Done($"deleted {steamId} ({chars.Count} characters)");
    }, ct);

    // ------------------------------------------------------------------ characters

    public Task<AdminResult> EditCharacter(string characterId, long expectedVersion, string sheetJson, CancellationToken ct = default) => Tx(async tx =>
    {
        var c = await Character(tx, characterId);
        await Unleased(tx, characterId);
        var r = Rules;
        var before = new CharacterSheet(c.Sheet, c.SheetVersion);
        var after = r.Sheets.FromJson(sheetJson);
        var again = r.Sheets.FromJson(r.Sheets.ToJson(after));
        if (!again.Data.AsSpan().SequenceEqual(after.Data) || again.SchemaVersion != after.SchemaVersion)
            throw HostRefusal.Precondition("no_round_trip", "the sheet does not survive the rules codec's JSON round trip");
        var verdict = r.ValidateCheckpoint(before, after, new CheckpointEvidence([], 0));
        if (!verdict.Ok)
            throw HostRefusal.Precondition("rule:" + verdict.FailedRule, $"the rules refuse this sheet: {verdict.FailedRule}");
        var s = r.Sheets.Summarize(after);
        var updated = await tx.Characters.Update(characterId, expectedVersion, x => x with
        {
            Sheet = after.Data, SheetVersion = after.SchemaVersion, Name = s.Name, ClassName = s.ClassName,
            Level = s.Level, Xp = s.Xp, ReachedDepth = s.ReachedDepth, Fallen = s.Fallen,
        });
        await Audit(tx, "character.edit", characterId, new { Row = Row(c), Sheet = r.Sheets.ToJson(before) },
            new { Row = Row(updated), Sheet = r.Sheets.ToJson(after) }, Actor);
        return AdminResult.Done($"edited {c.Name}", updated);
    }, ct);

    public Task<AdminResult> GrantAustralium(string characterId, long delta, string reason, CancellationToken ct = default) => Tx(async tx =>
    {
        var c = await Character(tx, characterId);
        await Unleased(tx, characterId);
        if (delta == 0) throw new HostRefusal(RefusalCode.InvalidArgument, "bad_amount", "a grant of 0 changes nothing");
        var total = await tx.Characters.AddAustralium(characterId, delta, "admin_grant", string.IsNullOrWhiteSpace(reason) ? null : reason);
        await Audit(tx, "character.australium", characterId, new { c.Australium }, new { Australium = total, delta, reason }, Actor);
        return AdminResult.Done($"{(delta > 0 ? "granted" : "took")} {Math.Abs(delta)} Australium; now {total}", total);
    }, ct);

    public Task<AdminResult> ReleaseLease(string characterId, CancellationToken ct = default) => Tx(async tx =>
    {
        var l = await tx.Leases.Get(characterId) ?? throw HostRefusal.NotFound("no_lease", $"{characterId} holds no lease");
        await tx.Leases.Release(characterId, null);
        await Audit(tx, "lease.force_release", characterId, new { l.InstanceId, l.Expires }, null, Actor);
        return AdminResult.Done($"released {characterId}'s lease on {l.InstanceId}");
    }, ct);

    public async Task<AdminResult> KickAndRelease(string characterId, CancellationToken ct = default)
    {
        var (c, lease) = await data.ReadAsync(async (tx, _) => (await tx.Characters.Get(characterId), await tx.Leases.Get(characterId)), ct);
        if (c is null) return AdminResult.Refused("no_character", characterId);
        if (lease is null) return AdminResult.Done($"{characterId} holds no lease");
        if (_c.Lifecycle is { } life)
        {
            try { await life.Kick(lease.InstanceId, c.Account, "an admin is editing this character", Actor, ct); }
            catch (HostRefusal) { /* no open stream: the pod is gone or going; releasing is all that is left */ }
        }
        var released = await ReleaseLease(characterId, ct);
        return released.Ok ? AdminResult.Done($"kicked {c.Account} from {lease.InstanceId} and released the lease") : released;
    }

    public async Task<AdminResult> SetFallen(string characterId, bool fallen, CancellationToken ct = default)
    {
        if (_c.Ledger is not { } ledger) return Unavailable("the ledger");
        return await Tx(async tx =>
        {
            var c = await Character(tx, characterId);
            await Unleased(tx, characterId);
            var after = await ledger.SetFallen(tx, characterId, fallen);
            await Audit(tx, fallen ? "character.fallen" : "character.unfallen", characterId, new { c.Fallen }, new { after.Fallen }, Actor);
            return AdminResult.Done($"{c.Name} is {(fallen ? "fallen" : "no longer fallen")}", after);
        }, ct);
    }

    public Task<AdminResult> DeleteCharacter(string characterId, CancellationToken ct = default) => Tx(async tx =>
    {
        var c = await Character(tx, characterId);
        await Unleased(tx, characterId);
        await tx.Characters.MarkDeleted(characterId);
        await Audit(tx, "character.delete", characterId, Row(c), new { Deleted = true }, Actor);
        return AdminResult.Done($"deleted {c.Name}");
    }, ct);

    // ------------------------------------------------------------------ items

    public Task<AdminResult> EditItem(string itemId, long expectedVersion, string instanceJson, CancellationToken ct = default) => Tx(async tx =>
    {
        var i = await Item(tx, itemId);
        await UnleasedAll(tx, await HoldersOf(tx, i));
        var r = Rules;
        var rolled = r.Items.FromJson(instanceJson);
        var bytes = rolled.Instance.Length > 0 ? rolled.Instance : Encoding.UTF8.GetBytes(r.Items.ToJson(rolled));
        var replays = r.Replays(rolled);
        var edited = await tx.Items.Mutate(itemId, expectedVersion, bytes, rolled.SchemaVersion, rolled.Identified,
            adminEdited: i.AdminEdited || !replays, "admin_edit", null);
        await Audit(tx, "item.edit", itemId, Row(i), new { Row = Row(edited), replays }, Actor);
        return AdminResult.Done(replays ? "edited; it still replays" : "edited; it no longer replays and is flagged admin_edited", edited);
    }, ct);

    public Task<AdminResult> MoveItem(string itemId, AdminItemTarget to, CancellationToken ct = default) => Tx(async tx =>
    {
        var i = await Item(tx, itemId);
        await UnleasedAll(tx, await HoldersOf(tx, i));
        await UnleasedAll(tx, await HoldersOfTarget(tx, to));
        if (to.OwnerKind is OwnerKind.World or OwnerKind.Reserve or OwnerKind.Corpse or OwnerKind.TradeEscrow or OwnerKind.Vendor)
            throw new HostRefusal(RefusalCode.InvalidArgument, "bad_owner", $"an admin moves items to Character, Stash, LostAndFound or Admin, not {to.OwnerKind}");
        if (to.OwnerKind == OwnerKind.Character) await Character(tx, to.OwnerId);
        if (i.OwnerKind == OwnerKind.LostAndFound && await tx.LostAndFound.ForItem(itemId) is { } entry)
            await tx.LostAndFound.Remove(entry.Id);
        var moved = await tx.Items.Move(new ItemMove(itemId, i.OwnerKind, to.OwnerKind, to.OwnerId, "admin_move", i.Version, Slot: to.Slot));
        await Audit(tx, "item.move", itemId, Row(i), Row(moved), Actor);
        return AdminResult.Done($"moved {itemId} to {to.OwnerKind} {to.OwnerId}", moved);
    }, ct);

    public Task<AdminResult> GiveItem(AdminItemTarget to, string instanceJson, CancellationToken ct = default) => Tx(async tx =>
    {
        if (to.OwnerKind is not (OwnerKind.Character or OwnerKind.Stash))
            throw new HostRefusal(RefusalCode.InvalidArgument, "bad_owner", "give mints to a character or a stash");
        if (to.OwnerKind == OwnerKind.Character) await Character(tx, to.OwnerId);
        await UnleasedAll(tx, await HoldersOfTarget(tx, to));
        var r = Rules;
        var rolled = r.Items.FromJson(instanceJson);
        var bytes = rolled.Instance.Length > 0 ? rolled.Instance : Encoding.UTF8.GetBytes(r.Items.ToJson(rolled));
        var minted = await tx.Items.Mint(new NewItem(rolled.Seed, rolled.BaseType, rolled.Rarity, rolled.ItemLevel, rolled.Count, rolled.Identified,
            bytes, rolled.SchemaVersion, rolled.Tier), to.OwnerKind, to.OwnerId, null, to.Slot, "admin", null);
        if (!r.Replays(rolled))
            minted = await tx.Items.Mutate(minted.Id, minted.Version, bytes, rolled.SchemaVersion, rolled.Identified, true, "admin_edit", null);
        await Audit(tx, "item.give", minted.Id, null, Row(minted), Actor);
        return AdminResult.Done($"gave {minted.BaseType} ({minted.Id}) to {to.OwnerKind} {to.OwnerId}", minted);
    }, ct);

    public Task<AdminResult> DestroyItem(string itemId, string reason, CancellationToken ct = default) => Tx(async tx =>
    {
        var i = await Item(tx, itemId);
        await UnleasedAll(tx, await HoldersOf(tx, i));
        if (i.OwnerKind == OwnerKind.LostAndFound && await tx.LostAndFound.ForItem(itemId) is { } entry)
            await tx.LostAndFound.Remove(entry.Id);
        var gone = await tx.Items.Terminate(itemId, i.OwnerKind, ItemState.Destroyed, "admin: " + (string.IsNullOrWhiteSpace(reason) ? "destroyed" : reason), null);
        await Audit(tx, "item.destroy", itemId, Row(i), Row(gone), Actor);
        return AdminResult.Done($"destroyed {itemId}", gone);
    }, ct);

    // ------------------------------------------------------------------ stash

    public async Task<AdminResult> StashMove(string itemId, int slot, CancellationToken ct = default)
    {
        if (_c.Ledger is not { } ledger) return Unavailable("the ledger");
        return await Tx(async tx =>
        {
            var i = await Item(tx, itemId);
            if (i.OwnerKind != OwnerKind.Stash) throw HostRefusal.Precondition("wrong_owner", $"{itemId} is not in a stash");
            await UnleasedAll(tx, await StashHolders(tx, i.OwnerId));
            var s = ledger.ParseStashOwner(i.OwnerId) ?? throw HostRefusal.Precondition("wrong_owner", $"{i.OwnerId} is not a stash");
            var cap = await ledger.StashCapacity(tx, s.Account, s.Hardcore);
            if (slot < 0 || slot >= cap) throw new HostRefusal(RefusalCode.InvalidArgument, "bad_slot", $"slot must be 0..{cap - 1}");
            var used = await tx.Items.Query(new ItemQuery(OwnerKind: OwnerKind.Stash, OwnerId: i.OwnerId, Take: 10_000));
            if (used.Any(x => x.Slot == slot && x.Id != itemId)) throw HostRefusal.Precondition("slot_taken", $"stash slot {slot} is taken");
            var moved = await tx.Items.Move(new ItemMove(itemId, OwnerKind.Stash, OwnerKind.Stash, i.OwnerId, "admin_stash_move", i.Version, Slot: slot));
            await Audit(tx, "stash.move", itemId, new { i.Slot }, new { moved.Slot }, Actor);
            return AdminResult.Done($"moved {itemId} to stash slot {slot}", moved);
        }, ct);
    }

    public Task<AdminResult> StashRemove(string itemId, CancellationToken ct = default) => Tx(async tx =>
    {
        var i = await Item(tx, itemId);
        if (i.OwnerKind != OwnerKind.Stash) throw HostRefusal.Precondition("wrong_owner", $"{itemId} is not in a stash");
        await UnleasedAll(tx, await StashHolders(tx, i.OwnerId));
        var moved = await tx.Items.Move(new ItemMove(itemId, OwnerKind.Stash, OwnerKind.Admin, Actor, "admin_stash_remove", i.Version));
        await Audit(tx, "stash.remove", itemId, Row(i), Row(moved), Actor);
        return AdminResult.Done($"removed {itemId} from the stash (now held by {Actor})", moved);
    }, ct);

    // ------------------------------------------------------------------ parties and trades

    public Task<AdminResult> DisbandParty(string partyId, CancellationToken ct = default) => Tx(async tx =>
    {
        var p = await tx.Parties.Get(partyId) ?? throw HostRefusal.NotFound("no_party", partyId);
        await UnleasedAll(tx, p.Members);
        await tx.Parties.Disband(partyId);
        await Audit(tx, "party.disband", partyId, new { p.Leader, p.Members }, null, Actor);
        return AdminResult.Done($"disbanded party {partyId}");
    }, ct);

    public Task<AdminResult> KickFromParty(string partyId, string characterId, CancellationToken ct = default) => Tx(async tx =>
    {
        var p = await tx.Parties.Get(partyId) ?? throw HostRefusal.NotFound("no_party", partyId);
        if (!p.Members.Contains(characterId)) throw HostRefusal.NotFound("not_member", $"{characterId} is not in party {partyId}");
        await Unleased(tx, characterId);
        var after = await tx.Parties.Remove(partyId, characterId);
        await Audit(tx, "party.kick", partyId, new { p.Leader, p.Members }, after is null ? null : new { after.Leader, after.Members }, Actor);
        return AdminResult.Done($"removed {characterId} from party {partyId}");
    }, ct);

    public async Task<AdminResult> CancelTrade(string tradeId, CancellationToken ct = default)
    {
        if (_c.Ledger is not { } ledger) return Unavailable("the ledger");
        return await Tx(async tx =>
        {
            var t = await tx.Trades.Get(tradeId) ?? throw HostRefusal.NotFound("no_trade", tradeId);
            if (t.State is TradeStatus.Committed or TradeStatus.Cancelled) throw HostRefusal.Precondition("trade_state", $"trade {tradeId} is {t.State}");
            await UnleasedAll(tx, [t.A, t.B]);
            var returned = await ledger.CancelTrade(tx, tradeId);
            await Audit(tx, "trade.cancel", tradeId, new { t.State, t.OfferA, t.OfferB }, new { State = TradeStatus.Cancelled, returned }, Actor);
            return AdminResult.Done($"cancelled trade {tradeId}; {returned} item(s) returned from escrow", returned);
        }, ct);
    }

    // ------------------------------------------------------------------ Lost & Found

    static async Task<LostAndFoundRecord> Entry(IReadTx tx, string id) =>
        await tx.LostAndFound.Get(id) ?? throw HostRefusal.NotFound("no_entry", id);

    public Task<AdminResult> WaiveFee(string entryId, CancellationToken ct = default) => Tx(async tx =>
    {
        var e = await Entry(tx, entryId);
        await Unleased(tx, e.CharacterId);
        await tx.LostAndFound.SetFee(entryId, 0);
        await Audit(tx, "lost_and_found.waive", entryId, new { e.Fee }, new { Fee = 0 }, Actor);
        return AdminResult.Done($"waived the fee of {e.Fee}");
    }, ct);

    public async Task<AdminResult> ReleaseToStash(string entryId, CancellationToken ct = default)
    {
        if (_c.Ledger is not { } ledger) return Unavailable("the ledger");
        return await Tx(async tx =>
        {
            var e = await Entry(tx, entryId);
            await Unleased(tx, e.CharacterId);
            var c = await Character(tx, e.CharacterId);
            await UnleasedAll(tx, await StashHolders(tx, ledger.StashOwner(c.Account, c.Hardcore)));
            var item = await ledger.ReleaseToStash(tx, entryId);
            await Audit(tx, "lost_and_found.release", entryId, new { e.ItemId, e.Fee, e.CharacterId }, Row(item), Actor);
            return AdminResult.Done($"released {e.ItemId} to {c.Account}'s stash", item);
        }, ct);
    }

    public Task<AdminResult> DeleteLostAndFound(string entryId, CancellationToken ct = default) => Tx(async tx =>
    {
        var e = await Entry(tx, entryId);
        await Unleased(tx, e.CharacterId);
        await tx.LostAndFound.Remove(entryId);
        var gone = await tx.Items.Terminate(e.ItemId, OwnerKind.LostAndFound, ItemState.Destroyed, "admin: lost_and_found_deleted", null);
        await Audit(tx, "lost_and_found.delete", entryId, new { e.ItemId, e.Fee, e.CharacterId }, Row(gone), Actor);
        return AdminResult.Done($"deleted entry {entryId}; item {e.ItemId} destroyed");
    }, ct);

    // ------------------------------------------------------------------ instances

    public Task<AdminResult> CreateHub(CancellationToken ct = default) => _c.Lifecycle is not { } life
        ? Task.FromResult(Unavailable("the instance manager"))
        : External("hub.create", "hub", null, async () => { await life.ResumeHubs(Actor, ct); return null; },
            "the manager creates the hub(s) on its next tick", audit: false);

    public Task<AdminResult> DrainInstance(string instanceId, string reason, CancellationToken ct = default) => _c.Lifecycle is not { } life
        ? Task.FromResult(Unavailable("the instance manager"))
        : External("instance.drain", instanceId, null, async () => (await life.Drain(instanceId, reason, Actor, ct)).State,
            $"draining {instanceId}", audit: false);

    public Task<AdminResult> KickPlayer(string instanceId, string steamId, string reason, CancellationToken ct = default) => _c.Lifecycle is not { } life
        ? Task.FromResult(Unavailable("the instance manager"))
        : External("instance.kick", instanceId, null, async () => { await life.Kick(instanceId, steamId, reason, Actor, ct); return null; },
            $"kicked {steamId}", audit: false);

    public Task<AdminResult> Exec(string instanceId, string command, CancellationToken ct = default) => _c.Lifecycle is not { } life
        ? Task.FromResult(Unavailable("the instance manager"))
        : string.IsNullOrWhiteSpace(command) ? Task.FromResult(AdminResult.Refused("bad_command", "empty command"))
        : External("instance.exec", instanceId, null, async () => { await life.Exec(instanceId, command, Actor, ct); return null; },
            $"sent: {command}", audit: false);

    public async Task<AdminResult> DeletePod(string instanceId, CancellationToken ct = default)
    {
        if (_c.Pods is not { } pods) return Unavailable("the pod host");
        var row = await data.ReadAsync((tx, _) => tx.Instances.Get(instanceId), ct);
        if (row is null) return AdminResult.Refused("no_instance", instanceId);
        if (row.PodName is null || row.PodUid is null) return AdminResult.Refused("no_pod", $"{instanceId} has no pod");
        return await External("instance.delete_pod", instanceId, new { row.PodName, row.PodUid, row.State }, async () =>
        {
            var deleted = await pods.Delete(row.PodName, row.PodUid, options.Instances.ReapGrace, ct);
            return new { deleted };
        }, $"deleted pod {row.PodName}");
    }

    // ------------------------------------------------------------------ gateway

    public async Task<AdminResult> CloseSession(string sessionId, CancellationToken ct = default)
    {
        if (_c.Gateway is not { } gw) return Unavailable("the gateway control");
        var s = await data.ReadAsync((tx, _) => tx.Sessions.Get(sessionId), ct);
        return await External("session.close", sessionId, s is null ? null : new { s.ClientAddr, s.SteamId, s.InstanceId, s.State },
            async () => { await gw.CloseSession(sessionId, "closed by admin", ct); return null; }, $"closed session {sessionId}");
    }

    public async Task<AdminResult> ForceToHub(string sessionId, CancellationToken ct = default)
    {
        if (_c.Gateway is not { } gw) return Unavailable("the gateway control");
        var s = await data.ReadAsync((tx, _) => tx.Sessions.Get(sessionId), ct);
        return await External("session.force_to_hub", sessionId, s is null ? null : new { s.InstanceId, s.Backend },
            async () => { await gw.ForceToHub(sessionId, ct); return null; }, $"session {sessionId} sent to the hub");
    }

    public Task<AdminResult> BanAddress(string address, string? note, CancellationToken ct = default) => _c.Gateway is not { } gw
        ? Task.FromResult(Unavailable("the gateway control"))
        : string.IsNullOrWhiteSpace(address) ? Task.FromResult(AdminResult.Refused("bad_address", "empty address"))
        : External("gateway.ban_address", address, null, async () => { await gw.BanAddress(address, note, ct); return new { note }; }, $"banned {address}");

    public async Task<AdminResult> ReleasePin(string steamId, CancellationToken ct = default)
    {
        if (_c.Gateway is not { } gw) return Unavailable("the gateway control");
        var pin = await data.ReadAsync((tx, _) => tx.Sessions.GetPin(steamId), ct);
        return await External("gateway.release_pin", steamId, pin is null ? null : new { pin.PinnedPeer, pin.Allocator },
            async () => { await gw.ReleasePin(steamId, ct); return null; }, $"released {steamId}'s pin");
    }

    // ------------------------------------------------------------------ map pool

    public async Task<AdminResult> RegenerateDepth(int depth, CancellationToken ct = default)
    {
        var r = await Tx(async tx =>
        {
            var ready = await tx.Levels.List(depth: depth, state: LevelState.Ready);
            foreach (var l in ready) await tx.Levels.SetState(l.Hash, LevelState.Retired, "regenerated by admin");
            await Audit(tx, "pool.regenerate", $"depth {depth}", new { Ready = ready.Select(l => l.Hash) }, new { Retired = ready.Count }, Actor);
            return AdminResult.Done($"retired {ready.Count} ready level(s) of depth {depth}; the pool links fresh ones", ready.Count);
        }, ct);
        _c.PoolSignal?.Pulse();
        return r;
    }

    static async Task<LevelRecord> NotHandedOut(IReadTx tx, string hash)
    {
        var l = await tx.Levels.Get(hash) ?? throw HostRefusal.NotFound("no_level", hash);
        if (l.State == LevelState.HandedOut)
            throw HostRefusal.Precondition("level_handed_out", $"level {hash} is running on {l.HandedTo}: a running instance keeps its level");
        return l;
    }

    public async Task<AdminResult> RetireLevel(string hash, CancellationToken ct = default)
    {
        var r = await Tx(async tx =>
        {
            var l = await NotHandedOut(tx, hash);
            var after = await tx.Levels.SetState(hash, LevelState.Retired, "retired by admin");
            await Audit(tx, "level.retire", hash, new { l.State }, new { after.State }, Actor);
            return AdminResult.Done($"retired level {hash}");
        }, ct);
        _c.PoolSignal?.Pulse();
        return r;
    }

    public async Task<AdminResult> DeleteLevel(string hash, CancellationToken ct = default)
    {
        // Retire (and audit) first, delete the files after the commit: a refusal never costs a file.
        LevelRecord? level = null;
        var r = await Tx(async tx =>
        {
            level = await NotHandedOut(tx, hash);
            if (level.State != LevelState.Retired) await tx.Levels.SetState(hash, LevelState.Retired, "deleted by admin");
            await Audit(tx, "level.delete", hash, new { level.State }, new { State = LevelState.Retired, FilesDeleted = true }, Actor);
            return AdminResult.Done($"deleted level {hash}");
        }, ct);
        if (!r.Ok || level is null) return r;
        var files = _c.Packs?.Storage.DeleteLevel(MapIdentity.MapName(options.Mod.Name, level.Depth, hash)) ?? 0;
        _c.PoolSignal?.Pulse();
        return AdminResult.Done($"deleted level {hash} ({files} file(s))", files);
    }

    public Task<AdminResult> RequeueJob(long jobId, CancellationToken ct = default) => Tx(async tx =>
    {
        var job = (await tx.MapJobs.List(take: 10_000)).FirstOrDefault(j => j.Id == jobId) ?? throw HostRefusal.NotFound("no_job", jobId.ToString());
        if (job.State is MapJobState.Queued or MapJobState.Running) throw HostRefusal.Precondition("job_live", $"job {jobId} is {job.State}");
        var again = await tx.MapJobs.Enqueue(job.Kind, job.Key, 0);
        await Audit(tx, "pool.requeue", job.Key, new { job.Id, job.State }, new { again.Id, again.State, again.Priority }, Actor);
        return AdminResult.Done($"requeued {job.Key} as job {again.Id}", again);
    }, ct);

    public async Task<AdminResult> SetPerDepth(int depth, int k, CancellationToken ct = default)
    {
        if (depth < 1 || depth > options.MapPool.Depths) return AdminResult.Refused("bad_depth", $"depth must be 1..{options.MapPool.Depths}");
        if (k < 0 || k > 50) return AdminResult.Refused("bad_k", "K must be 0..50");
        var r = await SetSetting($"MapPool.PerDepth.{depth}", k.ToString(), ct);
        _c.PoolSignal?.Pulse();
        return r;
    }

    // ------------------------------------------------------------------ room packs

    static async Task<AdminResult> Catalog(Func<Task<AdminResult>> work)
    {
        try { return await work(); }
        catch (HostRefusal r) { return AdminResult.Refused(r.Reason, r.Message); }
    }

    public Task<AdminResult> UploadPack(string mod, string library, Stream content, string? libraryJson, string? note, CancellationToken ct = default) =>
        _c.Packs is not { } packs ? Task.FromResult(Unavailable("the pack catalog")) : Catalog(async () =>
        {
            var up = await packs.UploadAsync(mod, library, content, libraryJson, note, PackCatalog.SourceUpload, Actor, ct);
            await Write(async tx =>
            {
                await tx.Audit.Write("pack.upload", $"{mod}/{library}", null, new { up.Pack.Id, up.Pack.Version, up.Pack.State, up.Pack.Sha256, up.Pack.Bytes }, Actor);
                return true;
            }, ct);
            return up.Accepted
                ? AdminResult.Done($"accepted {mod}/{library} as version {up.Pack.Version}", up.Pack)
                : AdminResult.Refused("rejected", $"rejected: {up.Pack.LintJson ?? "validation failed"}");
        });

    public Task<AdminResult> ActivatePack(string mod, IReadOnlyCollection<int> depths, long packId, bool pinned, OldLevelsPolicy policy, CancellationToken ct = default) =>
        _c.Packs is not { } packs ? Task.FromResult(Unavailable("the pack catalog")) : Catalog(async () =>
            AdminResult.Done($"activated pack {packId} on {depths.Count} depth(s)", await packs.ActivateAsync(mod, depths, packId, Actor, pinned, policy, ct: ct)));

    public Task<AdminResult> PinDepths(string mod, IReadOnlyCollection<int> depths, bool pinned, CancellationToken ct = default) =>
        _c.Packs is not { } packs ? Task.FromResult(Unavailable("the pack catalog")) : Catalog(async () =>
            AdminResult.Done($"{(pinned ? "pinned" : "unpinned")} {depths.Count} depth(s)", await packs.PinAsync(mod, depths, pinned, Actor, ct)));

    public Task<AdminResult> RollBackPack(string mod, IReadOnlyCollection<int> depths, long olderPackId, OldLevelsPolicy policy, CancellationToken ct = default) =>
        _c.Packs is not { } packs ? Task.FromResult(Unavailable("the pack catalog")) : Catalog(async () =>
            AdminResult.Done($"rolled {depths.Count} depth(s) back to pack {olderPackId}", await packs.RollBackAsync(mod, depths, olderPackId, Actor, policy, ct)));

    public Task<AdminResult> RetirePack(long packId, CancellationToken ct = default) =>
        _c.Packs is not { } packs ? Task.FromResult(Unavailable("the pack catalog")) : Catalog(async () =>
            AdminResult.Done($"retired pack {packId}", await packs.RetireAsync(packId, Actor, ct)));

    public Task<AdminResult> RebakePack(string mod, string library, CancellationToken ct = default) => Tx(async tx =>
    {
        var key = _c.Pool is { } pool && mod == options.Mod.Name
            ? pool.BakeKey(library, $"rebake-{tx.Now.ToUnixTimeSeconds()}")
            : $"bake:{mod}:{library}:rebake-{tx.Now.ToUnixTimeSeconds()}";
        var job = await tx.MapJobs.Enqueue(MapJobKind.Bake, key, 0);
        await Audit(tx, "pack.rebake", $"{mod}/{library}", null, new { job.Id, job.Key }, Actor);
        return AdminResult.Done($"queued bake job {job.Id} for {mod}/{library}", job);
    }, ct);

    public async Task<AdminResult> CollectPacks(string mod, CancellationToken ct = default)
    {
        if (_c.Pool is { } pool && mod == options.Mod.Name)
            return await Catalog(async () => AdminResult.Done($"collected {(await pool.CollectAsync(ct)).Count} level file set(s) and unreferenced packs"));
        if (_c.Packs is not { } packs) return Unavailable("the pack catalog");
        return await Catalog(async () => AdminResult.Done($"deleted {(await packs.CollectAsync(mod, ct)).Count} unreferenced pack file(s)"));
    }

    // ------------------------------------------------------------------ rules modules

    async Task<AdminResult> Module(string action, string sha256, Func<IAdminModules, Task> work, string message, CancellationToken ct)
    {
        if (_c.Modules is not { } modules) return Unavailable("the module registry");
        var before = await data.ReadAsync((tx, _) => tx.Modules.Get(sha256), ct);
        if (before is null) return AdminResult.Refused("unknown_module", sha256);
        return await External(action, sha256, new { before.State }, async () =>
        {
            await work(modules);
            var after = await data.ReadAsync((tx, _) => tx.Modules.Get(sha256), ct);
            return new { State = after?.State.ToString() ?? "deleted" };
        }, message);
    }

    public Task<AdminResult> ApproveModule(string sha256, CancellationToken ct = default) =>
        Module("module.approve", sha256, m => m.Approve(sha256, ct), $"approved {sha256[..Math.Min(12, sha256.Length)]}", ct);

    public async Task<AdminResult> QuarantineModule(string sha256, CancellationToken ct = default)
    {
        var r = await Module("module.quarantine", sha256, m => m.Quarantine(sha256, ct), $"quarantined {sha256[..Math.Min(12, sha256.Length)]}", ct);
        if (!r.Ok || _c.Lifecycle is not { } life) return r;
        var running = (await data.ReadAsync((tx, _) => tx.Instances.NonTerminal(), ct)).Where(i => i.RulesSha256 == sha256 && i.State != InstanceState.Draining).ToList();
        foreach (var i in running)
        {
            try { await life.Drain(i.Id, $"rules module {sha256} quarantined", Actor, ct); }
            catch (HostRefusal) { /* ended meanwhile */ }
        }
        return AdminResult.Done($"{r.Message}; draining {running.Count} instance(s)", running.Count);
    }

    public Task<AdminResult> DeleteModule(string sha256, CancellationToken ct = default) =>
        Module("module.delete", sha256, m => m.Delete(sha256, ct), $"deleted {sha256[..Math.Min(12, sha256.Length)]}", ct);

    // ------------------------------------------------------------------ operations

    public async Task<AdminResult> RunReconcile(bool fixCachedSums, CancellationToken ct = default)
    {
        if (_c.Ledger is not { } ledger) return Unavailable("the ledger");
        return await Tx(async tx =>
        {
            var findings = await ledger.Reconcile(tx, fixCachedSums);
            await Audit(tx, "ledger.reconcile", "ledger", null,
                new { findings = findings.Count, kinds = findings.GroupBy(f => f.Kind).ToDictionary(g => g.Key, g => g.Count()), fixCachedSums }, Actor);
            return AdminResult.Done($"reconcile: {findings.Count} finding(s)", findings);
        }, ct);
    }

    public Task<AdminResult> RunBackup(CancellationToken ct = default) => _c.Backups is not { } backups
        ? Task.FromResult(Unavailable("backups"))
        : External("backup.run", "data", null, async () => { var b = await backups.RunNow(ct); return new { b.Name, b.Bytes }; }, "backup written");

    public Task<AdminResult> RestoreBackupToFile(string name, CancellationToken ct = default) => _c.Backups is not { } backups
        ? Task.FromResult(Unavailable("backups"))
        : External("backup.restore_to_file", name, null, async () => new { path = await backups.RestoreToFile(name, ct) }, $"restored {name} to a file");

    public Task<AdminResult> SetSetting(string key, string valueJson, CancellationToken ct = default)
    {
        if (!IsRuntimeSetting(key)) return Task.FromResult(AdminResult.Refused("not_runtime", $"'{key}' is not a runtime setting"));
        try { using var _ = JsonDocument.Parse(valueJson); }
        catch (JsonException e) { return Task.FromResult(AdminResult.Refused("bad_json", e.Message)); }
        return Tx(async tx =>
        {
            var before = await tx.Settings.Get(key);
            await tx.Settings.Set(key, valueJson, Actor);
            await Audit(tx, "config.set", key, before is null ? null : new { before.ValueJson }, new { ValueJson = valueJson }, Actor);
            return AdminResult.Done($"{key} = {valueJson}");
        }, ct);
    }
}
