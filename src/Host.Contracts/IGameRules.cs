namespace SourceSharp.Host.Contracts;

/// <summary>
/// The mod's rules, as the host needs them (plan §1.2, D-H9). Implemented by the mod's
/// rules module (Descent.Rules in the sharp repo), which the host never compiles against:
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

    /// <summary>The codecs of the opaque payloads (§6): the host decodes with the very code that encoded.</summary>
    ISheetCodec Sheets { get; }
    IItemCodec Items { get; }
}

public enum PriceKind { Buy, Sell, VendorValue }

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
