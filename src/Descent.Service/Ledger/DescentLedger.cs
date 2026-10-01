using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;

namespace Descent.Service.Ledger;

/// <summary>
/// The item ledger's policies (plan §5, R-H1): every item the game sees was minted here,
/// has exactly one owner, and is handed to the server by id. Each method runs inside the
/// caller's transaction (the API wraps it in IHostData.IdempotentAsync), so a check and the
/// move it guards commit together. Descent-specific by the Q2 tie-break: the reserve,
/// the kill-roll replay, the Lost &amp; Found fee and the hardcore rule are game rules.
/// </summary>
public sealed partial class DescentLedger(IRulesProvider rules, IOptions<ServiceOptions> options)
{
    ServiceOptions O => options.Value;

    /// <summary>The Lost &amp; Found fee: 25 % of vendor value (D6).</summary>
    public const double LostAndFoundFeeShare = 0.25;
    /// <summary>Buy-back keeps the last 12 per character per vendor (D-H4).</summary>
    public const int BuyBackKept = 12;

    public static string StashOwner(string account, bool hardcore) => $"{account}:{(hardcore ? "hc" : "sc")}";
    public static string VendorOwner(string hubInstance, string vendor) => $"{hubInstance}:{vendor}";
    public static string BuyBackOwner(string characterId, string vendor) => $"buyback:{characterId}:{vendor}";

    public IGameRules RulesFor(InstanceRecord instance) =>
        instance.RulesSha256 is { } sha ? rules.For(sha)
        : rules.Current ?? throw new HostRefusal(RefusalCode.FailedPrecondition, "no_rules_module", "no rules module is known yet");

