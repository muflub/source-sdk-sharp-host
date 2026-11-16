# plan_sdk_2_7.md — SDK 2.7.0 / Contracts 1.1.0: use, wear and keep gear hosted

Written 2026-11-15, approved the same day. **Built 2026-11-16** on branch `sdk-2.7` (3a, H2,
H1, 3c); **3b moved to 2.8.0** by the user's ruling (§3b), so 2.7.0 ships without it. The first
drop of the Descent game's SDK gaps (source-sdk-sharp-cf's list, H1–H9): H1, H2 and H3, the
ones without which a hosted hero cannot use or keep their gear. H4–H9 are a later drop.

Versions: `Host.Sdk` 2.6.0 → **2.7.0**; `Host.Contracts` 1.0.0 → **1.1.0**. Every new
`IGameRules` member has a default body, so a rules module built on 1.0 still loads
(`HostContract.Supports`: same major, module minor ≤ host minor). Proto changes are additive
(new RPCs, new optional fields), so a 2.6 SDK keeps working against a 2.7 service.

## 0. What exists today (facts, 9a70c1c)

| area | today |
|---|---|
| slots | equip and belt are backpack slot numbers: `DescentLedger.Move` (`DescentLedger.cs:248`) takes any slot in `0..BackpackSlots(sheet)-1`; the rules never see the move |
| consume | none. An item can only be terminated as `Consumed` inside `Transform` (crate, craft, salvage) |
| durability | none on the host. It would live in the item's opaque `Instance` bytes; the only in-place write is `IItemLedger.Mutate` |
| repair | `Repair(lease, itemId)` → `IGameRules.Repair(item)` → `Mutate` in place, `Cost` charged. No kind, no kit (`ItemActionRequest`, `game.proto:380`) |
| Australium from kills | credited at `Reveal` to the killer and every nearby member, ref `kill:{instance}:{seq}` (`DescentLedger.cs:149`). Pickup (`Claim`) credits nothing |
| floor | `World`-owned, keyed by instance id. `ListWorldItems` lists the caller's floor; the end-of-level sweep audits only a count |
| SDK identity per call | `Rpc.Auth()` reads the current endpoint on **every attempt** (`Rpc.cs:44`); a same-process hop's `Rebind` swaps it (`HostSdk.cs:199`) |

## 1. H1 — equip, belt, consume

**A character's items live in three containers** (the game's `HeroInventory`, Q5):
`Backpack` (numbered cells, 12 + 4 a rank of Deep Pockets), `Equipped` (named slots:
primary, secondary, melee, hat, body, feet, trinket1, trinket2, amulet) and `Belt` (four
numbered slots, potions only). Items gain `ItemContainer container` (a column defaulting to
`Backpack`, one migration; proto `Item.container`, `MoveRequest.container`; `HostItem.Container`).
An equipped item's `slot` is its index in the rules' slot-name list (`EquipSlots`); the game
maps it to the name with its own copy of the rules, so the SDK carries only the index. New, defaulted contract members:

```csharp
// Host.Contracts 1.1.0. Defaults keep 1.0 behavior: one Backpack range, no other containers.
IReadOnlyList<string> EquipSlots => [];
int ContainerSlots(CharacterSheet sheet, ItemContainer c) => c == ItemContainer.Backpack ? BackpackSlots(sheet) : 0;
string? PlaceRefusal(CharacterSheet sheet, RolledItem item, ItemContainer c, int slot) => null; // null = allowed
```

`Move(lease, itemId, container, slot)` replaces the slot-only move (the 2.6 overload means
`Backpack`). Every placement (move, claim, transform output) asks `PlaceRefusal`; a refusal
is `FailedPrecondition` with the rules' reason (`wrong_slot`, `class_restricted`,
`potions_only`, …). Pickups and mints still land in the first free `Backpack` cell. Death
moves all three containers to the corpse, as it moves the backpack today.

**The starting kit.** Every class starts with potions on its belt (health_small ×2,
mana_small). New, defaulted: `IReadOnlyList<(RolledItem Item, ItemContainer In, int Slot)> StartingKit(string className, CharacterSheet sheet, ulong seed) => [];`
minted at character creation, in the same transaction.

**Consume is new.** `ItemService.Consume(ConsumeRequest)` / `IItems.Consume(lease, itemId, count = 1)`:

```csharp
// Default: a stack loses `count`; at 0 (or for a non-stack) the item is terminated Consumed.
ItemChange Consume(CharacterSheet sheet, RolledItem item, int count);
```

