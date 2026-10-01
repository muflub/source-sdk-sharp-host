using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SourceSharp.Host.Contracts;

namespace SourceSharp.Host.Testing;

/// <summary>
/// A deterministic IGameRules for the unit tier (CLAUDE.md: tests use FakeGameRules; nothing
/// here compiles against a Descent assembly). Small, pure and readable, so a fact can predict
/// every roll: KillRoll and RollItem are hashes of their inputs.
/// </summary>
public sealed class FakeGameRules : IGameRules
{
    public static readonly string[] TierNames = ["Stock", "Vintage", "Strange", "Unusual", "Australium"];
    static readonly string[] Bases = ["scattergun", "rocket_launcher", "minigun", "wrench", "sniper_rifle", "knife", "medigun", "flamethrower"];

    /// <summary>Added to every kill roll's hash: two fakes with different salts replay differently (the D-H9 fixture pair).</summary>
    public ulong Salt { get; init; }
    public int Backpack { get; init; } = 20;
    public int Stash { get; init; } = 50;

    public IReadOnlyList<string> Tiers => TierNames;
    public ISheetCodec Sheets { get; } = new SheetCodec();
    public IItemCodec Items { get; } = new ItemCodec();

    public static ulong Hash(params object[] parts)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', parts)));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    public RolledItem RollItem(ItemRollContext ctx, ulong seed)
    {
        var h = Hash("item", seed, ctx.CharacterClass, ctx.CharacterLevel, ctx.Depth, ctx.ItemLevel, ctx.Tier, ctx.LootTable);
        var baseType = Bases[(int)(h % (ulong)Bases.Length)];
        var identified = ctx.Tier < 2; // Strange and above drop unidentified (D22)
        var item = new RolledItem(seed, baseType, ctx.Tier, ctx.ItemLevel, 1, identified, [], ItemCodec.Schema, ctx.Tier, ctx);
        return item with { Instance = ItemCodec.Encode(item) };
    }

    public bool Replays(RolledItem item)
    {
        var again = RollItem(item.Context, item.Seed);
        return again.BaseType == item.BaseType && again.Rarity == item.Rarity && again.ItemLevel == item.ItemLevel
               && again.Tier == item.Tier && again.Count == item.Count;
    }

    /// <summary>Two kills in three drop; tiers by the hash: 60 % Stock, 20 % Vintage, 10 % Strange, 10 % Unusual.</summary>
    public KillRoll KillRoll(ulong instanceSeed, uint killSeq, string robotTemplate, int depth, int partySize)
    {
        var h = Hash("kill", instanceSeed, killSeq, robotTemplate, depth, partySize) + Salt;
        var drop = h % 3 != 0;
        var t = (int)(h / 3 % 10);
        var tier = t < 6 ? 0 : t < 8 ? 1 : t < 9 ? 2 : 3;
        return new KillRoll(drop, drop ? tier : -1, depth);
    }

    public IReadOnlyList<RolledItem> BossDrops(ItemRollContext ctx, string robotTemplate, ulong seed) =>
        [RollItem(ctx with { Tier = 2, Source = "boss:" + robotTemplate }, seed), RollItem(ctx with { Tier = 3, Source = "boss:" + robotTemplate }, seed + 1)];

    public int BackpackSlots(CharacterSheet sheet) => Backpack;
    public int StashSlots(IReadOnlyList<CharacterSheet> living) => Stash;

    public CharacterSheet NewCharacter(string className, string name, bool hardcore) =>
        SheetCodec.Write(new Sheet(className, name, 1, 0, 0, hardcore, false));

    public ValidationResult ValidateCheckpoint(CharacterSheet before, CharacterSheet after, CheckpointEvidence evidence)
    {
        var a = SheetCodec.Read(before);
        var b = SheetCodec.Read(after);
        if (b.ClassName != a.ClassName) return ValidationResult.Fail("class_changed");
        if (b.Level < a.Level) return ValidationResult.Fail("level_decreased");
        if (b.Xp < a.Xp) return ValidationResult.Fail("xp_decreased");
        if (b.Level - a.Level > 5) return ValidationResult.Fail("level_jump");
        if (a.Fallen && !b.Fallen) return ValidationResult.Fail("unfallen");
        return ValidationResult.Pass;
    }

    public TradeResult ApplyTrade(TradeState t) =>
        t.OfferB.Count - t.OfferA.Count > t.FreeSlotsA ? new TradeResult(false, "no_capacity_a")
        : t.OfferA.Count - t.OfferB.Count > t.FreeSlotsB ? new TradeResult(false, "no_capacity_b")
        : new TradeResult(true);

