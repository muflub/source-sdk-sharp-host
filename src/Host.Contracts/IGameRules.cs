namespace SourceSharp.Host.Contracts;

/// <summary>
/// The mod's rules, as the host needs them (plan §1.2, D-H9). Implemented by the mod's
/// rules module (TF2.Descent.Rules in the sharp repo), which the host never compiles against:
/// the game server's SDK announces the module at boot and the host loads it at run time,
/// one collectible load context per content hash. Every call is pure: the same inputs give
/// the same outputs in the game and in the host, which is what makes a replay a check.
/// </summary>
public interface IGameRules
{
    /// <summary>Roll one item. Pure in (ctx, seed).</summary>
    RolledItem RollItem(ItemRollContext ctx, ulong seed);

    /// <summary>True when <paramref name="item"/> is what <see cref="RollItem"/> gives for its own context and seed.</summary>
    bool Replays(RolledItem item);

    /// <summary>Whether a kill drops, and which tier (D-H1). Pure; the service replays it on every Reveal.</summary>
    KillRoll KillRoll(ulong instanceSeed, uint killSeq, string robotTemplate, int depth, int partySize);

    /// <summary>A boss's or champion's own table (D13), per player, synchronous.</summary>
    IReadOnlyList<RolledItem> BossDrops(ItemRollContext ctx, string robotTemplate, ulong seed);

    int BackpackSlots(CharacterSheet sheet);
    int StashSlots(IReadOnlyList<CharacterSheet> living);

    /// <summary>A new character's first sheet.</summary>
    CharacterSheet NewCharacter(string className, string name, bool hardcore);

    ValidationResult ValidateCheckpoint(CharacterSheet before, CharacterSheet after, CheckpointEvidence evidence);

    /// <summary>
    /// An operator's edit of a sheet (contract 1.3): the admin character edit asks this, never
    /// <see cref="ValidateCheckpoint"/>, so the rules can tell an edit, which has no play evidence,
    /// from a checkpoint and let it raise XP or level. The default is the 1.2 behavior: a
    /// checkpoint with empty evidence.
    /// </summary>
    ValidationResult ValidateEdit(CharacterSheet before, CharacterSheet after) =>
        ValidateCheckpoint(before, after, new CheckpointEvidence([], 0));

    TradeResult ApplyTrade(TradeState trade);

    /// <summary>The RPG layout of one level (D-H7): rooms, cells, rotations, roles.</summary>
    LevelPlan GenerateLayout(LayoutKey key);

    LevelRow LevelTable(int depth);

    /// <summary>The reserve tiers, index = tier number (e.g. Stock, Vintage, Strange, Unusual, Australium).</summary>
    IReadOnlyList<string> Tiers { get; }

    /// <summary>Vendor stock for one roll (D-H4).</summary>
    IReadOnlyList<RolledItem> VendorStock(string vendor, ulong seed);

    /// <summary>Price of an item in Australium for a buy, a sell or a Lost &amp; Found fee base.</summary>
    long Price(RolledItem item, PriceKind kind);

    ItemChange OpenCrate(RolledItem crate, ItemRollContext ctx, ulong seed);
    ItemChange Craft(string recipe, IReadOnlyList<RolledItem> inputs, ItemRollContext ctx, ulong seed);
    ItemChange Identify(RolledItem item, ulong seed);
    ItemChange Repair(RolledItem item);
    ItemChange Salvage(RolledItem item, ItemRollContext ctx, ulong seed);

    /// <summary>
    /// A repair of one kind (contract 1.1). Inputs are [item] for <see cref="RepairKind.Town"/> and
    /// [item, kit] for <see cref="RepairKind.Field"/>: <c>Produced[0]</c> is the repaired item, which
    /// keeps its id; <c>Consumed</c> may name the kit (index 1), and index 0 is ignored (the item is
    /// changed in place, as 1.0's <see cref="Repair(RolledItem)"/> reports it). The default is the 1.0
    /// behavior: a town repair is <see cref="Repair(RolledItem)"/>, a field repair is refused.
    /// </summary>
    ItemChange Repair(RolledItem item, RepairKind kind, RolledItem? kit, ulong seed) =>
        kind == RepairKind.Town ? Repair(item) : new ItemChange([], [], 0, "unsupported");

    /// <summary>
    /// A repair by a hero (contract 1.2): <paramref name="sheet"/> is the repairer's, so the rules can
    /// size a field repair by the hero's level and stats and price a town repair by their perks (a
    /// waived price is <c>Cost = 0</c>). The ledger calls this member; the default is the 1.1 one.
    /// </summary>
    ItemChange Repair(RolledItem item, RepairKind kind, RolledItem? kit, CharacterSheet sheet, ulong seed) =>
        Repair(item, kind, kit, seed);