The ledger terminates the item as `Consumed`, or lowers `Count` (a new `IItemLedger.SetCount`
primitive, versioned like `Mutate`), audits `item.consumed {count}`, and returns what is left.
Lease-gated, any instance kind. The effect (heal, a tome's skill) is the game's; a tome that
changes the sheet reaches it through the checkpoint, and H8's evidence can later cite the
consumed item id, which the host can check. Belt use is `Consume` of a belt-slot item.

## 2. H2 — wear and the two repairs

**Wear.** `ItemService.Wear(WearRequest)` / `IItems.Wear(lease, IReadOnlyList<(itemId, amount)>)`,
batched: the game accumulates wear and writes on a timer, at checkpoints and whenever an
item reaches 0. Each entry runs the rules and `Mutate`s the item in one transaction.

```csharp
// Default: refused "unsupported" (a 1.0 module has no durability).
ItemChange Wear(RolledItem item, int amount);
```

**Repair gets a kind.** `ItemActionRequest` gains `RepairKind repair_kind = 6` (`TOWN = 0`, so
an unset field is the 2.6 behavior; `FIELD = 1`) and `string kit_id = 7`; `IItems.Repair(lease, itemId, kind = Town, kitId = null)`
keeps the 2.6 signature working.

```csharp
// Default: Town → Repair(item); Field → refused "unsupported".
ItemChange Repair(RolledItem item, RepairKind kind, RolledItem? kit, ulong seed);
```

- **Town** (the Engineer's stall): lossless, costs Australium. **Hub only** (`RequireHub`).
- **Field** (a Repair Kit): anywhere, town included. The rules restore `level + rand(level)`
  and lower maximum durability by `max(maxDur / (level + 9), 1)` (the game's numbers; the
  host only applies the result). The kit must be the caller's and a kit by the rules' say.

The ledger's new branch does both halves in **one transaction**: `Mutate` the repaired item
(same id, stays in its slot) and terminate the kit `Consumed`. Today's `Transform` does
one or the other, never both. At maximum durability 0 the item stays; "unusable" is game data.

## 3. H3 — floor, pickup, a drop on the wrong level

**3a. The wrong-level drop (SDK fix, no contract change).** A call's instance identity is
captured once, when the call starts, and every retry reuses it. Today a `Reveal` or
`MintDrops` still retrying when a same-process hop rebinds goes out as the *new* instance,
gets a fresh idempotency key, and lands on the new level's floor. After the fix it lands on
the floor of the level the kill happened on, or is refused if that instance is gone. Fact:
`SameProcessHopFacts`, a `Reveal` held in flight across a `Rebind` lands on the old floor
(and, first, the same fact without the fix shows it on the new one).
Nobody saw it live (Q1): the game found the same symptom by reading its own hosted ledger,
which places a kill's item on whatever level the process holds when the answer arrives.
The game fixes its side (drop a placement whose level has gone); this fix is the SDK's half.

**3b. Australium on pickup.** Kill Australium becomes a floor item. `Reveal` and `MintDrops`
mint one pile per eligible member (reserved base type `australium`, `Count` = that member's
share, `ForCharacter` = that member) instead of crediting. `Claim` of a pile credits the
claimer (ref = the pile's item id, so it can't be credited twice) and terminates it
`Consumed`. Unclaimed piles are swept at level end like any floor item. The shares are the
rules' (Q2):

```csharp
// The split (total / members, remainder one apiece, killer first) plus each member's
// found share (Scavenger, rounded down, new Australium beyond the split).
// Default: every member gets the whole roll, which is what the host does today.
IReadOnlyList<long> AustraliumShares(long total, IReadOnlyList<CharacterSheet> killerFirst) => …;
```

Today's host credits **every nearby member the whole roll**, not a split, so the game's
rules and the host already disagree; this member fixes that.

**Ruled 2026-11-16: shared on pickup, and moved to SDK 2.8.0.** One pile per kill, not one per
member: anyone in the party may pick it up, and that `Claim` credits every party member on the
level their share at once (`AustraliumShares`), each with ref = the pile's id, and terminates
the pile. The pile shape above (one per member, `ForCharacter`) is replaced by this. 2.7.0
keeps today's whole-roll crediting.

**3c. Potions** (Q3) come from four places:
- **Kill tables** (loot.json, 6 %): already items from `KillRoll`/`Reveal`; nothing new.
- **Containers** (supply crate, locker, sarcophagus; up to 3 a room; 25 % potion; each
  player opens their own, one roll per eligible player, not split): a new
  `ItemService.OpenContainer(lease, containerSeq)` the host replays from the instance seed,
  as it replays kills, so a server can't mint at will. New, defaulted:
  `IReadOnlyList<RolledItem> ContainerDrops(ulong instanceSeed, uint containerSeq, int depth, CharacterSheet opener) => [];`
  Once per (instance, container, character), minted to the opener's floor with `ForCharacter`.
- **The starting kit**: `StartingKit`, §1.
- **Vendors** (the Medic): the existing `Buy`; nothing new.

**3d. What the sweep destroyed: deferred.** `ListWorldItems` already covers the floor now.
The real ask (Q4) is "what did a hero leave behind and lose at level end", read by the
game, which has no code waiting on it. Lowest priority, so it moves to the next drop: the
sweep audit names item ids per character, and a read returns a character's last sweep.

## 4. Answers from the game (source-sdk-sharp-cf, game main 841993449)

| # | question | answer |
|---|---|---|
| Q1 | the KillDrop on the next level | not seen live; the game's hosted ledger places without checking the level is still loaded. The game fixes that; the SDK's Rebind fix is separate and stays |
| Q2 | Australium split | one pile per member at that member's share (split plus Scavenger found share, rules-side at mint). **Split at drop vs on collection is not yet ruled by the user** |
| Q3 | potion sources | kill tables, containers (per player), the starting kit, vendors |
| Q4 | the floor report | what the sweep destroyed, per character, read by the game; lowest priority |
| Q5 | the kit | three containers: Backpack, Equipped (named slots), Belt (4, potions only) |
| Q6 | where repairs run | Town = the Engineer, hub only; Field = anywhere |

## 5. Order, tests, done

1. **3a** (SDK only, independent).
2. **H2**: proto fields, contract members, the ledger's mutate-and-consume branch.
3. **H1**: containers (migration), `PlaceRefusal`, `Consume`, `SetCount`, `StartingKit`.
4. **3c** containers (`OpenContainer`).
5. ~~**3b** once the split ruling is in.~~ Ruled; moved to 2.8.0 so 2.7.0 ships now. **3d** moves to the next drop.

Every surface lands with: `Host.Sdk.Tests` facts (`ItemFacts`), ledger facts
(`LedgerFlowFacts`), `ProtoContractFacts` (new writes carry `request_id` and
`lease_token`), `FakeGameRules` implementing each new member and `Host.FakeGame` exposing
each new call (the SDK-completeness rule), and the version facts in `ManagedOnlySdkFacts`.
Each defaulted member gets a fact that a 1.0-built module still loads and behaves as
before. `docs/sdk-integration.md` gets the 2.7.0 notes. Done = zero warnings, all facts
green, `make pack` + `pack-check`, then the package and its version handed to the game
session.

## 6. As built (2026-11-16), where it differs from the above

- **The starting kit** is a list of `StartingItem(RolledItem Item, ItemContainer Container, int Slot)`,
  a record rather than a tuple. Every kit item must replay at a checkpoint; a kit that breaks its
  own containers refuses the creation (`rules_bad_kit`).
- **`Consume`'s default** consumes a whole item and refuses a partial stack as `unsupported`:
  the host cannot re-encode a 1.0 module's opaque item, and `IItemCodec` has no `Encode`. The
  member returns `ItemChange` like the other item operations (`Consumed = [0]` ends the item,
  `Produced = [the re-encoded stack]` lowers it), and `SetCount` takes the new instance bytes.
- **`Item.state` / `HostItem.State`** (not in §1): a `Consume` that uses the last one returns
  the item with state `CONSUMED`, so the game can tell it is gone.
- **`PlaceRefusal`** is asked on `Move`, `Claim`, transform outputs and the kit. Buys, stash-out,
  trades, corpse loot and reclaims land in the first free backpack cell without asking (the
  backpack refuses nothing in the game's rules today).
- **The once-only container marker** is an audit row (`container.opened`, target
  `{instance}:{seq}:{character}`), written even when the roll is empty. `containerSeq` is not
  bounded by the level's layout: a server is trusted with container numbers as it is with kill
  numbers.
- **`Host.FakeGame`** does not expose the new calls: it never exposed `Repair` and the other
  inventory calls either, so the SDK facts (`ItemFacts`) are the coverage.
- **Items carried by a dead character** keep their container on the corpse (and in trade escrow);
  looting a corpse puts everything back in backpack cells.
