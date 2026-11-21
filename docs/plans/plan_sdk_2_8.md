# plan_sdk_2_8.md — SDK 2.8.0 / Contracts 1.2.0: Australium on the floor, breakables, swap and merge

Written 2026-11-20, approved the same day; **built 2026-11-20** on branch `sdk-2.8` (§7). The second drop of the game's SDK gaps, from
the 2.7 adoption (game main 6c1e2ac40, source-sdk-sharp-cf): the Australium share ruled for
2.7 §3b, plus the three things the adoption found. Requested by the game session, relaying
the user's ruling of 2026-11-20 ("pass whatever you need done to the host session").

Versions: `Host.Sdk` 2.7.0 → **2.8.0**; `Host.Contracts` 1.1.0 → **1.2.0**. As in 2.7, every
new `IGameRules` member has a default body (a 1.0 or 1.1 module still loads), and every proto
change is additive: a 2.7 SDK keeps today's behavior against a 2.8 service.

## 0. What exists today (facts, public bae94ac)

| area | today |
|---|---|
| kill Australium | credited inside `Reveal`, to the killer and every `nearby_members` id, the **whole** roll each, ref `kill:{instance}:{seq}` (`DescentLedger.cs:161`). `Reveal` runs only when an item drops, so a kill that rolls Australium and no item credits nothing hosted. `MintDrops` (the empty-tier fallback, bosses) credits nothing |
| repair | `Repair(item, kind, kit, seed)` (1.1). No sheet, so the rules price a town repair and size a field repair without the hero |
| town price | `Cost` of the rules' `ItemChange`; the host charges it. There is no waiver call |
| world containers | `OpenContainer(lease, seq)` replays `ContainerDrops`, once per (instance, seq, character), marker `container.opened` in the audit |
| moves | `Move(lease, item, container, slot)` refuses an occupied slot (`slot_taken`); no swap, no merge |
| item decode | only the ledger decodes item bytes (`Decode(r, item)`); the admin UI and the sweep never do |

## 1. The Australium pile (2.7 §3b, ruled 2026-11-16)

**One pile per kill, shared on pickup.** A new call mints it, independent of the item drop:

```
rpc KillAustralium(KillAustraliumRequest) returns (Item);   // request_id, character_id (killer), lease_token,
                                                             // kill_seq, robot_template, party_size, position
```

- The host replays `KillRoll(instanceSeed, killSeq, robot, depth, partySize)`; if `Australium > 0`
  it mints one pile: `World`-owned on this instance, base type **`host:australium`** (reserved; the
  `host:` prefix cannot clash with a rules base type), `Count` = the roll, `ForCharacter` empty,
  and an `Instance` the host writes itself (`{killer, killSeq}`, schema 0). The rules never decode
  a pile. Once per (instance, kill): a second call is `already_minted`; a roll of 0 is `no_australium`.
- **Pickup** is a new call, so a 2.7 SDK can never put a pile in a backpack (its `Claim` of a pile
  is refused `australium_pile`):

```
rpc ClaimAustralium(ItemActionRequest) returns (AustraliumPickup);  // item_id = the pile
message AustraliumPickup { repeated AustraliumCredit credits = 1; }
message AustraliumCredit { string character_id = 1; int64 amount = 2; }
```

  The claimer must be the killer or in the killer's party. The members are the killer's party
  members leased on this instance at pickup, killer first if present, then the party's order.
  The host asks the rules for the shares, checks them, credits each member with ref
  `pile:{pileId}` (so a retry cannot pay twice) and terminates the pile `Consumed`
  (`australium_pickup`). An unclaimed pile is swept at level end like any floor item.

```csharp
// Contracts 1.2. The split (total / members, remainder one apiece in order) plus each member's
// found share (Scavenger, new Australium beyond the split). One entry per member, in order.
// Default: every member gets the whole roll (today's host behavior).
IReadOnlyList<long> AustraliumShares(long total, IReadOnlyList<CharacterSheet> killerFirst) =>
    killerFirst.Select(_ => total).ToList();
```

  Checks: one share per member, each `0 ≤ share ≤ total` (a found share adds to the split but
  never beyond a whole roll), otherwise `rules_bad_shares`. Audited `australium.pickup` with
  every credit.
- **`Reveal` stops crediting for a 2.8 SDK.** `RevealRequest` gains `bool australium_pile = 9`;
  the 2.8 SDK sets it, and then `Reveal` credits nothing (`australium_booked` = 0). Unset (a 2.7
  game), `Reveal` credits as today. No other gate: the flag says which game it is.
- SDK: `IItems.KillAustralium(killer, killSeq, robot, partySize, position)` → `HostItem` (the
  pile); `IItems.ClaimAustralium(lease, pileId)` → `IReadOnlyList<HostAustraliumCredit>`;
  `HostItem.IsAustraliumPile` (base type `host:australium`).

## 2. Repair carries the sheet (Contracts 1.2)

```csharp
// The repairer's sheet (level, stats, perks such as a waived town price). Default: the 1.1 member.
ItemChange Repair(RolledItem item, RepairKind kind, RolledItem? kit, CharacterSheet sheet, ulong seed) =>
    Repair(item, kind, kit, seed);
```

The ledger calls this one. No proto or SDK change.

**The town waiver is this member, not a host call.** A price the game waives (`town_free`) is a
rule about the hero: the rules read it from the sheet and return `Cost = 0`. A host call that lets
a server waive a price would let any server repair free; this keeps the price the rules'.

## 3. Numbered breakables

Shaped like `ContainerDrops`, in their own numbering (a breakable 3 and a container 3 are two things):