    static ulong NewSeed() => BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));

    // ------------------------------------------------------------------ guards

    /// <summary>The caller holds the character's current lease (§4.3); a hub or level may only act on characters it leases.</summary>
    public static async Task<(CharacterRecord Character, Lease Lease)> Leased(IWriteTx tx, InstanceRecord caller, string characterId, byte[] token)
    {
        Lease lease;
        try { lease = await tx.Leases.Verify(characterId, token); }
        catch (HostRefusal e) when (e.Reason == "stale_lease")
        {
            await tx.Audit.Write("lease.stale", characterId, null, new { instance = caller.Id });
            throw;
        }
        if (lease.InstanceId != caller.Id)
            throw HostRefusal.Denied("wrong_instance", $"{characterId} is leased by {lease.InstanceId}, not {caller.Id}");
        var ch = await tx.Characters.Get(characterId) ?? throw HostRefusal.NotFound("no_character", characterId);
        return (ch, lease);
    }

    public static void RequireHub(InstanceRecord caller)
    {
        if (caller.Kind != InstanceKind.Hub) throw HostRefusal.Denied("not_a_hub", $"{caller.Id} is a level");
    }

    static CharacterSheet Sheet(CharacterRecord c) => new(c.Sheet, c.SheetVersion);

    RolledItem Decode(IGameRules r, ItemRecord i) => r.Items.Decode(i.Instance, i.SchemaVersion);

    static NewItem ToNew(RolledItem r, int rolledForLevel, string? forCharacter = null) =>
        new(r.Seed, r.BaseType, r.Rarity, r.ItemLevel, r.Count, r.Identified, r.Instance, r.SchemaVersion, r.Tier, rolledForLevel, forCharacter);

    async Task<IReadOnlyList<ItemRecord>> Backpack(IReadTx tx, string characterId) =>
        await tx.Items.Query(new ItemQuery(OwnerKind: OwnerKind.Character, OwnerId: characterId));

    /// <summary>The first free backpack slot, or no_capacity.</summary>
    async Task<int> FreeSlot(IReadTx tx, IGameRules r, CharacterRecord c)
    {
        var used = (await Backpack(tx, c.Id)).Select(i => i.Slot).ToHashSet();
        var slots = r.BackpackSlots(Sheet(c));
        for (var s = 0; s < slots; s++)
            if (!used.Contains(s)) return s;
        throw HostRefusal.Precondition("no_capacity", $"{c.Id}'s backpack is full ({slots})");
    }

    async Task<ItemRecord> Owned(IReadTx tx, string itemId, string characterId)
    {
        var item = await tx.Items.Get(itemId) ?? throw HostRefusal.NotFound("no_item", itemId);
        if (item.State != ItemState.Live || item.OwnerKind != OwnerKind.Character || item.OwnerId != characterId)
            throw HostRefusal.Precondition("wrong_owner", $"{itemId} is not in {characterId}'s backpack");
        return item;
    }

    // ------------------------------------------------------------------ reserve (D-H1, §5.2a)

    /// <summary>
    /// Mints reserve items for (character, instance), capped per tier at the configured size
    /// less what the character already holds in reserve there. Rolled for the character's
    /// level and the depth's item level; owned Reserve; returns only the new items.
    /// </summary>
    public async Task<IReadOnlyList<ItemRecord>> TakeReserve(IWriteTx tx, InstanceRecord caller, string characterId, byte[] token,
        IReadOnlyList<(int Tier, int Count)> wanted, string requestId)
    {
        var (ch, _) = await Leased(tx, caller, characterId, token);
        var r = RulesFor(caller);
        var held = (await tx.Items.Query(new ItemQuery(OwnerKind: OwnerKind.Reserve, OwnerId: characterId, InstanceId: caller.Id, Take: 10_000)))
            .GroupBy(i => i.Tier).ToDictionary(g => g.Key, g => g.Count());
        var row = r.LevelTable(caller.Depth);
        var minted = new List<ItemRecord>();
        foreach (var (tier, count) in wanted)
        {
            if (tier < 0 || tier >= r.Tiers.Count) throw new HostRefusal(RefusalCode.InvalidArgument, "bad_tier", $"tier {tier}");
            var size = O.Reserve.Sizes.TryGetValue(r.Tiers[tier], out var n) ? n : 0;
            var room = Math.Max(0, size - held.GetValueOrDefault(tier));
            for (var k = 0; k < Math.Min(count, room); k++)
            {
                var ctx = new ItemRollContext(ch.ClassName, ch.Level, caller.Depth, row.ItemLevel, tier, row.LootTable, "reserve");
                var rolled = r.RollItem(ctx, NewSeed());
                minted.Add(await tx.Items.Mint(ToNew(rolled, ch.Level, characterId), OwnerKind.Reserve, characterId, caller.Id, -1, "reserve", requestId));
            }
        }
        return minted;
    }

    /// <summary>
    /// A kill revealed from the reserve: the service replays the kill roll from the instance
    /// seed; the item must be this character's reserve item of the rolled tier, and each
    /// (instance, kill, character) reveals at most once. A reveal the roll does not reproduce
    /// destroys the item (forged_reveal, audited) and is refused. Books the kill's Australium
    /// once per member (R4).
    /// </summary>
    public async Task<(ItemRecord Item, long Australium)> Reveal(IWriteTx tx, InstanceRecord caller, string characterId, byte[] token,
        string itemId, uint killSeq, string robotTemplate, IReadOnlyList<string> nearby, string requestId)
    {
        var (_, _) = await Leased(tx, caller, characterId, token);
        var r = RulesFor(caller);
        var item = await tx.Items.Get(itemId) ?? throw HostRefusal.NotFound("no_item", itemId);
        if (item.State != ItemState.Live || item.OwnerKind != OwnerKind.Reserve || item.OwnerId != characterId || item.InstanceId != caller.Id)
            throw HostRefusal.Precondition("wrong_owner", $"{itemId} is not {characterId}'s reserve on {caller.Id}");

        var revealKind = $"reveal:{killSeq}:{characterId}";
        var roll = r.KillRoll(caller.Seed, killSeq, robotTemplate, caller.Depth, nearby.Count + 1);
        if (!roll.Drop || roll.Tier != item.Tier || await tx.Items.HasEvent(caller.Id, revealKind))
        {
            // A refusal rolls the transaction back, so the destruction is its own write; the
            // API runs ForgedReveal in a fresh transaction when it sees this reason.
            throw HostRefusal.Precondition("forged_reveal",
                $"kill {killSeq} of {robotTemplate} rolls drop={roll.Drop} tier={roll.Tier}; item {itemId} is tier {item.Tier}");
        }

        var moved = await tx.Items.Move(new ItemMove(itemId, OwnerKind.Reserve, OwnerKind.World, caller.Id, revealKind,
            InstanceId: caller.Id, RequestId: requestId));
        var booked = 0L;
        if (roll.Australium > 0)
        {
            var reference = $"kill:{caller.Id}:{killSeq}";
            foreach (var member in nearby.Prepend(characterId).Distinct())
            {
                if (await tx.Characters.HasAustraliumRef(member, reference)) continue;
                if (await tx.Characters.Get(member) is null) continue;
                await tx.Characters.AddAustralium(member, roll.Australium, "kill", reference);
                if (member == characterId) booked = roll.Australium;
            }
        }
        return (moved, booked);
    }

    /// <summary>The destruction half of a forged reveal, in its own transaction after the refusal.</summary>
    public static async Task ForgedReveal(IWriteTx tx, InstanceRecord caller, string itemId, uint killSeq, string requestId)
    {
        var item = await tx.Items.Get(itemId);
        if (item is null || item.State != ItemState.Live || item.OwnerKind != OwnerKind.Reserve || item.InstanceId != caller.Id) return;
        await tx.Items.Terminate(itemId, OwnerKind.Reserve, ItemState.Destroyed, "forged_reveal", requestId);
        await tx.Audit.Write("item.forged_reveal", itemId, null, new { instance = caller.Id, killSeq });
    }

    /// <summary>
    /// A synchronous mint (§5.2): tier ≥ 0 is the empty-reserve fallback for one kill (the
    /// kill roll must give that tier); tier -1 is a boss or champion table, per player.
    /// Each (instance, kill) mints at most once.
    /// </summary>
    public async Task<IReadOnlyList<ItemRecord>> MintDrops(IWriteTx tx, InstanceRecord caller, string killerId, byte[] token,
        string robotTemplate, IReadOnlyList<string> nearby, uint killSeq, int tier, string requestId)
    {
        var (killer, _) = await Leased(tx, caller, killerId, token);
        var r = RulesFor(caller);
        var kind = $"mintdrops:{killSeq}";
        if (await tx.Items.HasEvent(caller.Id, kind))
            throw HostRefusal.Precondition("forged_reveal", $"kill {killSeq} already minted");
        var row = r.LevelTable(caller.Depth);
        var minted = new List<ItemRecord>();
        if (tier >= 0)
        {
            var roll = r.KillRoll(caller.Seed, killSeq, robotTemplate, caller.Depth, nearby.Count + 1);
            if (!roll.Drop || roll.Tier != tier)
                throw HostRefusal.Precondition("forged_reveal", $"kill {killSeq} rolls drop={roll.Drop} tier={roll.Tier}, not {tier}");
            var ctx = new ItemRollContext(killer.ClassName, killer.Level, caller.Depth, row.ItemLevel, tier, row.LootTable, "fallback");
            var item = await tx.Items.Mint(ToNew(r.RollItem(ctx, NewSeed()), killer.Level, killerId), OwnerKind.World, caller.Id, caller.Id, -1, "fallback", requestId);
            minted.Add(item);
        }
        else
        {
            foreach (var member in nearby.Prepend(killerId).Distinct())
            {
                var lease = await tx.Leases.Get(member);
                if (lease?.InstanceId != caller.Id) continue; // only members on this level
                var c = await tx.Characters.Get(member);
                if (c is null) continue;
                var ctx = new ItemRollContext(c.ClassName, c.Level, caller.Depth, row.ItemLevel, -1, row.LootTable, "boss:" + robotTemplate);
                foreach (var drop in r.BossDrops(ctx, robotTemplate, NewSeed()))
                    minted.Add(await tx.Items.Mint(ToNew(drop, c.Level, member), OwnerKind.World, caller.Id, caller.Id, -1, "boss", requestId));
            }
        }
        // The once-only marker: an event on the first minted item carries the kill.
        if (minted.Count > 0)
            await tx.Items.Move(new ItemMove(minted[0].Id, OwnerKind.World, OwnerKind.World, caller.Id, kind, InstanceId: caller.Id, RequestId: requestId));
        return minted.Count > 0 ? [await tx.Items.Get(minted[0].Id) ?? minted[0], .. minted.Skip(1)] : minted;
    }

    /// <summary>§5.2a scope: a reserve belongs to (character, instance) and is swept when that lease ends.</summary>
    public static async Task<int> SweepReserve(IWriteTx tx, string characterId, string instanceId, string reason)
    {
        var items = await tx.Items.Query(new ItemQuery(OwnerKind: OwnerKind.Reserve, OwnerId: characterId, InstanceId: instanceId, Take: 10_000));
        foreach (var i in items)
            await tx.Items.Terminate(i.Id, OwnerKind.Reserve, ItemState.Swept, reason, null);
        return items.Count;
    }

    /// <summary>§5.2a level-up: tiers whose rolled level is StaleLevels or more below the new level are swept.</summary>
    public async Task<IReadOnlyList<int>> SweepStaleTiers(IWriteTx tx, string characterId, string instanceId, int newLevel)
    {
        var items = await tx.Items.Query(new ItemQuery(OwnerKind: OwnerKind.Reserve, OwnerId: characterId, InstanceId: instanceId, Take: 10_000));
        var stale = items.Where(i => newLevel - i.RolledForLevel >= O.Reserve.StaleLevels).ToList();
        foreach (var i in stale)
            await tx.Items.Terminate(i.Id, OwnerKind.Reserve, ItemState.Swept, "reserve_stale", null);
        return stale.Select(i => i.Tier).Distinct().Order().ToList();
    }

    // ------------------------------------------------------------------ pickup, drop, moves

    /// <summary>§5.2: requires World, this instance, for_character null or the caller, and capacity.</summary>
    public async Task<ItemRecord> Claim(IWriteTx tx, InstanceRecord caller, string characterId, byte[] token, string itemId, string requestId)
    {
        var (ch, _) = await Leased(tx, caller, characterId, token);
        var r = RulesFor(caller);
        var item = await tx.Items.Get(itemId) ?? throw HostRefusal.NotFound("no_item", itemId);
        if (item.ForCharacter is { } owner && owner != characterId)
            throw HostRefusal.Denied("wrong_owner", $"{itemId} dropped for {owner}");
        var slot = await FreeSlot(tx, r, ch);
        return await tx.Items.Move(new ItemMove(itemId, OwnerKind.World, OwnerKind.Character, characterId, "claim",
            Slot: slot, ClearForCharacter: true, RequestId: requestId, ExpectedInstanceId: caller.Id));
    }

    public async Task<ItemRecord> Drop(IWriteTx tx, InstanceRecord caller, string characterId, byte[] token, string itemId, string requestId)
    {
        await Leased(tx, caller, characterId, token);
        await Owned(tx, itemId, characterId);
        return await tx.Items.Move(new ItemMove(itemId, OwnerKind.Character, OwnerKind.World, caller.Id, "drop",
            InstanceId: caller.Id, ClearForCharacter: true, RequestId: requestId));
    }

    /// <summary>Backpack / equip / belt: a slot change inside the character; refused onto an occupied slot.</summary>
    public async Task<ItemRecord> Move(IWriteTx tx, InstanceRecord caller, string characterId, byte[] token, string itemId, int slot, string requestId)
    {
        var (ch, _) = await Leased(tx, caller, characterId, token);
        await Owned(tx, itemId, characterId);
        var r = RulesFor(caller);
        if (slot < 0 || slot >= r.BackpackSlots(Sheet(ch)))
            throw new HostRefusal(RefusalCode.InvalidArgument, "bad_slot", $"slot {slot}");
        if ((await Backpack(tx, characterId)).Any(i => i.Slot == slot && i.Id != itemId))
            throw HostRefusal.Precondition("slot_taken", $"slot {slot} of {characterId}");
        return await tx.Items.Move(new ItemMove(itemId, OwnerKind.Character, OwnerKind.Character, characterId, "move", Slot: slot, RequestId: requestId));
    }

    /// <summary>Backpack ↔ shared stash (hub only; capacity from the living characters of the account, D4).</summary>
    public async Task<ItemRecord> StashMove(IWriteTx tx, InstanceRecord caller, string characterId, byte[] token, string itemId, bool toStash, int slot, string requestId)
    {
        RequireHub(caller);
        var (ch, _) = await Leased(tx, caller, characterId, token);
        var r = RulesFor(caller);
        var stashOwner = StashOwner(ch.Account, ch.Hardcore);
        if (toStash)
        {
            await Owned(tx, itemId, characterId);
            var living = (await tx.Characters.List(ch.Account)).Where(c => c.Hardcore == ch.Hardcore && !c.Fallen).Select(Sheet).ToList();
            var cap = r.StashSlots(living);
            var used = (await tx.Items.Query(new ItemQuery(OwnerKind: OwnerKind.Stash, OwnerId: stashOwner, Take: 10_000))).Select(i => i.Slot).ToHashSet();
            if (slot < 0) slot = Enumerable.Range(0, cap).FirstOrDefault(s => !used.Contains(s), -1);
            if (slot < 0 || slot >= cap) throw HostRefusal.Precondition("no_capacity", $"stash {stashOwner} is full ({cap})");
            if (used.Contains(slot)) throw HostRefusal.Precondition("slot_taken", $"stash slot {slot}");
            return await tx.Items.Move(new ItemMove(itemId, OwnerKind.Character, OwnerKind.Stash, stashOwner, "stash_in", Slot: slot, RequestId: requestId));
        }
        var free = await FreeSlot(tx, r, ch);
        var item = await tx.Items.Get(itemId) ?? throw HostRefusal.NotFound("no_item", itemId);
        if (item.OwnerKind != OwnerKind.Stash || item.OwnerId != stashOwner)
            throw HostRefusal.Precondition("wrong_owner", $"{itemId} is not in {stashOwner}");
        return await tx.Items.Move(new ItemMove(itemId, OwnerKind.Stash, OwnerKind.Character, characterId, "stash_out", Slot: free, RequestId: requestId));
    }

    // ------------------------------------------------------------------ vendors (D-H4)

    /// <summary>The hourly roll: unsold stock of that hub is swept, new stock minted Vendor-owned.</summary>
    public async Task<int> RollVendor(IWriteTx tx, InstanceRecord hub, string vendor)
    {
        RequireHub(hub);
        var r = RulesFor(hub);
        var owner = VendorOwner(hub.Id, vendor);
        foreach (var old in await tx.Items.Query(new ItemQuery(OwnerKind: OwnerKind.Vendor, OwnerId: owner, Take: 10_000)))
            await tx.Items.Terminate(old.Id, OwnerKind.Vendor, ItemState.Swept, "vendor_roll", null);
        await tx.Vendors.Clear(hub.Id, vendor);
        var stock = r.VendorStock(vendor, NewSeed());
        for (var i = 0; i < stock.Count; i++)
        {
            var item = await tx.Items.Mint(ToNew(stock[i], 0), OwnerKind.Vendor, owner, hub.Id, i, "vendor", null);
            await tx.Vendors.Add(hub.Id, vendor, item.Id);
        }
        return stock.Count;
    }

    public async Task<ItemRecord> Buy(IWriteTx tx, InstanceRecord caller, string characterId, byte[] token, string itemId, string vendor, string requestId)
    {
        RequireHub(caller);
        var (ch, _) = await Leased(tx, caller, characterId, token);
        var r = RulesFor(caller);
        var item = await tx.Items.Get(itemId) ?? throw HostRefusal.NotFound("no_item", itemId);
        if (item.OwnerKind != OwnerKind.Vendor || item.OwnerId != VendorOwner(caller.Id, vendor))
            throw HostRefusal.Precondition("wrong_owner", $"{itemId} is not {vendor}'s stock here");
        var slot = await FreeSlot(tx, r, ch);
        await tx.Characters.AddAustralium(characterId, -r.Price(Decode(r, item), PriceKind.Buy), "buy", itemId);
        return await tx.Items.Move(new ItemMove(itemId, OwnerKind.Vendor, OwnerKind.Character, characterId, "buy", Slot: slot, InstanceId: null, RequestId: requestId));
    }

    /// <summary>Sold items go to that vendor's buy-back for the character; the oldest beyond 12 are consumed.</summary>
    public async Task<long> Sell(IWriteTx tx, InstanceRecord caller, string characterId, byte[] token, string itemId, string vendor, string requestId)
    {
        RequireHub(caller);
        await Leased(tx, caller, characterId, token);
        var r = RulesFor(caller);
        var item = await Owned(tx, itemId, characterId);
        var price = r.Price(Decode(r, item), PriceKind.Sell);
        var buyBack = BuyBackOwner(characterId, vendor);
        var kept = await tx.Items.Query(new ItemQuery(OwnerKind: OwnerKind.Vendor, OwnerId: buyBack, Take: 10_000));
        var next = kept.Count == 0 ? 0 : kept.Max(i => i.Slot) + 1;
        await tx.Items.Move(new ItemMove(itemId, OwnerKind.Character, OwnerKind.Vendor, buyBack, "sell", Slot: next, InstanceId: caller.Id, RequestId: requestId));
        foreach (var old in kept.OrderBy(i => i.Slot).Take(Math.Max(0, kept.Count + 1 - BuyBackKept)))
            await tx.Items.Terminate(old.Id, OwnerKind.Vendor, ItemState.Consumed, "buyback_expired", requestId);
        return await tx.Characters.AddAustralium(characterId, price, "sell", itemId);
    }

    public async Task<ItemRecord> BuyBack(IWriteTx tx, InstanceRecord caller, string characterId, byte[] token, string itemId, string vendor, string requestId)
    {
        RequireHub(caller);
        var (ch, _) = await Leased(tx, caller, characterId, token);
        var r = RulesFor(caller);
        var item = await tx.Items.Get(itemId) ?? throw HostRefusal.NotFound("no_item", itemId);
        if (item.OwnerKind != OwnerKind.Vendor || item.OwnerId != BuyBackOwner(characterId, vendor))
            throw HostRefusal.Precondition("wrong_owner", $"{itemId} is not in {characterId}'s buy-back at {vendor}");
        var slot = await FreeSlot(tx, r, ch);
        await tx.Characters.AddAustralium(characterId, -r.Price(Decode(r, item), PriceKind.Sell), "buyback", itemId);
        return await tx.Items.Move(new ItemMove(itemId, OwnerKind.Vendor, OwnerKind.Character, characterId, "buyback", Slot: slot, RequestId: requestId));
    }

    // ------------------------------------------------------------------ item operations (D22–D24)

    /// <summary>Crates, crafts and salvage consume inputs and mint outputs; identify and repair change the item in place.</summary>
    public async Task<IReadOnlyList<ItemRecord>> Transform(IWriteTx tx, InstanceRecord caller, string characterId, byte[] token,
        string operation, IReadOnlyList<string> inputIds, string? recipe, string requestId)
    {
        var (ch, _) = await Leased(tx, caller, characterId, token);
        var r = RulesFor(caller);
        var inputs = new List<ItemRecord>();
        foreach (var id in inputIds.Distinct()) inputs.Add(await Owned(tx, id, characterId));
        if (inputs.Count == 0 || inputs.Count != inputIds.Count)
            throw new HostRefusal(RefusalCode.InvalidArgument, "bad_inputs", "inputs must be distinct items of the caller");
        var rolled = inputs.Select(i => Decode(r, i)).ToList();
        var row = r.LevelTable(Math.Max(caller.Depth, 1));
        var ctx = new ItemRollContext(ch.ClassName, ch.Level, caller.Depth, row.ItemLevel, 0, row.LootTable, operation);
        var change = operation switch
        {
            "crate" => r.OpenCrate(rolled[0], ctx, NewSeed()),
            "craft" => r.Craft(recipe ?? "", rolled, ctx, NewSeed()),
            "identify" => r.Identify(rolled[0], NewSeed()),
            "repair" => r.Repair(rolled[0]),
            "salvage" => r.Salvage(rolled[0], ctx, NewSeed()),
            _ => throw new HostRefusal(RefusalCode.InvalidArgument, "bad_operation", operation),
        };
        if (!change.Ok) throw HostRefusal.Precondition(change.Refusal!, $"{operation} refused by the rules");
        if (change.Cost > 0) await tx.Characters.AddAustralium(characterId, -change.Cost, operation, inputs[0].Id);

        // identify / repair: the item itself changes, it is not a mint (§5.2).
        if (operation is "identify" or "repair")
        {
            var p = change.Produced[0];
            return [await tx.Items.Mutate(inputs[0].Id, inputs[0].Version, p.Instance, p.SchemaVersion, p.Identified, false, operation, requestId)];
        }
        foreach (var index in change.Consumed)
            await tx.Items.Terminate(inputs[index].Id, OwnerKind.Character, ItemState.Consumed, operation, requestId);
        var result = new List<ItemRecord>();
        foreach (var p in change.Produced)
        {
            var slot = await FreeSlot(tx, r, ch);
            result.Add(await tx.Items.Mint(ToNew(p, ch.Level), OwnerKind.Character, characterId, null, slot, operation, requestId));
        }
        return result;
    }

    // ------------------------------------------------------------------ death, corpse, Lost & Found (D6, D7)

    /// <summary>Everything carried becomes Corpse-owned on this instance; the Australium held moves onto the corpse.</summary>
    public async Task<IReadOnlyList<ItemRecord>> RecordDeath(IWriteTx tx, InstanceRecord caller, string characterId, byte[] token, string requestId)
    {
        var (ch, _) = await Leased(tx, caller, characterId, token);
        var carried = await Backpack(tx, characterId);
        var moved = new List<ItemRecord>();
        foreach (var i in carried)
            moved.Add(await tx.Items.Move(new ItemMove(i.Id, OwnerKind.Character, OwnerKind.Corpse, characterId, "death", Slot: i.Slot, InstanceId: caller.Id, RequestId: requestId)));
        if (ch.Australium > 0)
            await tx.Characters.AddAustralium(characterId, -ch.Australium, "corpse", $"corpse:{caller.Id}");
        if (ch.Hardcore)
            await tx.Characters.Update(characterId, ch.Version, c => c with { Fallen = true });
        await tx.Audit.Write("character.death", characterId, null, new { instance = caller.Id, items = moved.Count, australium = ch.Australium });
        return moved;
    }

    /// <summary>The character's own corpse on this level: as many items as fit come back, the rest stay; the Australium comes back.</summary>
    public async Task<IReadOnlyList<ItemRecord>> LootCorpse(IWriteTx tx, InstanceRecord caller, string characterId, byte[] token, string requestId)
    {
        var (ch, _) = await Leased(tx, caller, characterId, token);
        var r = RulesFor(caller);
        var corpse = await tx.Items.Query(new ItemQuery(OwnerKind: OwnerKind.Corpse, OwnerId: characterId, InstanceId: caller.Id, Take: 10_000));
        var looted = new List<ItemRecord>();
        foreach (var i in corpse)
        {
            int slot;
            try { slot = await FreeSlot(tx, r, ch); }
            catch (HostRefusal) { break; }
            looted.Add(await tx.Items.Move(new ItemMove(i.Id, OwnerKind.Corpse, OwnerKind.Character, characterId, "loot_corpse", Slot: slot, RequestId: requestId)));
        }
        await ReturnCorpseAustralium(tx, characterId, caller.Id, "loot_corpse");
        return looted;
    }

    static async Task ReturnCorpseAustralium(IWriteTx tx, string characterId, string instanceId, string reason)
    {
        var reference = $"corpse:{instanceId}";
        var held = -(await tx.Characters.AustraliumLedger(characterId)).Where(e => e.Ref == reference).Sum(e => e.Delta);
        if (held > 0) await tx.Characters.AddAustralium(characterId, held, reason, reference);
    }

    public async Task<ItemRecord> Reclaim(IWriteTx tx, InstanceRecord caller, string characterId, byte[] token, string entryId, string requestId)
    {
        RequireHub(caller);
        var (ch, _) = await Leased(tx, caller, characterId, token);
        var r = RulesFor(caller);
        var entry = await tx.LostAndFound.Get(entryId) ?? throw HostRefusal.NotFound("no_item", entryId);
        if (entry.CharacterId != characterId) throw HostRefusal.Denied("wrong_owner", $"{entryId} belongs to {entry.CharacterId}");
        var slot = await FreeSlot(tx, r, ch);
        if (entry.Fee > 0) await tx.Characters.AddAustralium(characterId, -entry.Fee, "lost_and_found", entry.ItemId);
        await tx.LostAndFound.Remove(entry.Id);
        return await tx.Items.Move(new ItemMove(entry.ItemId, OwnerKind.LostAndFound, OwnerKind.Character, characterId, "reclaim", Slot: slot, RequestId: requestId));
    }

    // ------------------------------------------------------------------ the sweep (§5.3)

    public sealed record SweepCounts(int World, int Reserve, int ToLostAndFound, int CorpseDestroyed, int Vendor, int EscrowReturned, int TradesCancelled);

    /// <summary>
    /// Inside the reap transition, once, in one transaction; idempotent (a second run finds
    /// nothing Live to move). World and Reserve items of the instance are swept; corpses go
    /// to Lost &amp; Found at 25 % of vendor value, a hardcore character's are destroyed; a hub's
    /// vendor stock is swept and its open trades cancelled with escrow returned; one audit row.
    /// </summary>
    public async Task<SweepCounts> SweepInstance(IWriteTx tx, InstanceRecord instance)
    {
        var world = 0; var reserve = 0; var lf = 0; var destroyed = 0; var vendor = 0; var escrow = 0; var cancelled = 0;
        IGameRules? r = null;
        try { r = RulesFor(instance); } catch (HostRefusal) { }

        foreach (var i in await tx.Items.Query(new ItemQuery(OwnerKind: OwnerKind.World, InstanceId: instance.Id, Take: 100_000)))
        { await tx.Items.Terminate(i.Id, OwnerKind.World, ItemState.Swept, "level_shutdown", null); world++; }
        foreach (var i in await tx.Items.Query(new ItemQuery(OwnerKind: OwnerKind.Reserve, InstanceId: instance.Id, Take: 100_000)))
        { await tx.Items.Terminate(i.Id, OwnerKind.Reserve, ItemState.Swept, "level_shutdown", null); reserve++; }

        var corpses = await tx.Items.Query(new ItemQuery(OwnerKind: OwnerKind.Corpse, InstanceId: instance.Id, Take: 100_000));
        foreach (var byOwner in corpses.GroupBy(i => i.OwnerId))
        {
            var ch = await tx.Characters.Get(byOwner.Key);
            foreach (var i in byOwner)
            {
                if (ch is null || ch.Hardcore)
                {
                    await tx.Items.Terminate(i.Id, OwnerKind.Corpse, ItemState.Destroyed, "hardcore_death", null);
                    destroyed++;
                    continue;
                }
                var fee = r is null ? 0 : (long)Math.Round(r.Price(r.Items.Decode(i.Instance, i.SchemaVersion), PriceKind.VendorValue) * LostAndFoundFeeShare);
                await tx.Items.Move(new ItemMove(i.Id, OwnerKind.Corpse, OwnerKind.LostAndFound, ch.Id, "lost_and_found", InstanceId: null));
                await tx.LostAndFound.Add(ch.Id, i.Id, fee, instance.Id);
                lf++;
            }
            if (ch is { Hardcore: false }) await ReturnCorpseAustralium(tx, ch.Id, instance.Id, "lost_and_found");
        }

        if (instance.Kind == InstanceKind.Hub)
        {
            foreach (var i in await tx.Items.Query(new ItemQuery(OwnerKind: OwnerKind.Vendor, InstanceId: instance.Id, Take: 100_000)))
            {
                if (i.OwnerId.StartsWith("buyback:", StringComparison.Ordinal)) continue; // buy-back belongs to the character
                await tx.Items.Terminate(i.Id, OwnerKind.Vendor, ItemState.Swept, "hub_reap", null);
                vendor++;
            }
            await tx.Vendors.Clear(instance.Id);
            foreach (var t in await tx.Trades.ForHub(instance.Id))
            {
                escrow += await ReturnEscrow(tx, t);
                await tx.Trades.Save(t with { State = TradeStatus.Cancelled });
                cancelled++;
            }
        }

        var counts = new SweepCounts(world, reserve, lf, destroyed, vendor, escrow, cancelled);
        await tx.Audit.Write("instance.sweep", instance.Id, null, counts);
        return counts;
    }

    // ------------------------------------------------------------------ trades (2d, two-phase)

    public static async Task<int> ReturnEscrow(IWriteTx tx, TradeRecord t)
    {
        var n = 0;
        foreach (var i in await tx.Items.Query(new ItemQuery(OwnerKind: OwnerKind.TradeEscrow, OwnerId: t.Id, Take: 1000)))
        {
            var back = t.OfferA.Contains(i.Id) ? t.A : t.B;
            await tx.Items.Move(new ItemMove(i.Id, OwnerKind.TradeEscrow, OwnerKind.Character, back, "trade_return", Slot: i.Slot));
            n++;
        }
        return n;
    }

    public async Task<TradeRecord> TradeOpen(IWriteTx tx, InstanceRecord caller, string characterId, byte[] token, string other, string requestId)
    {
        RequireHub(caller);
        await Leased(tx, caller, characterId, token);
        var otherLease = await tx.Leases.Get(other);
        if (otherLease?.InstanceId != caller.Id) throw HostRefusal.Precondition("trade_state", $"{other} is not in this hub");
        return await tx.Trades.Open(caller.Id, characterId, other);
    }

    async Task<(TradeRecord Trade, bool IsA)> TradeFor(IWriteTx tx, InstanceRecord caller, string characterId, byte[] token, string tradeId)
    {
        RequireHub(caller);
        await Leased(tx, caller, characterId, token);
        var t = await tx.Trades.Get(tradeId) ?? throw HostRefusal.NotFound("no_trade", tradeId);
        if (t.HubInstance != caller.Id || (t.A != characterId && t.B != characterId))
            throw HostRefusal.Denied("wrong_owner", $"{characterId} is not in trade {tradeId}");
        if (t.State is TradeStatus.Committed or TradeStatus.Cancelled)
            throw HostRefusal.Precondition("trade_state", $"trade {tradeId} is {t.State}");
        return (t, t.A == characterId);
    }

    /// <summary>The caller's full offer; refused once either side has locked.</summary>
    public async Task<TradeRecord> TradeOffer(IWriteTx tx, InstanceRecord caller, string characterId, byte[] token, string tradeId,
        IReadOnlyList<string> itemIds, long australium, string requestId)
    {
        var (t, isA) = await TradeFor(tx, caller, characterId, token, tradeId);
        if (t.LockedA || t.LockedB) throw HostRefusal.Precondition("trade_state", "offers are locked");
        foreach (var id in itemIds) await Owned(tx, id, characterId);
        if (australium < 0) throw new HostRefusal(RefusalCode.InvalidArgument, "bad_amount", "negative Australium");
        return await tx.Trades.Save(isA ? t with { OfferA = [.. itemIds], AustraliumA = australium }
                                        : t with { OfferB = [.. itemIds], AustraliumB = australium });
    }

    /// <summary>Locking moves the caller's offered items into escrow.</summary>
    public async Task<TradeRecord> TradeLock(IWriteTx tx, InstanceRecord caller, string characterId, byte[] token, string tradeId, string requestId)
    {
        var (t, isA) = await TradeFor(tx, caller, characterId, token, tradeId);
        if (isA ? t.LockedA : t.LockedB) return t;
        foreach (var id in isA ? t.OfferA : t.OfferB)
        {
            var i = await Owned(tx, id, characterId);
            await tx.Items.Move(new ItemMove(id, OwnerKind.Character, OwnerKind.TradeEscrow, t.Id, "trade_lock", Slot: i.Slot, RequestId: requestId));
        }
        t = isA ? t with { LockedA = true } : t with { LockedB = true };
        return await tx.Trades.Save(t.LockedA && t.LockedB ? t with { State = TradeStatus.Locked } : t);
    }

    /// <summary>Both locked, both confirm; the second Confirm commits through IGameRules.ApplyTrade.</summary>
    public async Task<TradeRecord> TradeConfirm(IWriteTx tx, InstanceRecord caller, string characterId, byte[] token, string tradeId, string requestId)
    {
        var (t, isA) = await TradeFor(tx, caller, characterId, token, tradeId);
        if (!(t.LockedA && t.LockedB)) throw HostRefusal.Precondition("trade_state", "both sides must lock before confirming");
        t = isA ? t with { ConfirmedA = true } : t with { ConfirmedB = true };
        if (!(t.ConfirmedA && t.ConfirmedB)) return await tx.Trades.Save(t);

        var r = RulesFor(caller);
        var a = await tx.Characters.Get(t.A) ?? throw HostRefusal.NotFound("no_character", t.A);
        var b = await tx.Characters.Get(t.B) ?? throw HostRefusal.NotFound("no_character", t.B);
        var escrow = await tx.Items.Query(new ItemQuery(OwnerKind: OwnerKind.TradeEscrow, OwnerId: t.Id, Take: 1000));
        var offerA = escrow.Where(i => t.OfferA.Contains(i.Id)).ToList();
        var offerB = escrow.Where(i => t.OfferB.Contains(i.Id)).ToList();
        int Free(CharacterRecord c) => r.BackpackSlots(Sheet(c));
        var usedA = (await Backpack(tx, a.Id)).Count;
        var usedB = (await Backpack(tx, b.Id)).Count;
        var verdict = r.ApplyTrade(new TradeState(Sheet(a), Sheet(b), offerA.Select(i => Decode(r, i)).ToList(), offerB.Select(i => Decode(r, i)).ToList(),
            t.AustraliumA, t.AustraliumB, Free(a) - usedA, Free(b) - usedB));
        if (!verdict.Ok) throw HostRefusal.Precondition("no_capacity", verdict.Reason ?? "trade refused by the rules");

        if (t.AustraliumA > 0) { await tx.Characters.AddAustralium(a.Id, -t.AustraliumA, "trade", t.Id); await tx.Characters.AddAustralium(b.Id, t.AustraliumA, "trade", t.Id); }
        if (t.AustraliumB > 0) { await tx.Characters.AddAustralium(b.Id, -t.AustraliumB, "trade", t.Id); await tx.Characters.AddAustralium(a.Id, t.AustraliumB, "trade", t.Id); }
        foreach (var i in offerA)
            await tx.Items.Move(new ItemMove(i.Id, OwnerKind.TradeEscrow, OwnerKind.Character, b.Id, "trade", Slot: await FreeSlot(tx, r, b), RequestId: requestId));
        foreach (var i in offerB)
            await tx.Items.Move(new ItemMove(i.Id, OwnerKind.TradeEscrow, OwnerKind.Character, a.Id, "trade", Slot: await FreeSlot(tx, r, a), RequestId: requestId));
        return await tx.Trades.Save(t with { State = TradeStatus.Committed, Committed = tx.Now });
    }

    public async Task<TradeRecord> TradeCancel(IWriteTx tx, InstanceRecord caller, string characterId, byte[] token, string tradeId, string requestId)
    {
        var (t, _) = await TradeFor(tx, caller, characterId, token, tradeId);
        await ReturnEscrow(tx, t);
        return await tx.Trades.Save(t with { State = TradeStatus.Cancelled });
    }

    // ------------------------------------------------------------------ reconcile (§5.4)

    public sealed record Finding(string Kind, string Target, string Detail);

    /// <summary>Nightly and on demand: nothing auto-fixed but the cached Australium sum.</summary>
    public static async Task<IReadOnlyList<Finding>> Reconcile(IWriteTx tx, bool fixCachedSums)
    {
        var findings = new List<Finding>();
        var (minted, terminal, live) = await tx.Items.Totals();
        if (minted - terminal != live) findings.Add(new("ledger_totals", "items", $"minted {minted} − terminal {terminal} ≠ live {live}"));

        for (var skip = 0; ; skip += 500)
        {
            var page = await tx.Characters.ListAll(skip, 500);
            if (page.Count == 0) break;
            foreach (var c in page)
            {
                var sum = await tx.Characters.AustraliumLedgerSum(c.Id);
                if (sum == c.Australium) continue;
                findings.Add(new("australium_cache", c.Id, $"cached {c.Australium}, ledger {sum}"));
                if (fixCachedSums) await tx.Characters.RecomputeAustralium(c.Id);
            }
        }

        var instances = (await tx.Instances.List(take: 100_000)).ToDictionary(i => i.Id);
        foreach (var i in await tx.Items.Query(new ItemQuery(OwnerKind: OwnerKind.World, Take: 100_000)))
            if (i.InstanceId is null || !instances.TryGetValue(i.InstanceId, out var inst) || inst.Terminal)
                findings.Add(new("world_item_orphan", i.Id, $"instance {i.InstanceId ?? "none"} is not live"));
        foreach (var i in await tx.Items.Query(new ItemQuery(OwnerKind: OwnerKind.Character, Take: 100_000)))
            if (await tx.Characters.Get(i.OwnerId) is null)
                findings.Add(new("owner_missing", i.Id, $"character {i.OwnerId} does not exist"));
        foreach (var l in await tx.Leases.All())
            if (!instances.TryGetValue(l.InstanceId, out var inst) || inst.Terminal)
                findings.Add(new("lease_orphan", l.CharacterId, $"instance {l.InstanceId} is not live"));
        return findings;
    }
}