    /// <summary>
    /// Wear on one item (contract 1.1): <c>Produced[0]</c> is the item with its durability lowered
    /// by <paramref name="amount"/>, in place. The default refuses: a 1.0 module has no durability.
    /// </summary>
    ItemChange Wear(RolledItem item, int amount) => new([], [], 0, "unsupported");

    /// <summary>
    /// What one world container (a supply crate, a locker) gives <paramref name="opener"/> (contract 1.1).
    /// Pure in its inputs, so the host replays it from the instance seed as it replays kills; each
    /// player opens each container once and gets their own roll. The default is nothing.
    /// </summary>
    IReadOnlyList<RolledItem> ContainerDrops(ulong instanceSeed, uint containerSeq, int depth, CharacterSheet opener) => [];

    /// <summary>
    /// A kill's Australium pile, divided when it is picked up (contract 1.2): one share per member of
    /// <paramref name="killerFirst"/> (the killer's party on the level, killer first when there), in
    /// that order. The split (total / members, the remainder one apiece in order) plus each member's
    /// found share (Scavenger: new Australium beyond the split); the host refuses a share below 0 or
    /// above <paramref name="total"/>. The default gives every member the whole roll (the 2.7 host).
    /// </summary>
    IReadOnlyList<long> AustraliumShares(long total, IReadOnlyList<CharacterSheet> killerFirst) =>
        killerFirst.Select(_ => total).ToList();

    /// <summary>
    /// One breakable (a crate, a barrel) broken by <paramref name="opener"/> (contract 1.2), numbered
    /// apart from the containers. Pure, replayed from the instance seed; every hero in the room breaks it
    /// for themselves and gets their own roll: Australium credited to them, items to the floor for them.
    /// A bad outcome (a blast, a cloud, an ambush) is the game's and is the empty roll here. The
    /// default is the empty roll.
    /// </summary>
    BreakableRoll BreakableDrops(ulong instanceSeed, uint breakableSeq, int depth, CharacterSheet opener) => new(0, []);

    /// <summary>
    /// What a new character starts with (contract 1.1), minted at creation in the same transaction
    /// into the named container and slot (the Descent classes: potions on the belt). Every item must
    /// replay (<see cref="Replays"/>), as any carried item must at a checkpoint. The default is nothing.
    /// </summary>
    IReadOnlyList<StartingItem> StartingKit(string className, CharacterSheet sheet, ulong seed) => [];

    /// <summary>
    /// Using up <paramref name="count"/> of an item (contract 1.1): a potion drunk, a tome read. Either
    /// <c>Consumed = [0]</c> (the item is gone) or <c>Produced = [the stack with its lower count]</c>,
    /// re-encoded, in place; nothing costs. The effect is the game's. The default consumes a whole item
    /// and refuses a partial stack as <c>unsupported</c> (the host cannot re-encode a 1.0 module's item).
    /// </summary>
    ItemChange Consume(CharacterSheet sheet, RolledItem item, int count) =>
        count < 1 || count > item.Count ? new([], [], 0, "bad_count")
        : count == item.Count ? new([0], [], 0)
        : new([], [], 0, "unsupported");

    /// <summary>
    /// Two stacks of one kind become one (contract 1.2): <paramref name="source"/>'s count goes into
    /// <paramref name="target"/>, up to the rules' stack limit. Inputs are [source, target]. <c>Produced[0]</c>
    /// is the target with its new count; <c>Produced[1]</c> is what is left of the source, or
    /// <c>Consumed = [0]</c> when the source is used up. Both re-encoded, in place; nothing costs. The
    /// host refuses a merge whose counts are not conserved. The default refuses: the host cannot
    /// re-encode a 1.x module's items.
    /// </summary>
    ItemChange Merge(RolledItem source, RolledItem target) => new([], [], 0, "unsupported");

    /// <summary>
    /// The named equip slots (contract 1.1); an <see cref="ItemContainer.Equipped"/> item's slot is its
    /// index here. The default is none: a 1.0 module has only the backpack.
    /// </summary>
    IReadOnlyList<string> EquipSlots => [];

    /// <summary>How many slots <paramref name="container"/> has (contract 1.1). The default is 1.0's backpack and nothing else.</summary>
    int ContainerSlots(CharacterSheet sheet, ItemContainer container) =>
        container == ItemContainer.Backpack ? BackpackSlots(sheet) : 0;

