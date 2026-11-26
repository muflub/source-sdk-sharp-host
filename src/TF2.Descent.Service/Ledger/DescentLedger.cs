using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;

namespace TF2.Descent.Service.Ledger;

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

    /// <summary>Everything the character carries: backpack, equipped and belt.</summary>
    static async Task<IReadOnlyList<ItemRecord>> Carried(IReadTx tx, string characterId) =>
        await tx.Items.Query(new ItemQuery(OwnerKind: OwnerKind.Character, OwnerId: characterId));

    /// <summary>The character's items in backpack cells (pickups, mints and trades land there; equipped and belt items take none).</summary>
    static async Task<IReadOnlyList<ItemRecord>> Backpack(IReadTx tx, string characterId) =>
        (await Carried(tx, characterId)).Where(i => i.Container == ItemContainer.Backpack).ToList();

    /// <summary>The rules' refusal of a placement, as FailedPrecondition with the rules' reason (plan_sdk_2_7 §1).</summary>
    static void CheckPlace(IGameRules r, CharacterRecord c, RolledItem item, ItemContainer container, int slot)
    {
        if (r.PlaceRefusal(Sheet(c), item, container, slot) is { } reason)
            throw HostRefusal.Precondition(reason, $"{item.BaseType} may not go in {container} slot {slot}");
    }

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
        IReadOnlyList<(int Tier, int Count)> wanted, string requestId, bool includeHeld = false)
    {
        var (ch, _) = await Leased(tx, caller, characterId, token);
        var r = RulesFor(caller);
        var heldItems = await tx.Items.Query(new ItemQuery(OwnerKind: OwnerKind.Reserve, OwnerId: characterId, InstanceId: caller.Id, Take: 10_000));
        var held = heldItems.GroupBy(i => i.Tier).ToDictionary(g => g.Key, g => g.Count());
        var row = r.LevelTable(caller.Depth);
        // §5.2a: a reserve outlives the lease; a returning character's first take gets its pre-rolls back.
        var wantedTiers = wanted.Select(w => w.Tier).ToHashSet();
        var minted = includeHeld ? heldItems.Where(i => wantedTiers.Contains(i.Tier)).ToList() : new List<ItemRecord>();
        foreach (var (tier, count) in wanted)
        {
            if (tier < 0 || tier >= r.Tiers.Count) throw new HostRefusal(RefusalCode.InvalidArgument, "bad_tier", $"tier {tier}");
            var size = O.Reserve.Sizes.TryGetValue(r.Tiers[tier], out var n) ? n : 0;
            var room = Math.Max(0, size - held.GetValueOrDefault(tier));
            // With includeHeld the held items returned count toward what was asked for.
            var ask = includeHeld ? count - held.GetValueOrDefault(tier) : count;
            for (var k = 0; k < Math.Min(ask, room); k++)
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
        string itemId, uint killSeq, string robotTemplate, IReadOnlyList<string> nearby, string requestId, bool australiumPile = false)
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
        // plan_sdk_2_8 §1: a 2.8 game drops the kill's Australium as a pile (KillAustralium); only a 2.7 game is credited here.
        if (roll.Australium > 0 && !australiumPile)
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

    public const string ContainerOpenedAction = "container.opened";

    /// <summary>
    /// plan_sdk_2_7 §3c: one world container opened by one character. The rules' roll is replayed
    /// from the instance seed (a server cannot choose what it gives) and minted to this floor for the
    /// opener alone. Once per (instance, container, character): the open is recorded even when the roll
    /// is empty, so a later sheet cannot roll the same container again.
    /// </summary>
    public async Task<IReadOnlyList<ItemRecord>> OpenContainer(IWriteTx tx, InstanceRecord caller, string characterId, byte[] token,
        uint containerSeq, string requestId)
    {
        var (ch, _) = await Leased(tx, caller, characterId, token);
        var r = RulesFor(caller);
        var target = $"{caller.Id}:{containerSeq}:{characterId}";
        if (await tx.Audit.Count(new AuditQuery(Action: ContainerOpenedAction, Target: target)) > 0)
            throw HostRefusal.Precondition("already_opened", $"container {containerSeq} of {caller.Id} was opened by {characterId}");
        var minted = new List<ItemRecord>();
        foreach (var drop in r.ContainerDrops(caller.Seed, containerSeq, caller.Depth, Sheet(ch)))
            minted.Add(await tx.Items.Mint(ToNew(drop, ch.Level, characterId), OwnerKind.World, caller.Id, caller.Id, -1, "container", requestId));
        await tx.Audit.Write(ContainerOpenedAction, target, null, new { items = minted.Select(i => i.Id).ToArray() });
        return minted;
    }

    public const string BreakableOpenedAction = "breakable.opened";

    /// <summary>
    /// plan_sdk_2_8 §3: one breakable broken by one hero. The rules' roll is replayed from the instance
    /// seed; its Australium is credited to the hero (their own roll, nothing to share) and its items go
    /// to this floor for them alone. Once per (instance, breakable, character), recorded even when the
    /// roll is empty. Every hero in the room breaks it with their own lease (the game's R4).
    /// </summary>
    public async Task<(long Australium, IReadOnlyList<ItemRecord> Items)> Break(IWriteTx tx, InstanceRecord caller, string characterId, byte[] token,
        uint breakableSeq, string requestId)
    {
        var (ch, _) = await Leased(tx, caller, characterId, token);
        var r = RulesFor(caller);
        var target = $"{caller.Id}:{breakableSeq}:{characterId}";
        if (await tx.Audit.Count(new AuditQuery(Action: BreakableOpenedAction, Target: target)) > 0)
            throw HostRefusal.Precondition("already_broken", $"breakable {breakableSeq} of {caller.Id} was broken by {characterId}");
        var roll = r.BreakableDrops(caller.Seed, breakableSeq, caller.Depth, Sheet(ch));
        if (roll.Australium < 0) throw HostRefusal.Precondition("rules_bad_breakable", $"breakable {breakableSeq} rolls {roll.Australium} Australium");
        if (roll.Australium > 0)
            await tx.Characters.AddAustralium(characterId, roll.Australium, "breakable", $"breakable:{caller.Id}:{breakableSeq}");
        var minted = new List<ItemRecord>();
        foreach (var drop in roll.Items)
            minted.Add(await tx.Items.Mint(ToNew(drop, ch.Level, characterId), OwnerKind.World, caller.Id, caller.Id, -1, "breakable", requestId));
        await tx.Audit.Write(BreakableOpenedAction, target, null, new { australium = roll.Australium, items = minted.Select(i => i.Id).ToArray() });
        return (roll.Australium, minted);
    }

    // ------------------------------------------------------------------ the Australium pile (plan_sdk_2_8 §1)

    /// <summary>A kill's Australium on the floor: a host item the rules never decode (the <c>host:</c> prefix is reserved).</summary>
    public const string AustraliumPile = "host:australium";
    public const string PileMintedAction = "australium.minted";
    public const string PilePickupAction = "australium.pickup";

    sealed record PileData(string Killer, uint KillSeq, string Robot);

    /// <summary>
    /// One pile per kill: the host replays the kill's roll and mints its Australium to this floor, for
    /// the killer's party to pick up. Called for every kill with Australium, item or not. Once per
    /// (instance, kill), already_minted after; a roll of none is no_australium.
    /// </summary>
    public async Task<ItemRecord> KillAustralium(IWriteTx tx, InstanceRecord caller, string killerId, byte[] token,
        uint killSeq, string robotTemplate, int partySize, string requestId)
    {
        await Leased(tx, caller, killerId, token);
        var r = RulesFor(caller);
        var target = $"{caller.Id}:{killSeq}";
        if (await tx.Audit.Count(new AuditQuery(Action: PileMintedAction, Target: target)) > 0)
            throw HostRefusal.Precondition("already_minted", $"kill {killSeq} of {caller.Id} already has its pile");
        var roll = r.KillRoll(caller.Seed, killSeq, robotTemplate, caller.Depth, Math.Max(1, partySize));
        if (roll.Australium <= 0) throw HostRefusal.Precondition("no_australium", $"kill {killSeq} of {robotTemplate} rolls no Australium");
        var data = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new PileData(killerId, killSeq, robotTemplate));
        var pile = await tx.Items.Mint(new NewItem(0, AustraliumPile, 0, 0, (int)Math.Min(int.MaxValue, roll.Australium), true, data, 0),
            OwnerKind.World, caller.Id, caller.Id, -1, "australium", requestId);
        await tx.Audit.Write(PileMintedAction, target, null, new { pile = pile.Id, killer = killerId, australium = roll.Australium });
        return pile;
    }

    /// <summary>
    /// A pile picked up: the killer's party members on this level (killer first when here) are each
    /// credited their share, as the rules divide it, in this one write; the pile ends. Only the killer
    /// or one of their party may pick it up.
    /// </summary>
    public async Task<IReadOnlyList<(string CharacterId, long Amount)>> ClaimAustralium(IWriteTx tx, InstanceRecord caller, string characterId,
        byte[] token, string pileId, string requestId)
    {
        await Leased(tx, caller, characterId, token);
        var r = RulesFor(caller);
        var pile = await tx.Items.Get(pileId) ?? throw HostRefusal.NotFound("no_item", pileId);
        if (pile.BaseType != AustraliumPile) throw new HostRefusal(RefusalCode.InvalidArgument, "not_a_pile", $"{pileId} is not an Australium pile");
        if (pile.State != ItemState.Live || pile.OwnerKind != OwnerKind.World || pile.InstanceId != caller.Id)
            throw HostRefusal.Precondition("wrong_owner", $"{pileId} is not on {caller.Id}'s floor");
        var data = System.Text.Json.JsonSerializer.Deserialize<PileData>(pile.Instance)!;
        var party = await tx.Parties.ForCharacter(data.Killer);
        var members = party is null ? [data.Killer] : party.Members.Prepend(data.Killer).Distinct().ToList();
        if (!members.Contains(characterId))
            throw HostRefusal.Denied("not_in_party", $"{characterId} is not in the party of {data.Killer}, who made {pileId}");
        var here = new List<CharacterRecord>();
        foreach (var id in members)
        {
            if ((await tx.Leases.Get(id))?.InstanceId != caller.Id) continue; // only members on this level share
            if (await tx.Characters.Get(id) is { Deleted: false } c) here.Add(c);
        }
        var shares = r.AustraliumShares(pile.Count, here.Select(Sheet).ToList());
        if (shares.Count != here.Count || shares.Any(s => s < 0 || s > pile.Count))
            throw HostRefusal.Precondition("rules_bad_shares", $"{here.Count} members, each 0..{pile.Count}; the rules gave [{string.Join(",", shares)}]");
        var credits = new List<(string, long)>();
        for (var i = 0; i < here.Count; i++)
        {
            var reference = $"pile:{pileId}";
            if (shares[i] == 0 || await tx.Characters.HasAustraliumRef(here[i].Id, reference)) continue;
            await tx.Characters.AddAustralium(here[i].Id, shares[i], "kill", reference);
            credits.Add((here[i].Id, shares[i]));
        }
        await tx.Items.Terminate(pileId, OwnerKind.World, ItemState.Consumed, "australium_pickup", requestId);
        await tx.Audit.Write(PilePickupAction, pileId, null, new { by = characterId, total = pile.Count, credits = credits.Select(c => new { character = c.Item1, amount = c.Item2 }) });
        return credits;
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
        if (item.BaseType == AustraliumPile)
            throw HostRefusal.Precondition("australium_pile", $"{itemId} is an Australium pile: ClaimAustralium");
        var slot = await FreeSlot(tx, r, ch);
        CheckPlace(r, ch, Decode(r, item), ItemContainer.Backpack, slot);
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

    /// <summary>A backpack slot change (the 2.6 move).</summary>
    public Task<ItemRecord> Move(IWriteTx tx, InstanceRecord caller, string characterId, byte[] token, string itemId, int slot, string requestId) =>
        Move(tx, caller, characterId, token, itemId, ItemContainer.Backpack, slot, requestId);

    /// <summary>
    /// Backpack / equip / belt (plan_sdk_2_7 §1): a slot change inside the character, refused onto an
    /// occupied slot, outside the container, or where the rules refuse the item.
    /// </summary>
    public async Task<ItemRecord> Move(IWriteTx tx, InstanceRecord caller, string characterId, byte[] token, string itemId,
        ItemContainer container, int slot, string requestId)
    {
        var (ch, _) = await Leased(tx, caller, characterId, token);
        var item = await Owned(tx, itemId, characterId);
        var r = RulesFor(caller);
        if (!Enum.IsDefined(container))
            throw new HostRefusal(RefusalCode.InvalidArgument, "bad_container", $"container {(int)container}");
        if (slot < 0 || slot >= r.ContainerSlots(Sheet(ch), container))
            throw new HostRefusal(RefusalCode.InvalidArgument, "bad_slot", $"{container} slot {slot}");
        if ((await Carried(tx, characterId)).Any(i => i.Container == container && i.Slot == slot && i.Id != itemId))
            throw HostRefusal.Precondition("slot_taken", $"{container} slot {slot} of {characterId}");
        CheckPlace(r, ch, Decode(r, item), container, slot);
        return await tx.Items.Move(new ItemMove(itemId, OwnerKind.Character, OwnerKind.Character, characterId, "move",
            Slot: slot, RequestId: requestId, Container: container));
    }

    /// <summary>
    /// plan_sdk_2_8 §4: two carried items exchange their (container, slot) in one transaction, each
    /// destination asked of the rules; one refusal refuses both. How an equip goes through a full
    /// backpack: the displaced item takes the cell the new one left.
    /// </summary>
    public async Task<IReadOnlyList<ItemRecord>> Swap(IWriteTx tx, InstanceRecord caller, string characterId, byte[] token,
        string itemA, string itemB, string requestId)
    {
        var (ch, _) = await Leased(tx, caller, characterId, token);
        if (itemA == itemB) throw new HostRefusal(RefusalCode.InvalidArgument, "bad_items", "a swap names two different items");
        var a = await Owned(tx, itemA, characterId);
        var b = await Owned(tx, itemB, characterId);
        var r = RulesFor(caller);
        CheckPlace(r, ch, Decode(r, a), b.Container, b.Slot);
        CheckPlace(r, ch, Decode(r, b), a.Container, a.Slot);
        var movedA = await tx.Items.Move(new ItemMove(a.Id, OwnerKind.Character, OwnerKind.Character, characterId, "swap",
            Slot: b.Slot, RequestId: requestId, Container: b.Container));
        var movedB = await tx.Items.Move(new ItemMove(b.Id, OwnerKind.Character, OwnerKind.Character, characterId, "swap",
            Slot: a.Slot, RequestId: requestId, Container: a.Container));
        return [movedA, movedB];
    }

    /// <summary>
    /// plan_sdk_2_8 §4: two carried stacks become one, as the rules' <c>Merge</c> says. The counts
    /// must be conserved (a merge never makes or loses any); <c>into</c> and what is left of
    /// <c>from</c> are rewritten in place, or <c>from</c> is ended when it is used up. Returns [into, from].
    /// </summary>
    public async Task<IReadOnlyList<ItemRecord>> Merge(IWriteTx tx, InstanceRecord caller, string characterId, byte[] token,
        string fromId, string intoId, string requestId)
    {
        await Leased(tx, caller, characterId, token);
        if (fromId == intoId) throw new HostRefusal(RefusalCode.InvalidArgument, "bad_items", "a merge names two different items");
        var source = await Owned(tx, fromId, characterId);
        var target = await Owned(tx, intoId, characterId);
        var r = RulesFor(caller);
        var change = r.Merge(Decode(r, source), Decode(r, target));
        if (!change.Ok) throw HostRefusal.Precondition(change.Refusal!, $"merge of {fromId} into {intoId} refused by the rules");
        var whole = change.Produced.Count == 1 && change.Consumed is [0];
        var partial = change.Produced.Count == 2 && change.Consumed.Count == 0;
        var counts = change.Produced.Sum(p => (long)p.Count);
        if (change.Cost != 0 || !(whole || partial) || change.Produced.Any(p => p.Count < 1) || counts != (long)source.Count + target.Count)
            throw HostRefusal.Precondition("rules_bad_merge", $"a merge keeps {source.Count + target.Count} in one or two stacks, costing nothing");
        var merged = change.Produced[0];
        var intoAfter = await tx.Items.SetCount(target.Id, target.Version, merged.Count, merged.Instance, merged.SchemaVersion, "merge", requestId);
        var fromAfter = whole
            ? await tx.Items.Terminate(source.Id, OwnerKind.Character, ItemState.Consumed, "merge", requestId)
            : await tx.Items.SetCount(source.Id, source.Version, change.Produced[1].Count, change.Produced[1].Instance, change.Produced[1].SchemaVersion, "merge", requestId);
        return [intoAfter, fromAfter];
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

    /// <summary>Crates, crafts and salvage consume inputs and mint outputs; identify changes the item in place (repair is <see cref="Repair"/>).</summary>
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
            "salvage" => r.Salvage(rolled[0], ctx, NewSeed()),
            _ => throw new HostRefusal(RefusalCode.InvalidArgument, "bad_operation", operation),
        };
        if (!change.Ok) throw HostRefusal.Precondition(change.Refusal!, $"{operation} refused by the rules");
        if (change.Cost > 0) await tx.Characters.AddAustralium(characterId, -change.Cost, operation, inputs[0].Id);

        // identify: the item itself changes, it is not a mint (§5.2).
        if (operation is "identify")
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
            CheckPlace(r, ch, p, ItemContainer.Backpack, slot);
            result.Add(await tx.Items.Mint(ToNew(p, ch.Level), OwnerKind.Character, characterId, null, slot, operation, requestId));
        }
        return result;
    }

    /// <summary>
    /// plan_sdk_2_7 §2: a town repair (hub only) or a field repair with a kit (anywhere). The
    /// repaired item changes in place (same id, same slot); a field repair's kit is consumed in
    /// the same transaction. The rules may consume only the kit.
    /// </summary>
    public async Task<ItemRecord> Repair(IWriteTx tx, InstanceRecord caller, string characterId, byte[] token,
        string itemId, RepairKind kind, string? kitId, string requestId)
    {
        var (ch, _) = await Leased(tx, caller, characterId, token);
        if (kind == RepairKind.Town) RequireHub(caller);
        var r = RulesFor(caller);
        var item = await Owned(tx, itemId, characterId);
        ItemRecord? kit = null;
        if (kind == RepairKind.Field)
        {
            if (string.IsNullOrEmpty(kitId) || kitId == itemId)
                throw new HostRefusal(RefusalCode.InvalidArgument, "bad_kit", "a field repair names a kit other than the item");
            kit = await Owned(tx, kitId, characterId);
        }
        else if (!string.IsNullOrEmpty(kitId))
            throw new HostRefusal(RefusalCode.InvalidArgument, "bad_kit", "a town repair takes no kit");

        var change = r.Repair(Decode(r, item), kind, kit is null ? null : Decode(r, kit), Sheet(ch), NewSeed());
        if (!change.Ok) throw HostRefusal.Precondition(change.Refusal!, "repair refused by the rules");
        // Index 0 is the item itself, changed in place whatever the rules say (1.0's Repair returns
        // Consumed = [0]); index 1, the kit, is the only input a repair may terminate.
        if (change.Produced.Count != 1 || change.Consumed.Any(i => i is not (0 or 1)) || (kit is null && change.Consumed.Contains(1)))
            throw HostRefusal.Precondition("rules_bad_repair", "a repair produces the item and may consume only the kit");
        if (change.Cost > 0) await tx.Characters.AddAustralium(characterId, -change.Cost, "repair", item.Id);
        if (kit is not null && change.Consumed.Contains(1))
            await tx.Items.Terminate(kit.Id, OwnerKind.Character, ItemState.Consumed, "repair_kit", requestId);
        var p = change.Produced[0];
        return await tx.Items.Mutate(item.Id, item.Version, p.Instance, p.SchemaVersion, p.Identified, false,
            kind == RepairKind.Town ? "repair" : "field_repair", requestId);
    }

    public const int MaxWearEntries = 64;

    /// <summary>
    /// plan_sdk_2_7 §2: batched wear, one rules call and one in-place write per item, all in one
    /// transaction. Any refusal refuses the batch.
    /// </summary>
    public async Task<IReadOnlyList<ItemRecord>> Wear(IWriteTx tx, InstanceRecord caller, string characterId, byte[] token,
        IReadOnlyList<(string ItemId, int Amount)> entries, string requestId)
    {
        await Leased(tx, caller, characterId, token);
        if (entries.Count == 0 || entries.Count > MaxWearEntries)
            throw new HostRefusal(RefusalCode.InvalidArgument, "bad_entries", $"1..{MaxWearEntries} entries, got {entries.Count}");
        if (entries.Select(e => e.ItemId).Distinct().Count() != entries.Count)
            throw new HostRefusal(RefusalCode.InvalidArgument, "bad_entries", "an item appears twice");
        var r = RulesFor(caller);
        var worn = new List<ItemRecord>();
        foreach (var (itemId, amount) in entries)
        {
            if (amount <= 0) throw new HostRefusal(RefusalCode.InvalidArgument, "bad_amount", $"{itemId}: {amount}");
            var item = await Owned(tx, itemId, characterId);
            var change = r.Wear(Decode(r, item), amount);
            if (!change.Ok) throw HostRefusal.Precondition(change.Refusal!, $"wear on {itemId} refused by the rules");
            if (change.Produced.Count != 1 || change.Consumed.Count > 0 || change.Cost != 0)
                throw HostRefusal.Precondition("rules_bad_wear", "wear produces the item in place, consumes nothing, costs nothing");
            var p = change.Produced[0];
            worn.Add(await tx.Items.Mutate(item.Id, item.Version, p.Instance, p.SchemaVersion, p.Identified, false, "wear", requestId));
        }
        return worn;
    }

    // ------------------------------------------------------------------ consume (plan_sdk_2_7 §1)

    public const string ConsumeAction = "item.consumed";

    /// <summary>
    /// Uses up <paramref name="count"/> of a carried item, on any instance kind: the item is terminated
    /// Consumed, or a stack's count is lowered in place. Returns the item as it is after (its state says
    /// which). The effect (a heal, a skill) is the game's.
    /// </summary>
    public async Task<ItemRecord> Consume(IWriteTx tx, InstanceRecord caller, string characterId, byte[] token,
        string itemId, int count, string requestId)
    {
        var (ch, _) = await Leased(tx, caller, characterId, token);
        if (count < 1) throw new HostRefusal(RefusalCode.InvalidArgument, "bad_count", $"count {count}");
        var item = await Owned(tx, itemId, characterId);
        var r = RulesFor(caller);
        var change = r.Consume(Sheet(ch), Decode(r, item), count);
        if (!change.Ok) throw HostRefusal.Precondition(change.Refusal!, $"consume of {itemId} refused by the rules");
        var whole = change.Consumed is [0] && change.Produced.Count == 0;
        var partial = change.Consumed.Count == 0 && change.Produced is [{ Count: var left }] && left >= 1 && left == item.Count - count;
        if (change.Cost != 0 || !(whole || partial))
            throw HostRefusal.Precondition("rules_bad_consume", "consume ends the item or lowers its count by exactly the amount, costing nothing");
        await tx.Audit.Write(ConsumeAction, itemId, null, new { character = characterId, count, instance = caller.Id });
        if (whole) return await tx.Items.Terminate(itemId, OwnerKind.Character, ItemState.Consumed, "consume", requestId);
        var p = change.Produced[0];
        return await tx.Items.SetCount(itemId, item.Version, p.Count, p.Instance, p.SchemaVersion, "consume", requestId);
    }

    // ------------------------------------------------------------------ death, corpse, Lost & Found (D6, D7)

    /// <summary>
    /// Everything carried becomes Corpse-owned on this instance; a softcore character is then minted
    /// the class's starting kit, and the result is the corpse's items, not the kit. The Australium
    /// stays with the character (ruling 2026-11-22). A corpse written before that ruling still holds what it took
    /// (ref <c>corpse:{instance}</c>), and looting, the reap and a level fork still return or move it.
    /// </summary>
    public const string DeathAction = "character.death";

    public async Task<IReadOnlyList<ItemRecord>> RecordDeath(IWriteTx tx, InstanceRecord caller, string characterId, byte[] token, string requestId)
    {
        var (ch, _) = await Leased(tx, caller, characterId, token);
        var carried = await Carried(tx, characterId);
        var moved = new List<ItemRecord>();
        foreach (var i in carried)
            moved.Add(await tx.Items.Move(new ItemMove(i.Id, OwnerKind.Character, OwnerKind.Corpse, characterId, "death",
                Slot: i.Slot, InstanceId: caller.Id, RequestId: requestId, Container: i.Container)));
        var kit = 0;
        if (ch.Hardcore)
            await tx.Characters.Update(characterId, ch.Version, c => c with { Fallen = true });
        else // ruling 2026-11-22: a respawn always gets the class's starting kit, on the ledger
            kit = await MintStartingKit(tx, RulesFor(caller), ch, ch.ClassName, Sheet(ch), "respawn_kit", requestId);
        // D-H16: where they died, so ReturnToCorpse goes back to that level (depth + level hash).
        await tx.Audit.Write(DeathAction, characterId, null, new { instance = caller.Id, depth = caller.Depth, level = caller.LevelCopy, items = moved.Count, kit });
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

        (lf, destroyed) = await CorpsesToLostAndFound(tx, instance.Id, r);

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

    /// <summary>
    /// The corpses lying at <paramref name="place"/> (an instance id, or a level copy's pending
    /// place, D-H18) go to their owners' Lost &amp; Found with the Australium they held, at 25 % of
    /// vendor value when the rules are known; a hardcore character's are destroyed.
    /// </summary>
    public static async Task<(int ToLostAndFound, int Destroyed)> CorpsesToLostAndFound(IWriteTx tx, string place, IGameRules? r)
    {
        var lf = 0; var destroyed = 0;
        var corpses = await tx.Items.Query(new ItemQuery(OwnerKind: OwnerKind.Corpse, InstanceId: place, Take: 100_000));
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
                await tx.LostAndFound.Add(ch.Id, i.Id, fee, place);
                lf++;
            }
            if (ch is { Hardcore: false }) await ReturnCorpseAustralium(tx, ch.Id, place, "lost_and_found");
        }
        return (lf, destroyed);
    }

    /// <summary>
    /// D-H18: a character's corpse moves from <paramref name="from"/> to <paramref name="to"/> (instance
    /// ids, or a level copy's pending place) with the Australium it holds; one owner throughout.
    /// </summary>
    public static async Task<int> MoveCorpse(IWriteTx tx, string characterId, string from, string to, string reason)
    {
        var items = await tx.Items.Query(new ItemQuery(OwnerKind: OwnerKind.Corpse, OwnerId: characterId, InstanceId: from, Take: 10_000));
        foreach (var i in items)
            await tx.Items.Move(new ItemMove(i.Id, OwnerKind.Corpse, OwnerKind.Corpse, characterId, reason, InstanceId: to, Slot: i.Slot, Container: i.Container));
        var held = -(await tx.Characters.AustraliumLedger(characterId)).Where(e => e.Ref == $"corpse:{from}").Sum(e => e.Delta);
        if (held > 0)
        {
            await tx.Characters.AddAustralium(characterId, held, reason, $"corpse:{from}");
            await tx.Characters.AddAustralium(characterId, -held, reason, $"corpse:{to}");
        }
        return items.Count;
    }

    // ------------------------------------------------------------------ trades (2d, two-phase)

    public static async Task<int> ReturnEscrow(IWriteTx tx, TradeRecord t)
    {
        var n = 0;
        foreach (var i in await tx.Items.Query(new ItemQuery(OwnerKind: OwnerKind.TradeEscrow, OwnerId: t.Id, Take: 1000)))
        {
            var back = t.OfferA.Contains(i.Id) ? t.A : t.B;
            await tx.Items.Move(new ItemMove(i.Id, OwnerKind.TradeEscrow, OwnerKind.Character, back, "trade_return", Slot: i.Slot, Container: i.Container));
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
            await tx.Items.Move(new ItemMove(id, OwnerKind.Character, OwnerKind.TradeEscrow, t.Id, "trade_lock", Slot: i.Slot, RequestId: requestId, Container: i.Container));
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