```csharp
// One breakable broken by one character: Australium for them and items to the floor for them.
// The bad outcomes (blast, cloud, ambush) are the game's and mint nothing: return the empty roll.
BreakableRoll BreakableDrops(ulong instanceSeed, uint breakableSeq, int depth, CharacterSheet opener) =>
    new(0, []);

public sealed record BreakableRoll(long Australium, IReadOnlyList<RolledItem> Items);
```

```
rpc Break(BreakRequest) returns (BreakResponse);   // request_id, character_id, lease_token, uint32 breakable_seq
message BreakResponse { int64 australium = 1; repeated Item items = 2; }
```

- Once per (instance, breakable, character), marker `breakable.opened` in the audit (written even
  when the roll is empty), else `already_broken`.
- **Australium is credited to the breaker directly** (ref `breakable:{instance}:{seq}`), not
  dropped as a pile: the roll is the breaker's own, so there is nothing to share and nothing for
  another player to pick up. Items go to the floor with `ForCharacter` = the breaker, as containers do.
- `breakableSeq` is the game's: the breakables manager's slot index in the level's list (stable for
  a level copy, -cf). As with kills and containers, the host does not bound it by the layout.
- **Every hero in the room gets their own roll (the game's R4).** The game calls `Break` once per
  eligible hero, each with that hero's lease, breaker first; each call is its own once-only
  (instance, seq, character), so several characters breaking one seq is expected, and each one's
  items land with `ForCharacter` = that character. The game keys the outcome kind on
  (instanceSeed, seq, depth) so every hero agrees on it, and the share on the opener. Containers
  work the same way today. A fact: two heroes break one seq, each is paid their own roll, and each
  one's second call is `already_broken`.
- SDK: `IItems.Break(lease, breakableSeq)` → `HostBreak(long Australium, IReadOnlyList<HostItem> Items)`.

## 4. Swap and merge

**Swap** (equip ↔ backpack, belt ↔ backpack, any two carried items): one call, one transaction.

```
rpc Swap(SwapRequest) returns (ItemsResponse);   // request_id, character_id, lease_token, item_a, item_b
```

Each item takes the other's (container, slot). Both must be the caller's; each destination is
asked `PlaceRefusal`, and a refusal refuses both moves. This is how an equip goes through a full
backpack: the displaced item takes the cell the new one left. SDK `IItems.Swap(lease, a, b)`.

**Merge** two stacks of the same kind:

```csharp
// from's count into into's, up to the rules' stack limit. Produced[0] = into with its new count;
// Produced[1] = what is left of from, or Consumed = [0] when from is used up. Re-encoded, in place.
// Default: unsupported (a 1.x module's items cannot be re-encoded by the host).
ItemChange Merge(RolledItem from, RolledItem into) => new([], [], 0, "unsupported");
```

```
rpc Merge(MergeRequest) returns (ItemsResponse);   // request_id, character_id, lease_token, from_item_id, into_item_id
```

The host checks the counts are conserved (`into' + from' == into + from`, every count ≥ 1, cost
0), else `rules_bad_merge`; it writes both with `SetCount`, or terminates `from` `Consumed`
(`merge`). SDK `IItems.Merge(lease, from, into)`.

## 5. Order, tests, done

1. **§2** Repair with the sheet (contract member, ledger call).
2. **§4** Swap, then Merge.
3. **§3** Break.
4. **§1** the pile: `KillAustralium`, `ClaimAustralium`, the `Reveal` flag, `AustraliumShares`.
5. Versions 2.8.0 / 1.2.0, `docs/sdk-integration.md` 2.8.0 notes, zero warnings, all facts,
   `make pack` + pack-check, the numbers to the game session; its adoption lane runs against
   this worktree's local service (`HOST_ROOT=… make local-hosted`), as with 2.7.

Every surface lands with ledger facts, `Host.Sdk.Tests` facts, `ProtoContractFacts` (each new
write carries `request_id` and `lease_token`), `FakeGameRules` implementing each new member, and a
1.1-only rules module (as `Contract10RulesFacts`) proving each default. Pile facts: a retry pays
once; a non-party claimer is refused; a member who left the level is not paid; a 2.7 `Reveal`
still credits; a 2.8 `Reveal` does not; the sweep takes an unclaimed pile.

## 6. For the game (source-sdk-sharp-cf)

- Call `KillAustralium` for **every** kill whose roll has Australium, item or not (today's hosted
  path pays nothing for a kill without an item), and set nothing else: the 2.8 SDK sets
  `australium_pile` on `Reveal` itself.
- Render `host:australium` floor items as the pile; pick up with `ClaimAustralium`, show each
  credit.
- Waive the town price in `Repair(item, Town, null, sheet, seed)` by returning `Cost = 0`.
- Number breakables by the manager's slot index; return the empty `BreakableRoll` for a bad outcome.

## 7. As built (2026-11-20), where it differs from the above

- **`Merge`'s parameters are `(source, target)`**, not `(from, into)`: both are C# query keywords
  and break expressions such as `from.Count`. The RPC fields stay `from_item_id` / `into_item_id`.
- **`Host.FakeGame` calls `KillAustralium` and `ClaimAustralium`** on every kill with Australium:
  the 2.8 SDK's `Reveal` credits nothing, and the reference game must still pay its kills. It has
  no floor, so its killer picks the pile up at once.
- **Shares are paid to the party at pickup**, by membership then: a member who left the level, or
  joined the party after the kill, is judged at the pickup. The pile records only its killer.
- The fake rules: `AustraliumShares` is the split alone (no Scavenger); a town repair is free from
  level 50 (`TownFreeLevel`, the stand-in for `town_free`); a field repair adds the hero's level;
  breakable kinds are keyed on (seed, seq, depth), the share on the opener; stacks merge up to 5.