    public LevelPlan GenerateLayout(LayoutKey key)
    {
        var rooms = key.Rooms.Where(r => key.Depth >= r.MinDepth && key.Depth <= r.MaxDepth).ToList();
        if (rooms.Count == 0) rooms = [.. key.Rooms];
        var up = rooms.FirstOrDefault(r => r.Tags.Contains("start"))?.Name ?? rooms[0].Name;
        var down = rooms.FirstOrDefault(r => r.Tags.Contains("stairs"))?.Name ?? rooms[^1].Name;
        var placements = new List<Placement> { new(up, 0, 0, 0, "up") };
        var fill = rooms.Where(r => r.Name != up && r.Name != down).ToList();
        if (fill.Count > 0)
            placements.Add(new Placement(fill[(int)(Hash("layout", key.Seed, key.Depth) % (ulong)fill.Count)].Name, 0, 1, 90, "fill"));
        placements.Add(new Placement(down, 0, placements.Count, 0, "down"));
        return new LevelPlan(1, placements.Count, placements, up, down);
    }

    public LevelRow LevelTable(int depth) => new(depth, Math.Max(1, depth * 5), $"depth{depth}", depth * 3);

    public IReadOnlyList<RolledItem> VendorStock(string vendor, ulong seed) =>
        Enumerable.Range(0, 6).Select(i => RollItem(new ItemRollContext("any", 1, 0, 5, i % 2, "vendor:" + vendor, "vendor"), seed + (ulong)i)).ToList();

    public long Price(RolledItem item, PriceKind kind)
    {
        var buy = 10L * (item.Rarity + 1) * Math.Max(1, item.ItemLevel) * item.Count;
        return kind switch { PriceKind.Buy => buy, PriceKind.Sell => buy / 4, _ => buy / 2 };
    }

    public ItemChange OpenCrate(RolledItem crate, ItemRollContext ctx, ulong seed) =>
        new([0], [Re(RollItem(ctx with { Tier = Math.Min(3, crate.Rarity + 1), Source = "crate" }, seed) with { Identified = false })], 0);

    public ItemChange Craft(string recipe, IReadOnlyList<RolledItem> inputs, ItemRollContext ctx, ulong seed) =>
        recipe != "combine3" ? new([], [], 0, "unknown_recipe")
        : inputs.Count != 3 ? new([], [], 0, "needs_three")
        : new([0, 1, 2], [RollItem(ctx with { Tier = Math.Min(3, inputs.Max(i => i.Rarity) + 1), Source = "craft" }, seed)], 5);

    public ItemChange Identify(RolledItem item, ulong seed) =>
        item.Identified ? new([], [], 0, "already_identified") : new([0], [Re(item with { Identified = true })], 2);

    public ItemChange Repair(RolledItem item) => new([0], [item], 1);

    public ItemChange Salvage(RolledItem item, ItemRollContext ctx, ulong seed) =>
        new([0], [Re(RollItem(ctx with { Tier = 0, Source = "salvage" }, seed) with { BaseType = "metal", Count = item.Rarity + 1 })], 0);

    /// <summary>An item changed with `with` carries its old bytes until it is encoded again.</summary>
    static RolledItem Re(RolledItem i) => i with { Instance = ItemCodec.Encode(i) };

    public sealed record Sheet(string ClassName, string Name, int Level, long Xp, int ReachedDepth, bool Hardcore, bool Fallen);

    public sealed class SheetCodec : ISheetCodec
    {
        public const int Schema = 1;
        public int SchemaVersion => Schema;
        public static CharacterSheet Write(Sheet s) => new(JsonSerializer.SerializeToUtf8Bytes(s), Schema);
        public static Sheet Read(CharacterSheet s) => JsonSerializer.Deserialize<Sheet>(s.Data) ?? throw new InvalidDataException("empty sheet");
        public SheetSummary Summarize(CharacterSheet sheet)
        {
            var s = Read(sheet);
            return new SheetSummary(s.ClassName, s.Name, s.Level, s.Xp, s.ReachedDepth, s.Hardcore, s.Fallen);
        }
        public string ToJson(CharacterSheet sheet) => Encoding.UTF8.GetString(sheet.Data);
        public CharacterSheet FromJson(string json) => Write(JsonSerializer.Deserialize<Sheet>(json) ?? throw new InvalidDataException("empty sheet"));
    }

    public sealed class ItemCodec : IItemCodec
    {
        public const int Schema = 1;
        public int SchemaVersion => Schema;

        sealed record Wire(ulong Seed, string BaseType, int Rarity, int ItemLevel, int Count, bool Identified, int Tier, ItemRollContext Context);

        public static byte[] Encode(RolledItem i) =>
            JsonSerializer.SerializeToUtf8Bytes(new Wire(i.Seed, i.BaseType, i.Rarity, i.ItemLevel, i.Count, i.Identified, i.Tier, i.Context));

        public RolledItem Decode(byte[] instance, int schemaVersion)
        {
            var w = JsonSerializer.Deserialize<Wire>(instance) ?? throw new InvalidDataException("empty item");
            return new RolledItem(w.Seed, w.BaseType, w.Rarity, w.ItemLevel, w.Count, w.Identified, instance, schemaVersion, w.Tier, w.Context);
        }

        public string ToJson(RolledItem item) => Encoding.UTF8.GetString(Encode(item));
        public RolledItem FromJson(string json) => Decode(Encoding.UTF8.GetBytes(json), Schema);
    }
}