    /// <summary>
    /// Why <paramref name="item"/> may not sit in <paramref name="slot"/> of <paramref name="container"/>
    /// (e.g. <c>wrong_slot</c>, <c>class_restricted</c>, <c>potions_only</c>), or null when it may
    /// (contract 1.1). The host has already checked the slot is in range and free. The default allows.
    /// </summary>
    string? PlaceRefusal(CharacterSheet sheet, RolledItem item, ItemContainer container, int slot) => null;

    /// <summary>The codecs of the opaque payloads (§6): the host decodes with the very code that encoded.</summary>
    ISheetCodec Sheets { get; }
    IItemCodec Items { get; }
}

public enum PriceKind { Buy, Sell, VendorValue }

/// <summary>
/// Where a character's item sits (contract 1.1): numbered backpack cells, the named equip slots
/// (<c>slot</c> indexes <see cref="IGameRules.EquipSlots"/>) or the numbered belt.
/// </summary>
public enum ItemContainer { Backpack = 0, Equipped = 1, Belt = 2 }

/// <summary>Town: at the town's repairer, hub only, lossless, costs Australium. Field: with a kit, anywhere.</summary>
public enum RepairKind { Town = 0, Field = 1 }

/// <summary>What one hero gets from one breakable (<see cref="IGameRules.BreakableDrops"/>).</summary>
public sealed record BreakableRoll(long Australium, IReadOnlyList<RolledItem> Items);

/// <summary>One item of a <see cref="IGameRules.StartingKit"/>: where it goes, in which slot.</summary>
public sealed record StartingItem(RolledItem Item, ItemContainer Container, int Slot);

public sealed record ItemRollContext(
    string CharacterClass,
    int CharacterLevel,
    int Depth,
    int ItemLevel,
    int Tier,
    string LootTable,
    string Source);

/// <summary>An item as the rules see it. <see cref="Instance"/> is the rules' own encoding.</summary>
public sealed record RolledItem(
    ulong Seed,
    string BaseType,
    int Rarity,
    int ItemLevel,
    int Count,
    bool Identified,
    byte[] Instance,
    int SchemaVersion,
    int Tier,
    ItemRollContext Context);

public readonly record struct KillRoll(bool Drop, int Tier, long Australium);

public sealed record CharacterSheet(byte[] Data, int SchemaVersion);

/// <summary>What the host keeps in columns; everything else stays opaque.</summary>
public sealed record SheetSummary(string ClassName, string Name, int Level, long Xp, int ReachedDepth, bool Hardcore, bool Fallen);

public sealed record CheckpointEvidence(byte[] Data, int SchemaVersion);

public sealed record ValidationResult(bool Ok, string? FailedRule = null)
{
    public static readonly ValidationResult Pass = new(true);
    public static ValidationResult Fail(string rule) => new(false, rule);
}

public sealed record TradeState(
    CharacterSheet A, CharacterSheet B,
    IReadOnlyList<RolledItem> OfferA, IReadOnlyList<RolledItem> OfferB,
    long AustraliumA, long AustraliumB,
    int FreeSlotsA, int FreeSlotsB);

public sealed record TradeResult(bool Ok, string? Reason = null);

/// <summary>An item operation's result: items consumed (by index into the inputs), items produced, Australium cost.</summary>
public sealed record ItemChange(IReadOnlyList<int> Consumed, IReadOnlyList<RolledItem> Produced, long Cost, string? Refusal = null)
{
    public bool Ok => Refusal is null;
}

public sealed record RoomInfo(string Name, IReadOnlyList<string> Tags, int Weight, int MinDepth, int MaxDepth);

public sealed record LayoutKey(string Library, int Depth, int Difficulty, ulong Seed, IReadOnlyList<RoomInfo> Rooms);

public sealed record Placement(string Room, int Row, int Column, int Rotation, string Role);

public sealed record LevelPlan(int Rows, int Columns, IReadOnlyList<Placement> Placements, string UpRoom, string DownRoom);

public sealed record LevelRow(int Depth, int ItemLevel, string LootTable, int MonsterLevel);

public interface ISheetCodec
{
    int SchemaVersion { get; }
    SheetSummary Summarize(CharacterSheet sheet);
    string ToJson(CharacterSheet sheet);
    CharacterSheet FromJson(string json);
}

/// <summary>
/// The rules' own item encoding. <see cref="RolledItem.Instance"/> carries everything the
/// rules need, including the roll context, so the host can rebuild a <see cref="RolledItem"/>
/// from the ledger's opaque bytes and replay it (<see cref="IGameRules.Replays"/>).
/// </summary>
public interface IItemCodec
{
    int SchemaVersion { get; }
    RolledItem Decode(byte[] instance, int schemaVersion);
    string ToJson(RolledItem item);
    RolledItem FromJson(string json);
}
